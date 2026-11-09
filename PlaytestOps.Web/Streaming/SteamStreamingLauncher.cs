using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PlaytestOps.Sandboxie;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BackendChecks")]

namespace PlaytestOps.Web.Streaming;

public sealed record SteamExecutionEnvironment(string Name, bool Available, string? Reason = null);
public sealed record SteamEnvironmentInspection(IReadOnlyList<SteamExecutionEnvironment> Environments, bool Available, string? Reason = null);

/// <summary>Keeps a known child reserved when startup cleanup cannot confirm its exit.</summary>
public sealed class SteamLaunchCleanupException(string message, IStreamingOwnedProcess ownedProcess, Exception innerException)
    : InvalidOperationException(message, innerException)
{
    public IStreamingOwnedProcess OwnedProcess { get; } = ownedProcess;
}

/// <summary>Launches approved players beside existing, user-signed-in Steam clients.</summary>
public sealed class SteamStreamingLauncher(IConfiguration configuration) : ISteamStreamingLauncher, IDisposable
{
    private readonly object nativeGate = new();
    private readonly SemaphoreSlim launchGate = new(1, 1);
    private SandboxieClient? native;
    private HostSessionClient? sessions;
    private bool disposed;

    public SteamEnvironmentInspection InspectEnvironments()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return new([], false, "Steam sessions require a Windows x64 execution host.");
        try
        {
            var client = GetClient();
            var environments = new List<SteamExecutionEnvironment>();
            bool hostAvailable = HasSteam(client, HostSessionClient.Host);
            foreach (var box in client.GetBoxes().Where(box => box != HostSessionClient.Host))
            {
                bool available = HasSteam(client, box);
                environments.Add(new(box, available, available ? null : "Start Steam and sign in in this environment."));
            }
            bool ready = hostAvailable && environments.Any(environment => environment.Available);
            return new(environments, ready, ready ? null : "Start Steam in the normal host and at least one configured sandbox.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new([], false, "Steam environments could not be inspected. Check the execution host's Steam and Sandboxie paths.");
        }
    }

    public Task ValidateEnvironmentsAsync(IReadOnlyList<string> sandboxNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sandboxNames);
        cancellationToken.ThrowIfCancellationRequested();
        if (sandboxNames.Count is < 1 or > 3 || sandboxNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sandboxNames.Count)
            throw new ArgumentException("Select one to three distinct Sandboxie environments beside normal Steam.", nameof(sandboxNames));
        foreach (var box in sandboxNames) SandboxieClient.ValidateBoxName(box);
        var client = GetClient();
        if (!HasSteam(client, HostSessionClient.Host))
            throw new InvalidOperationException("Start normal Steam and sign in before starting the Steam playtest.");
        var configured = client.GetBoxes();
        foreach (var box in sandboxNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!configured.Contains(box, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("A selected Sandboxie environment is unavailable.");
            if (!HasSteam(client, box))
                throw new InvalidOperationException($"Start Steam and sign in in sandbox {box} before starting the Steam playtest.");
        }
        return Task.CompletedTask;
    }

