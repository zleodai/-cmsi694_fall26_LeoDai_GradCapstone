using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace PlaytestOps.Sandboxie;

// Opt-in composite: Sandboxie remains responsible for every actual box.
public sealed class HostSessionClient(ISandboxieClient sandboxie) : ISandboxieClient {
    public const string Host = "@host";
    public IReadOnlyList<string> GetBoxes() => sandboxie.GetBoxes().Concat([Host]).ToArray();
    public IReadOnlyList<BoxProcess> GetProcesses(string box) {
        if (box != Host) return sandboxie.GetProcesses(box);
        var result = new List<BoxProcess>();
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcesses()) {
            using (process) {
                BoxProcess? identity;
                try {
                    if (process.SessionId != session) continue;
                    identity = ReadHostIdentity(process);
                } catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException) {
                    // Windows system processes may not expose their executable. They cannot be claimed or stopped.
                    continue;
                }
                // A native driver/query failure must fail inventory, not masquerade as an empty host list.
                if (identity is not null && sandboxie.GetProcess(identity.Pid) is null) result.Add(identity);
            }
        }
        return result;
    }
    public BoxProcess? GetProcess(int pid) {
        var boxed = sandboxie.GetProcess(pid);
        if (boxed is not null) return boxed;
        BoxProcess? identity;
        try {
            using var process = Process.GetProcessById(pid);
            identity = ReadHostIdentity(process);
        } catch (ArgumentException) { return null; }
        // Keep native failures outside the OS-process race handling and recheck after the OS snapshot.
        return identity is null ? null : sandboxie.GetProcess(pid) ?? identity;
    }
    private static BoxProcess? ReadHostIdentity(Process process) => process.HasExited ? null : new(process.Id, Host,
        process.MainModule?.FileName ?? throw new InvalidOperationException("Process image is unavailable."),
        checked((uint)process.SessionId), process.StartTime.ToUniversalTime().ToFileTimeUtc());
    public Task LaunchAsync(string box, string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
        if (box != Host) return sandboxie.LaunchAsync(box, executable, workingDirectory, arguments, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !Directory.Exists(workingDirectory))
            throw new ArgumentException("Host launch requires existing absolute executable and working directory paths.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = workingDirectory, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Host process did not start.");
        return Task.CompletedTask;
    }
    public void StopProcess(BoxProcess expected) {
        if (expected.BoxName != Host) { sandboxie.StopProcess(expected); return; }
        if (expected.Pid <= 0 || expected.CreationTime <= 0 || expected.SessionId != (uint)Process.GetCurrentProcess().SessionId)
            throw new InvalidOperationException("Invalid recorded host process identity.");
        var current = GetProcess(expected.Pid);
        if (current is null) return;
        RequireSame(expected, current);
        using var handle = OpenProcess(0x0001 | 0x1000, false, expected.Pid);
        if (handle.IsInvalid) {
            if (GetProcess(expected.Pid) is null) return;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the recorded host game.");
        }
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (creation != expected.CreationTime) throw new InvalidOperationException("Host process creation identity changed.");
        current = GetProcess(expected.Pid);
        if (current is null) return;
        RequireSame(expected, current);
        if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot stop the recorded host game.");
    }
    private static void RequireSame(BoxProcess expected, BoxProcess current) {
        if (expected.Pid != current.Pid || expected.CreationTime != current.CreationTime || expected.SessionId != current.SessionId
            || current.BoxName != Host || !string.Equals(expected.ImageName, current.ImageName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Host process identity changed; refusing termination.");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
}
