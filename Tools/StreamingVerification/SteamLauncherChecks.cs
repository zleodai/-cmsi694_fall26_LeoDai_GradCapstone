using System.Security.AccessControl;
using System.Security.Principal;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using PlaytestOps.Sandboxie;
using PlaytestOps.Web.Streaming;

internal static class SteamLauncherChecks
{
    public static async Task Run(string scratch, Action<bool, string> check)
    {
        var storage = Path.Combine(scratch, "steam-launcher");
        Directory.CreateDirectory(storage);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SteamSessions:SandboxieStartPath"] = Path.Combine(storage, "not-installed", "Start.exe"),
            ["SteamSessions:SteamExecutablePath"] = Path.Combine(storage, "not-installed", "steam.exe")
        }).Build();
        using var launcher = new SteamStreamingLauncher(configuration);
        var inspection = launcher.InspectEnvironments();
        check(!inspection.Available && inspection.Environments.Count == 0 && !string.IsNullOrWhiteSpace(inspection.Reason),
            "Steam inspection reports an unavailable installation without starting applications");

        foreach (var names in new string[][]
        {
            [], ["@host"], ["DefaultBox", "@host"], ["@HOST"], ["DefaultBox", "defaultbox"],
            [null!], [""], [" "], ["*"], ["Box/Other"], [new string('x', 39)], ["A", "B", "C", "D"]
        })
        {
            try
            {
                await launcher.ValidateEnvironmentsAsync(names, CancellationToken.None);
                check(false, "invalid Steam environment selection is rejected before native inspection");
            }
            catch (ArgumentException)
            {
                check(true, "invalid Steam environment selection is rejected before native inspection");
            }
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await launcher.ValidateEnvironmentsAsync(["DefaultBox"], cancelled.Token);
                check(false, "cancelled Steam preflight stops before native inspection");
            }
            catch (OperationCanceledException)
            {
                check(true, "cancelled Steam preflight stops before native inspection");
            }
        }

        var fake = new RetainedProcess();
        var cause = new IOException("Synthetic cleanup failure.");
        var retainedFailure = new SteamLaunchCleanupException("Synthetic startup failure.", fake, cause);
        check(ReferenceEquals(retainedFailure.OwnedProcess, fake) && ReferenceEquals(retainedFailure.InnerException, cause),
            "failed Steam cleanup preserves an owned process for supervisor reservation");

        using (var held = Process.GetCurrentProcess())
        {
            _ = held.SafeHandle;
            var expected = new BoxProcess(held.Id, HostSessionClient.Host, held.MainModule!.FileName,
                checked((uint)held.SessionId), held.StartTime.ToUniversalTime().ToFileTimeUtc());
            int attempts = 0;
            var delayedImage = new IdentityClient(() => ++attempts switch
            {
                1 => throw new InvalidOperationException("Process image is unavailable."),
                2 => throw new Win32Exception(299),
                _ => expected
            });
            var verified = await SteamStreamingLauncher.WaitForHostIdentityAsync(delayedImage, held, expected.ImageName, CancellationToken.None);
            check(verified == expected && attempts == 3, "held host identity tolerates early loader metadata unavailability");

            var nativeFailure = new IdentityClient(() => throw new InvalidOperationException("Sandboxie NTSTATUS 0xC0000001."));
            try
            {
                await SteamStreamingLauncher.WaitForHostIdentityAsync(nativeFailure, held, expected.ImageName, CancellationToken.None);
                check(false, "host identity wait propagates native driver failures");
            }
            catch (InvalidOperationException exception)
            {
                check(exception.Message.StartsWith("Sandboxie NTSTATUS"), "host identity wait propagates native driver failures");
            }
            foreach (var changed in new[] { expected with { CreationTime = expected.CreationTime + 1 },
                expected with { BoxName = "DefaultBox" }, expected with { ImageName = "unrelated.exe" } })
            {
                try
                {
                    await SteamStreamingLauncher.WaitForHostIdentityAsync(new IdentityClient(() => changed), held, expected.ImageName, CancellationToken.None);
                    check(false, "host identity wait rejects changed creation, box, or executable");
                }
                catch (InvalidOperationException)
                {
                    check(true, "host identity wait rejects changed creation, box, or executable");
                }
            }
            var unavailableImage = new IdentityClient(() => throw new InvalidOperationException("Process image is unavailable."));
            try
            {
                await SteamStreamingLauncher.WaitForHostIdentityAsync(unavailableImage, held, expected.ImageName, CancellationToken.None, TimeSpan.Zero);
                check(false, "host loader metadata wait is bounded");
            }
            catch (InvalidOperationException exception)
            {
                check(exception.Message.Contains("deadline"), "host loader metadata wait is bounded");
            }
        }

        if (!OperatingSystem.IsWindows()) return;
        await CheckNativeProcessLifecycle(check);
        const string streamJson = "{\"token\":\"synthetic-only-streaming-secret\"}";
        using (var files = new SteamStreamingLauncher.PrivateLaunchFiles(storage, streamJson, "{}"))
        {
            check(File.ReadAllText(files.StreamPath) == streamJson && File.ReadAllText(files.SteamPath) == "{}",
                "private Steam launch configuration writes the exact per-instance JSON");
            check(!File.ReadAllBytes(files.StreamPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }),
                "Steam launch configuration uses UTF-8 without a byte-order mark");
            var permissions = new DirectoryInfo(Path.GetDirectoryName(files.StreamPath)!).GetAccessControl();
            var rules = permissions.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            using var current = WindowsIdentity.GetCurrent();
            var allowed = new[] { current.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) };
            bool privateDirectory = permissions.AreAccessRulesProtected && rules.Length == 2;
            foreach (var rule in rules)
                privateDirectory &= rule.AccessControlType == AccessControlType.Allow && !rule.IsInherited && allowed.Contains(rule.IdentityReference);
            check(privateDirectory,
                "Steam configuration directory permits only the current Windows user and SYSTEM");
            var streamPath = files.StreamPath;
            var steamPath = files.SteamPath;
            files.Dispose();
            check(!File.Exists(streamPath) && !File.Exists(steamPath), "owned Steam configuration files are removed on disposal");
        }

        string? firstWrittenPath = null;
        try
        {
            _ = new SteamStreamingLauncher.PrivateLaunchFiles(storage, streamJson, "{}", (path, json) =>
            {
                if (firstWrittenPath is not null) throw new IOException("Synthetic second-file failure.");
                File.WriteAllText(path, json, new UTF8Encoding(false));
                firstWrittenPath = path;
            });
            check(false, "second Steam configuration write failure removes the first token file");
        }
        catch (IOException)
        {
            check(firstWrittenPath is not null && !File.Exists(firstWrittenPath),
                "second Steam configuration write failure removes the first token file");
        }
        foreach (var invalidJson in new[] { "[]", "{\"value\":\"" + new string('\u044f', 5000) + "\"}" })
        {
            try
            {
                using var files = new SteamStreamingLauncher.PrivateLaunchFiles(storage, invalidJson, "{}");
                check(false, "Steam config rejects non-object or oversized UTF-8 before writing");
            }
            catch (InvalidDataException)
            {
                check(true, "Steam config rejects non-object or oversized UTF-8 before writing");
            }
        }
    }

    private static async Task CheckNativeProcessLifecycle(Action<bool, string> check)
    {
        var command = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var directStart = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/q", "/c", "echo streaming-wrapper-fixture & exit /b 7" })
            directStart.ArgumentList.Add(argument);
        using (var direct = StreamingOwnedProcess.Start(directStart))
        {
            var output = direct.StandardOutput!.ReadToEndAsync();
            var error = direct.StandardError!.ReadToEndAsync();
            await direct.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            check(direct.HasExited && direct.ExitCode == 7 && (await output).Contains("streaming-wrapper-fixture") && (await error).Length == 0,
                "direct owned native process retains redirected output and its actual exit code");
        }

        var adoptedStart = new ProcessStartInfo(command)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "/d", "/q", "/c", "set /p streamingFixture=" })
            adoptedStart.ArgumentList.Add(argument);
        using var owner = Process.Start(adoptedStart) ?? throw new InvalidOperationException("Native lifecycle fixture did not start.");
        _ = owner.SafeHandle;
        try
        {
            var adopted = Process.GetProcessById(owner.Id);
            int cleanupCalls = 0;
            using var wrapped = new StreamingOwnedProcess(adopted, cleanup: () => cleanupCalls++);
            check(wrapped.Id == owner.Id && wrapped.StandardOutput is null && wrapped.StandardError is null && !wrapped.HasExited,
                "adopted native process exposes nullable streams without querying unavailable StartInfo");
            var exit = wrapped.WaitForExitAsync();
            await owner.StandardInput.WriteLineAsync("fixture-complete");
            owner.StandardInput.Close();
            await exit.WaitAsync(TimeSpan.FromSeconds(5));
            check(wrapped.HasExited && wrapped.ExitCode == 0, "adopted native process wait observes its actual exit and exit code");
            wrapped.Dispose();
            wrapped.Dispose();
            check(cleanupCalls == 1, "adopted process disposal releases session cleanup exactly once");
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class IdentityClient(Func<BoxProcess?> readIdentity) : ISandboxieClient
    {
        public BoxProcess? GetProcess(int pid) => readIdentity();
        public IReadOnlyList<string> GetBoxes() => throw new InvalidOperationException("Unexpected box enumeration.");
        public IReadOnlyList<BoxProcess> GetProcesses(string box) => throw new InvalidOperationException("Unexpected process enumeration.");
        public Task LaunchAsync(string box, string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct) =>
            throw new InvalidOperationException("The identity fixture cannot launch processes.");
        public void StopProcess(BoxProcess expected) => throw new InvalidOperationException("The identity fixture cannot stop processes.");
    }

    private sealed class RetainedProcess : IStreamingOwnedProcess
    {
        public int Id => 123;
        public bool HasExited => false;
        public int? ExitCode => null;
        public StreamReader? StandardOutput => null;
        public StreamReader? StandardError => null;
        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }
}
