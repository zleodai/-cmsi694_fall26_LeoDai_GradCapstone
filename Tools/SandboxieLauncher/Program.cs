using System.Text.Json;
using PlaytestOps.Sandboxie;

return await LauncherCli.RunAsync(args);

public static class LauncherCli {
    public static async Task<int> RunAsync(string[] arguments) {
        try {
            if (arguments.Length == 0 || arguments[0] is "--help" or "help") {
                Console.WriteLine("SandboxieLauncher inspect --start <Start.exe>\nSandboxieLauncher dry-run|launch --start <Start.exe> --steam <steam.exe> --game <game.exe> --box <name> [--box <name> ...] [--include-host] --receipt <new.json> [--api <steam_api64.dll>] [--timeout <5..300>]\nSandboxieLauncher stop --start <Start.exe> --receipt <existing.json> [--include-host]");
                return 0;
            }
            var command = arguments[0];
            var values = Parse(arguments.Skip(1).ToArray());
            string One(string key) => values.TryGetValue(key, out var value) && value.Count == 1 ? value[0]
                : throw new ArgumentException($"Specify {key} exactly once.");
            string Optional(string key, string fallback) => values.ContainsKey(key) ? One(key) : fallback;
            var start = Path.GetFullPath(One("--start"));
            var allowed = command switch {
                "inspect" => new[] { "--start" },
                "stop" => ["--start", "--receipt", "--include-host"],
                "dry-run" or "launch" => ["--start", "--steam", "--game", "--box", "--receipt", "--api", "--timeout", "--include-host"],
                "--internal-probe" => ["--start", "--box", "--api", "--port", "--nonce", "--timeout", "--allow-host"],
                _ => throw new ArgumentException("Unknown command; use --help.")
            };
            if (values.Keys.Any(key => !allowed.Contains(key))) throw new ArgumentException("Unknown option for this command.");
            var timeout = int.Parse(Optional("--timeout", "60"));
            if (timeout is < 5 or > 300) throw new ArgumentException("Timeout must be 5–300 seconds.");
            if (command == "--internal-probe")
                return await SteamReadinessProbe.RunInternalAsync(start, One("--box"), One("--api"), int.Parse(One("--port")), One("--nonce"), timeout, values.ContainsKey("--allow-host"));
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sandboxie sessions require Windows.");
            using var nativeClient = new SandboxieClient(start);
            var includeHost = values.ContainsKey("--include-host");
            ISandboxieClient client = includeHost ? new HostSessionClient(nativeClient) : nativeClient;
            if (command == "inspect") {
                Console.WriteLine(JsonSerializer.Serialize(client.GetBoxes().Select(box => new { box, processes = client.GetProcesses(box) }), Launcher.Json));
                return 0;
            }
            var launcher = new Launcher(client, new SteamReadinessProbe(client));
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
            if (command == "stop") {
                var receipt = await Launcher.ReadReceiptAsync(One("--receipt"), cancellation.Token);
                if (receipt.Games.Any(game => string.Equals(game.BoxName, HostSessionClient.Host, StringComparison.OrdinalIgnoreCase)) && !includeHost)
                    throw new ArgumentException("This receipt contains a normal host game; explicitly pass --include-host to stop it.");
                launcher.Stop(receipt);
                Console.WriteLine("Stopped matching owned game processes. Steam sessions remain available.");
                return 0;
            }
            var game = Path.GetFullPath(One("--game"));
            var api = Optional("--api", Path.Combine(Path.GetDirectoryName(game)!, Path.GetFileNameWithoutExtension(game) + "_Data", "Plugins", "x86_64", "steam_api64.dll"));
            var options = new LauncherOptions(start, Path.GetFullPath(One("--steam")), game, Path.GetFullPath(api),
                Path.GetFullPath(One("--receipt")), values.TryGetValue("--box", out var boxes) ? boxes : [], timeout, includeHost);
            launcher.Validate(options);
            if (command == "dry-run") {
                Console.WriteLine(JsonSerializer.Serialize(new { validated = true, options, note = "No processes started. Steam authentication and game startup remain unverified." }, Launcher.Json));
                return 0;
            }
            var result = await launcher.LaunchAsync(options, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { receipt = options.ReceiptPath,
                games = result.Games.Select(game => new { game.BoxName, game.Process.Pid, game.Process.CreationTime,
                    steamIdentitySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(game.SteamId.ToString()))) }) }, Launcher.Json));
            Console.WriteLine(includeHost
                ? "Distinct selected Steam identities, including the normal host, verified; game processes observed. Lobby and multiplayer connectivity are not verified."
                : "Selected sandbox Steam identities verified; game processes observed. Normal host Steam identity, lobby and multiplayer connectivity are not verified.");
            return 0;
        } catch (OperationCanceledException) {
            Console.Error.WriteLine("Cancelled or timed out. Check the receipt for any observed owned game processes.");
            return 2;
        } catch (Exception exception) {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
    private static Dictionary<string, List<string>> Parse(string[] arguments) {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2) {
            if (arguments[index] is "--include-host" or "--allow-host") {
                if (!result.TryAdd(arguments[index], ["true"])) throw new ArgumentException("Specify each flag at most once.");
                index--;
                continue;
            }
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
                throw new ArgumentException("Every option requires one value; repeat --box for each session.");
            if (!result.TryGetValue(arguments[index], out var values)) result.Add(arguments[index], values = []);
            values.Add(arguments[index + 1]);
        }
        return result;
    }
}