    public async Task<IStreamingOwnedProcess> LaunchAsync(string executablePath, string workingDirectory,
        string storagePath, string? sandboxName, IReadOnlyList<string> arguments,
        string streamConfigJson, string steamConfigJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(arguments);
        CheckExecutable(executablePath, workingDirectory);
        if (arguments.Any(argument => argument is null || argument.Contains('\0') ||
            argument == "--playtestops-stream-config-file" || argument == "--playtestops-steam-config-file"))
            throw new ArgumentException("The Steam player arguments contain an invalid or duplicate configuration option.", nameof(arguments));
        string box = sandboxName ?? HostSessionClient.Host;
        if (box != HostSessionClient.Host) SandboxieClient.ValidateBoxName(box);

        await launchGate.WaitAsync(cancellationToken);
        PrivateLaunchFiles? files = null;
        Process? observed = null;
        BoxProcess? identity = null;
        var baseline = new HashSet<(int Pid, long CreationTime)>();
        bool attempted = false;
        try
        {
            var client = GetClient();
            if (!HasSteam(client, box))
                throw new InvalidOperationException("Steam is no longer running in the selected environment.");
            foreach (var existing in client.GetProcesses(box))
            {
                baseline.Add((existing.Pid, existing.CreationTime));
                using var matching = OpenMatchingProcess(client, existing, executablePath);
                if (matching is not null)
                    throw new InvalidOperationException("The approved Steam player is already running in the selected environment.");
            }
            files = new PrivateLaunchFiles(storagePath, streamConfigJson, steamConfigJson);
            var launchArguments = arguments.Concat(new[] { "--playtestops-stream-config-file", files.StreamPath,
                "--playtestops-steam-config-file", files.SteamPath }).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            if (box == HostSessionClient.Host)
            {
                var start = new ProcessStartInfo(executablePath)
                {
                    WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var argument in launchArguments) start.ArgumentList.Add(argument);
                foreach (var variable in new[] { "PLAYTESTOPS_STREAM_CONFIG", "PLAYTESTOPS_ARENA_CONFIG", "PLAYTESTOPS_STEAM_CONFIG" })
                    start.Environment.Remove(variable);
                observed = Process.Start(start) ?? throw new InvalidOperationException("The approved Steam player did not start.");
                _ = observed.SafeHandle;
                identity = await WaitForHostIdentityAsync(client, observed, executablePath, cancellationToken);
            }
            else
            {
                attempted = true;
                await client.LaunchAsync(box, executablePath, workingDirectory, launchArguments, cancellationToken);
                (identity, observed) = await ObserveNewPlayerAsync(client, box, executablePath, baseline,
                    TimeSpan.FromSeconds(15), cancellationToken);
                if (identity is null || observed is null)
                    throw new TimeoutException("The approved Steam player was not observed in its sandbox before the launch deadline.");
            }
            var launchFiles = files;
            var ownedIdentity = identity;
            var result = new StreamingOwnedProcess(observed, () => client.StopProcess(ownedIdentity), launchFiles.Dispose,
                standardOutput: box == HostSessionClient.Host ? observed.StandardOutput : null,
                standardError: box == HostSessionClient.Host ? observed.StandardError : null);
            files = null;
            observed = null;
            return result;
        }
        catch (Exception launchError)
        {
            try
            {
                if (identity is not null || attempted)
                {
                    var client = GetClient();
                    if (identity is null)
                        (identity, observed) = await ObserveNewPlayerAsync(client, box, executablePath, baseline,
                            TimeSpan.FromSeconds(3), CancellationToken.None);
                    if (identity is not null) client.StopProcess(identity);
                    if (observed is not null)
                        await observed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                else if (observed is not null)
                {
                    // A direct child handle remains authoritative if its initial native
                    // identity check failed. Never adopt a replacement found by PID here.
                    if (!observed.HasExited) observed.Kill(entireProcessTree: false);
                    await observed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
            }
            catch (Exception cleanupError)
            {
                if (observed is not null)
                {
                    var retainedFiles = files;
                    var retainedIdentity = identity;
                    Action? terminate = retainedIdentity is null ? null : () => GetClient().StopProcess(retainedIdentity);
                    var retained = new StreamingOwnedProcess(observed, terminate, () => retainedFiles?.Dispose(),
                        standardOutput: box == HostSessionClient.Host ? observed.StandardOutput : null,
                        standardError: box == HostSessionClient.Host ? observed.StandardError : null);
                    observed = null;
                    files = null;
                    throw new SteamLaunchCleanupException("Steam startup failed and the owned player is still awaiting confirmed exit.", retained,
                        new AggregateException(launchError, cleanupError));
                }
                throw new InvalidOperationException("Steam startup failed and cleanup could not confirm the new player's exit.",
                    new AggregateException(launchError, cleanupError));
            }
            throw;
        }
        finally
        {
            observed?.Dispose();
            files?.Dispose();
            launchGate.Release();
        }
    }

    internal static async Task<BoxProcess> WaitForHostIdentityAsync(ISandboxieClient client, Process heldProcess,
        string executable, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        var deadline = timeout ?? TimeSpan.FromSeconds(5);
        long creationTime = heldProcess.StartTime.ToUniversalTime().ToFileTimeUtc();
        using var current = Process.GetCurrentProcess();
        uint session = checked((uint)current.SessionId);
        Exception? imageFailure = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (heldProcess.HasExited)
                throw new InvalidOperationException("The normal Steam player exited before its process identity became available.");
            BoxProcess? identity = null;
            try { identity = client.GetProcess(heldProcess.Id); }
            catch (Exception exception) when (exception is Win32Exception ||
                exception is InvalidOperationException { Message: "Process image is unavailable." })
            {
                // MainModule can be unavailable before Windows initializes the loader.
                // Retry only metadata for our held child. Native driver failures propagate.
                imageFailure = exception;
            }
            if (identity is not null)
            {
                if (identity.Pid != heldProcess.Id || identity.BoxName != HostSessionClient.Host ||
                    identity.SessionId != session || identity.CreationTime != creationTime ||
                    !string.Equals(identity.ImageName, executable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The normal Steam player's process identity did not match the approved launch.");
                return identity;
            }
            if (timer.Elapsed >= deadline)
                throw new InvalidOperationException("The normal Steam player's process image was unavailable before the identity deadline.", imageFailure);
            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task<(BoxProcess? Identity, Process? Process)> ObserveNewPlayerAsync(ISandboxieClient client,
        string box, string executable, HashSet<(int Pid, long CreationTime)> baseline,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BoxProcess? identity = null;
            Process? selected = null;
            try
            {
                foreach (var process in client.GetProcesses(box))
                {
                    if (baseline.Contains((process.Pid, process.CreationTime))) continue;
                    var matching = OpenMatchingProcess(client, process, executable);
                    if (matching is null) continue;
                    if (selected is not null)
                    {
                        matching.Dispose();
                        throw new InvalidOperationException("Multiple approved players appeared in the sandbox. Process ownership is ambiguous.");
                    }
                    selected = matching;
                    identity = process;
                }
                if (selected is not null) return (identity, selected);
            }
            catch { selected?.Dispose(); throw; }
            if (timer.Elapsed >= timeout) return (null, null);
            await Task.Delay(100, cancellationToken);
        }
    }

    private bool HasSteam(ISandboxieClient client, string box)
    {
        string executable = SteamPath();
        foreach (var process in client.GetProcesses(box))
        {
            using var matching = OpenMatchingProcess(client, process, executable);
            if (matching is not null) return true;
        }
        return false;
    }

    private static Process? OpenMatchingProcess(ISandboxieClient client, BoxProcess identity, string executable)
    {
        if (!string.Equals(Path.GetFileName(identity.ImageName), Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase))
            return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.Pid);
            _ = process.SafeHandle;
            if (process.HasExited || process.StartTime.ToUniversalTime().ToFileTimeUtc() != identity.CreationTime ||
                !string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase) ||
                !Equals(client.GetProcess(identity.Pid), identity))
            {
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (ArgumentException) { process?.Dispose(); return null; }
        catch { process?.Dispose(); throw; }
    }

    private HostSessionClient GetClient()
    {
        lock (nativeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw new InvalidOperationException("Steam sessions require a Windows x64 execution host.");
            if (sessions is not null) return sessions;
            string? configured = configuration["SteamSessions:SandboxieStartPath"];
            string[] candidates = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie", "Start.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sandboxie-Plus", "Start.exe")];
            string start = string.IsNullOrWhiteSpace(configured) ? candidates.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Sandboxie Start.exe is unavailable.") : configured;
            try { native = new SandboxieClient(start); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                throw new InvalidOperationException("Sandboxie's native library could not be loaded. Check its installation and x64 architecture.", exception);
            }
            sessions = new HostSessionClient(native);
            return sessions;
        }
    }

    private string SteamPath()
    {
        string? configured = configuration["SteamSessions:SteamExecutablePath"];
        string executable = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe") : configured;
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !Path.GetFileName(executable).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Configure the existing absolute Steam executable path.");
        return Path.GetFullPath(executable);
    }

    private static void CheckExecutable(string executable, string workingDirectory)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new ArgumentException("Steam launch requires the approved absolute executable and working directory.");
    }

    public void Dispose()
    {
        lock (nativeGate)
        {
            if (disposed) return;
            disposed = true;
            native?.Dispose();
        }
    }

    internal sealed class PrivateLaunchFiles : IDisposable
    {
        public string StreamPath { get; }
        public string SteamPath { get; }

        public PrivateLaunchFiles(string storagePath, string streamJson, string steamJson,
            Action<string, string>? writeFile = null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Private Steam configuration requires Windows.");
            if (!Path.IsPathFullyQualified(storagePath)) throw new InvalidDataException("Steam instance storage must be absolute.");
            ValidateJson(streamJson);
            ValidateJson(steamJson);
            for (var ancestor = Path.GetFullPath(storagePath); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Steam configuration storage must use ordinary local directories.");
            using var windowsIdentity = WindowsIdentity.GetCurrent();
            var owner = windowsIdentity.User ?? throw new InvalidOperationException("The execution host has no Windows user identity.");
            var permissions = new DirectorySecurity();
            permissions.SetOwner(owner);
            permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var principal in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                permissions.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            string directory = Path.Combine(storagePath, "Launch-" + Guid.NewGuid().ToString("N"));
            new DirectoryInfo(directory).Create(permissions);
            StreamPath = Path.Combine(directory, "stream.json");
            SteamPath = Path.Combine(directory, "steam.json");
            try
            {
                writeFile ??= WriteNew;
                writeFile(StreamPath, streamJson);
                writeFile(SteamPath, steamJson);
            }
            catch { Dispose(); throw; }
        }

        private static void ValidateJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 8192)
                throw new InvalidDataException("Steam launch configuration must be a bounded JSON object.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Steam launch configuration must be a JSON object.");
        }

        private static void WriteNew(string path, string json)
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            writer.Write(json);
        }

        public void Dispose()
        {
            foreach (var file in new[] { StreamPath, SteamPath })
                try { File.Delete(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
