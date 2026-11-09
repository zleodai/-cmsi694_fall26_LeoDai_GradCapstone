using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PlaytestOps.Sandboxie;

public sealed record BoxProcess(int Pid, string BoxName, string ImageName, uint SessionId, long CreationTime);

public interface ISandboxieClient
{
    IReadOnlyList<string> GetBoxes();
    IReadOnlyList<BoxProcess> GetProcesses(string box);
    BoxProcess? GetProcess(int pid);
    Task LaunchAsync(string box, string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct);
    void StopProcess(BoxProcess expected);
}

/// <summary>Controls existing boxes without changing their configuration or stored contents.</summary>
public sealed class SandboxieClient : ISandboxieClient, IDisposable
{
    private const int BoxNameCapacity = 40;
    private const int ImageNameCapacity = 1024;
    private const int StatusInvalidCid = unchecked((int)0xC000000B);
    private const int MaximumProcessCapacity = 65536;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private readonly object gate = new();
    private readonly string startPath;
    private readonly uint sessionId;
    private readonly EnumBoxesDelegate enumBoxes;
    private readonly IsBoxEnabledDelegate isBoxEnabled;
    private readonly EnumProcessDelegate enumProcess;
    private readonly QueryProcessDelegate queryProcess;
    private nint library;

    public SandboxieClient(string startPath)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The Sandboxie launcher requires a Windows x64 process and installation.");

        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);
        if (!Path.IsPathFullyQualified(startPath) || !File.Exists(startPath)
            || !string.Equals(Path.GetFileName(startPath), "Start.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Specify the existing absolute Sandboxie Start.exe path.", nameof(startPath));

        this.startPath = Path.GetFullPath(startPath);
        var dllPath = Path.Combine(Path.GetDirectoryName(this.startPath)!, "SbieDll.dll");
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("SbieDll.dll must be beside the configured Start.exe.", dllPath);

        library = NativeLibrary.Load(dllPath);
        try
        {
            enumBoxes = Load<EnumBoxesDelegate>("SbieApi_EnumBoxes");
            isBoxEnabled = Load<IsBoxEnabledDelegate>("SbieApi_IsBoxEnabled");
            enumProcess = Load<EnumProcessDelegate>("SbieApi_EnumProcessEx");
            queryProcess = Load<QueryProcessDelegate>("SbieApi_QueryProcessEx2");
            sessionId = checked((uint)Process.GetCurrentProcess().SessionId);
            EnsureHostCaller();
        }
        catch
        {
            NativeLibrary.Free(library);
            library = 0;
            throw;
        }
    }

    public static void ValidateBoxName(string box)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(box);
        if (box.Length > 38 || box.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("A sandbox name must contain 1 to 38 ASCII letters, digits, or underscores.", nameof(box));
    }

    public IReadOnlyList<string> GetBoxes()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureHostCaller();
            var boxes = new List<string>();
            var index = -1;
            for (var iteration = 0; iteration < MaximumProcessCapacity; iteration++)
            {
                var buffer = new char[BoxNameCapacity];
                var nextIndex = enumBoxes(index, buffer);
                if (nextIndex == -1)
                {
                    // EnumBoxes also uses -1 on underlying failures. Recheck driver access;
                    // launch additionally validates the selected box through IsBoxEnabled.
                    EnsureHostCaller();
                    return boxes.AsReadOnly();
                }

                if (nextIndex <= index)
                    throw new InvalidOperationException("Sandboxie returned an invalid box enumeration cursor.");
                var box = ReadString(buffer);
                ValidateBoxName(box);
                EnsureBoxEnabled(box);
                if (!boxes.Contains(box, StringComparer.OrdinalIgnoreCase))
                    boxes.Add(box);
                index = nextIndex;
            }

            throw new InvalidOperationException("Sandboxie box enumeration exceeded its safety bound.");
        }
    }

    public IReadOnlyList<BoxProcess> GetProcesses(string box)
    {
        ValidateBoxName(box);
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureBoxEnabled(box);
            var capacity = 256;
            while (true)
            {
                var pids = new uint[capacity];
                var count = checked((uint)capacity);
                var status = enumProcess(PaddedBox(box), 0, sessionId, pids, ref count);
                CheckStatus(status, "enumerate sandbox processes");
                if (count > capacity)
                    throw new InvalidOperationException("Sandboxie returned an invalid process count.");
                if (count == capacity)
                {
                    if (capacity >= MaximumProcessCapacity)
                        throw new InvalidOperationException("Sandboxie process enumeration may be truncated.");
                    capacity *= 2;
                    continue;
                }

                var processes = new List<BoxProcess>();
                for (var index = 0; index < count; index++)
                {
                    if (pids[index] == 0 || pids[index] > int.MaxValue)
                        throw new InvalidOperationException("Sandboxie returned an invalid process ID.");
                    var process = GetProcessCore((int)pids[index]);
                    // A process may exit after enumeration. A replacement may have a new box.
                    if (process is not null && process.SessionId == sessionId
                        && string.Equals(process.BoxName, box, StringComparison.OrdinalIgnoreCase))
                        processes.Add(process);
                }
                return processes.AsReadOnly();
            }
        }
    }

    public BoxProcess? GetProcess(int pid)
    {
        if (pid <= 0)
            throw new ArgumentOutOfRangeException(nameof(pid));
        lock (gate)
        {
            ThrowIfDisposed();
            return GetProcessCore(pid);
        }
    }

    /// <summary>Waits for launcher acceptance only; the caller must establish app readiness.</summary>
    public async Task LaunchAsync(string box, string executable, string workingDirectory,
        IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ValidateBoxName(box);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)
            || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Specify an existing absolute executable path.", nameof(executable));
        if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new ArgumentException("Specify an existing absolute working directory.", nameof(workingDirectory));
        if (arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw new ArgumentException("Program arguments cannot be null or contain NUL.", nameof(arguments));

        var startInfo = new ProcessStartInfo(startPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        startInfo.ArgumentList.Add($"/box:{box}");
        startInfo.ArgumentList.Add("/silent");
        startInfo.ArgumentList.Add(executable);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process launcher;
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureBoxEnabled(box);
            ct.ThrowIfCancellationRequested();
            launcher = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Sandboxie Start.exe did not start.");
        }

        using (launcher)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
        {
            try
            {
                await launcher.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stop only our launcher handle. An app may already exist; caller cleanup
                // must inspect the selected box even when launch times out or is cancelled.
                try { if (!launcher.HasExited) launcher.Kill(entireProcessTree: false); }
                catch (InvalidOperationException) { }
                if (!ct.IsCancellationRequested)
                    throw new TimeoutException("Sandboxie launch acceptance exceeded 15 seconds; inspect the box before retrying.");
                throw;
            }

            if (launcher.ExitCode != 0)
                throw new InvalidOperationException($"Sandboxie Start.exe failed with exit code {launcher.ExitCode}.");
        }
    }

    public void StopProcess(BoxProcess expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateBoxName(expected.BoxName);
        if (expected.Pid <= 0 || expected.CreationTime <= 0 || expected.SessionId != sessionId)
            throw new ArgumentException("Only a recorded sandbox process in this Windows session may be stopped.", nameof(expected));

        lock (gate)
        {
            ThrowIfDisposed();
            var current = GetProcessCore(expected.Pid);
            if (current is null)
                return;
            EnsureSameProcess(expected, current);

            using var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, expected.Pid);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (GetProcessCore(expected.Pid) is null)
                    return;
                throw new Win32Exception(error, "Cannot open the recorded sandbox process for termination.");
            }

            if (!GetProcessTimes(handle, out var creationTime, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot validate sandbox process creation time.");
            if (creationTime != expected.CreationTime)
                throw new InvalidOperationException("Process identity changed; refusing termination.");
            current = GetProcessCore(expected.Pid);
            if (current is null)
                return;
            EnsureSameProcess(expected, current);
            // Hold the verified process object, so PID reuse cannot redirect this operation.
            if (!TerminateProcess(handle, 1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot terminate the recorded sandbox process.");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (library != 0)
            {
                NativeLibrary.Free(library);
                library = 0;
            }
        }
    }

    private BoxProcess? GetProcessCore(int pid)
    {
        var box = new char[BoxNameCapacity];
        var image = new char[ImageNameCapacity];
        var status = queryProcess((nint)pid, ImageNameCapacity, box, image, 0,
            out var processSession, out var creationTime);
        if (status == StatusInvalidCid)
            return null;
        CheckStatus(status, "query sandbox process identity");
        var boxName = ReadString(box);
        ValidateBoxName(boxName);
        var imageName = ReadString(image);
        if (imageName.Length == 0 || creationTime <= 0)
            throw new InvalidOperationException("Sandboxie returned incomplete process identity.");
        return new BoxProcess(pid, boxName, imageName, processSession, creationTime);
    }

    private void EnsureHostCaller()
    {
        if (GetProcessCore(Environment.ProcessId) is not null)
            throw new InvalidOperationException("The launcher must run outside Sandboxie.");
    }

    private void EnsureBoxEnabled(string box) => CheckStatus(isBoxEnabled(PaddedBox(box)), "validate the enabled sandbox");

    private static void EnsureSameProcess(BoxProcess expected, BoxProcess current)
    {
        if (expected.Pid != current.Pid || expected.CreationTime != current.CreationTime
            || expected.SessionId != current.SessionId
            || !string.Equals(expected.BoxName, current.BoxName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.ImageName, current.ImageName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Sandbox process identity changed; refusing termination.");
    }

    private static char[] PaddedBox(string box)
    {
        var buffer = new char[BoxNameCapacity];
        box.CopyTo(0, buffer, 0, box.Length);
        return buffer;
    }

    private static string ReadString(char[] buffer)
    {
        var end = Array.IndexOf(buffer, '\0');
        if (end < 0)
            throw new InvalidOperationException("Sandboxie returned an unterminated native string.");
        return new string(buffer, 0, end);
    }

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(library == 0, this);

    private static void CheckStatus(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException($"Cannot {operation}: Sandboxie NTSTATUS 0x{unchecked((uint)status):X8}.");
    }

    // Sandboxie Classic 5.73.5 / Plus v1.18.5: core/dll/sbieapi.h.
    // BOOLEAN is one byte; ULONG/LONG are 32 bits; QueryProcess's HANDLE is a PID.
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int EnumBoxesDelegate(int index, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int IsBoxEnabledDelegate([In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] box);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int EnumProcessDelegate([In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] box,
        byte allSessions, uint session, [Out] uint[] pids, ref uint count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int QueryProcessDelegate(nint pid, uint imageCapacity,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] box,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[] image,
        nint sid, out uint session, out long creationTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
