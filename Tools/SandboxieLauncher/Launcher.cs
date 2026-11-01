using System.Text.Json;

namespace PlaytestOps.Sandboxie;

public sealed record LauncherOptions(string StartPath, string SteamPath, string GamePath,
    string SteamApiPath, string ReceiptPath, IReadOnlyList<string> Boxes, int TimeoutSeconds = 60, bool IncludeHost = false);
public sealed record OwnedGame(string BoxName, ulong SteamId, BoxProcess Process);
public sealed record LaunchReceipt(int Version, string GamePath, DateTimeOffset CreatedUtc, IReadOnlyList<OwnedGame> Games);
public interface ISteamReadinessProbe {
    Task<ulong> GetSteamIdAsync(string box, LauncherOptions options, CancellationToken cancellationToken);
}

public sealed class Launcher(ISandboxieClient client, ISteamReadinessProbe probe) {
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void ValidateFiles(LauncherOptions options) {
        if (options.Boxes.Count < 1 || options.Boxes.Count > 4)
            throw new ArgumentException("Select between one and four preconfigured boxes; an existing normal Steam session is managed separately.");
        if (options.IncludeHost && options.Boxes.Count > 3) throw new ArgumentException("Normal host plus selected boxes must total no more than four sessions.");
        foreach (var box in options.Boxes) SandboxieClient.ValidateBoxName(box);
        if (options.Boxes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Boxes.Count)
            throw new ArgumentException("Each Steam session must use a distinct box.");
        if (options.TimeoutSeconds is < 5 or > 300) throw new ArgumentException("Timeout must be 5–300 seconds.");
        foreach (var path in new[] { options.StartPath, options.SteamPath, options.GamePath, options.SteamApiPath }) {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new ArgumentException($"Missing absolute file path: {path}");
            RejectLinks(path);
        }
        if (!Path.GetFileName(options.StartPath).Equals("Start.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(options.SteamPath).Equals("steam.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(options.GamePath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(options.SteamApiPath).Equals("steam_api64.dll", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Expected Sandboxie Start.exe, steam.exe, a game .exe and steam_api64.dll.");
        var gameDirectory = Path.GetDirectoryName(Path.GetFullPath(options.GamePath))!;
        if (!Inside(options.SteamApiPath, gameDirectory)) throw new ArgumentException("Steam API DLL must come from the selected existing game build.");
        var appIdPath = Path.Combine(gameDirectory, "steam_appid.txt");
        if (!File.Exists(appIdPath) || !uint.TryParse(File.ReadAllText(appIdPath).Trim(), out var appId) || appId == 0)
            throw new ArgumentException("The existing game directory must contain steam_appid.txt with a valid positive App ID. No file will be created.");
        RejectLinks(appIdPath);
        if (!Path.IsPathFullyQualified(options.ReceiptPath)) throw new ArgumentException("Receipt path must be absolute.");
        if (Inside(options.ReceiptPath, gameDirectory) || Inside(options.ReceiptPath, Path.GetDirectoryName(options.SteamPath)!))
            throw new ArgumentException("Receipt must be outside the game and Steam installation directories.");
        if (File.Exists(options.ReceiptPath) || Directory.Exists(options.ReceiptPath)) throw new ArgumentException("Choose a new receipt path; existing receipts are never overwritten.");
        var receiptDirectory = Path.GetDirectoryName(options.ReceiptPath)!;
        if (!Directory.Exists(receiptDirectory)) throw new ArgumentException("Create the receipt parent directory before launching.");
        RejectLinks(receiptDirectory);
    }

    public void Validate(LauncherOptions options) {
        ValidateFiles(options);
        var boxes = client.GetBoxes();
        foreach (var box in Contexts(options)) {
            if (!boxes.Contains(box, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Sandbox is not configured: {box}");
            if (client.GetProcesses(box).Any(process => ImageMatches(process.ImageName, options.GamePath)))
                throw new InvalidOperationException($"The selected game is already running in {box}; stop it normally first.");
        }
    }

    public async Task<LaunchReceipt> LaunchAsync(LauncherOptions options, CancellationToken cancellationToken) {
        Validate(options);
        // Reserve evidence output before any external process starts, without overwriting prior evidence.
        await using var receiptFile = new FileStream(options.ReceiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var games = new List<OwnedGame>();
        var identities = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        string? pendingBox = null;
        HashSet<(int Pid, long CreationTime)>? pendingBaseline = null;
        try {
            foreach (var box in Contexts(options)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!client.GetProcesses(box).Any(process => ImageMatches(process.ImageName, options.SteamPath)))
                    await client.LaunchAsync(box, options.SteamPath, Path.GetDirectoryName(options.SteamPath)!, [], cancellationToken);
            }
            // All accounts must actually authenticate before any game is launched.
            foreach (var box in Contexts(options)) {
                var steamId = await probe.GetSteamIdAsync(box, options, cancellationToken);
                if (steamId == 0 || identities.Values.Contains(steamId))
                    throw new InvalidOperationException("Each selected sandbox must report a distinct authenticated Steam identity. No games were launched.");
                identities.Add(box, steamId);
            }
            foreach (var box in Contexts(options)) {
                var before = client.GetProcesses(box).Select(process => (process.Pid, process.CreationTime)).ToHashSet();
                if (client.GetProcesses(box).Any(process => ImageMatches(process.ImageName, options.GamePath)))
                    throw new InvalidOperationException($"The selected game started independently in {box}; refusing an ambiguous launch.");
                pendingBox = box;
                pendingBaseline = before;
                await client.LaunchAsync(box, options.GamePath, Path.GetDirectoryName(options.GamePath)!, [], cancellationToken);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                BoxProcess? observed = null;
                while (observed is null) {
                    var candidates = client.GetProcesses(box).Where(process => ImageMatches(process.ImageName, options.GamePath)
                        && !before.Contains((process.Pid, process.CreationTime))).ToArray();
                    if (candidates.Length > 1) throw new InvalidOperationException($"Multiple new game processes appeared in {box}; ownership is ambiguous.");
                    if (candidates.Length == 1) observed = candidates[0];
                    else await Task.Delay(200, deadline.Token);
                }
                games.Add(new OwnedGame(box, identities[box], observed));
                pendingBox = null;
                pendingBaseline = null;
                await SaveAsync(receiptFile, new(1, options.GamePath, DateTimeOffset.UtcNow, games.ToArray()));
            }
            foreach (var game in games) {
                if (client.GetProcess(game.Process.Pid) != game.Process)
                    throw new InvalidOperationException($"An observed game exited or changed identity in {game.BoxName} during startup.");
            }
            return new(1, options.GamePath, DateTimeOffset.UtcNow, games.ToArray());
        } catch {
            // Start.exe can fail or be cancelled after creating the target. Recover only an unambiguous new target.
            if (pendingBox is not null && pendingBaseline is not null) {
                try {
                    for (var attempt = 0; attempt < 15; attempt++) {
                        var candidates = client.GetProcesses(pendingBox).Where(process => ImageMatches(process.ImageName, options.GamePath)
                            && !pendingBaseline.Contains((process.Pid, process.CreationTime))).ToArray();
                        if (candidates.Length == 1) {
                            games.Add(new OwnedGame(pendingBox, identities[pendingBox], candidates[0]));
                            break;
                        }
                        if (candidates.Length > 1) throw new InvalidOperationException("Multiple new matching processes; no process was claimed for cleanup.");
                        await Task.Delay(200);
                    }
                } catch (Exception exception) { Console.Error.WriteLine($"Startup cleanup could not establish ownership in {pendingBox}: {exception.Message}"); }
            }
            foreach (var game in games.AsEnumerable().Reverse()) {
                try { client.StopProcess(game.Process); }
                catch (Exception exception) { Console.Error.WriteLine($"Cleanup failed for owned game in {game.BoxName}: {exception.Message}"); }
            }
            await SaveAsync(receiptFile, new(1, options.GamePath, DateTimeOffset.UtcNow, games.ToArray()));
            throw;
        }
    }

    public static async Task<LaunchReceipt> ReadReceiptAsync(string path, CancellationToken cancellationToken) {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Receipt is oversized.");
        var receipt = JsonSerializer.Deserialize<LaunchReceipt>(await File.ReadAllTextAsync(path, cancellationToken), Json)
            ?? throw new InvalidDataException("Receipt is empty.");
        if (receipt.Version != 1 || receipt.Games is null || receipt.Games.Count > 4 || !Path.IsPathFullyQualified(receipt.GamePath)
            || receipt.Games.Any(game => game.Process is null || game.Process.Pid <= 0 || game.Process.CreationTime <= 0
                || !string.Equals(game.BoxName, game.Process.BoxName, StringComparison.OrdinalIgnoreCase)
                || !ImageMatches(game.Process.ImageName, receipt.GamePath)))
            throw new InvalidDataException("Receipt contains invalid process identities.");
        return receipt;
    }

    public void Stop(LaunchReceipt receipt) {
        var failures = new List<Exception>();
        foreach (var game in receipt.Games) {
            try {
                client.StopProcess(game.Process);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (client.GetProcess(game.Process.Pid) == game.Process) {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException($"The recorded game in {game.BoxName} has not exited.");
                    Thread.Sleep(50);
                }
            } catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0) throw new AggregateException("Some recorded games could not be stopped; other recorded games were still processed.", failures);
    }
    private static IEnumerable<string> Contexts(LauncherOptions options) => options.IncludeHost
        ? new[] { HostSessionClient.Host }.Concat(options.Boxes) : options.Boxes;
    private static async Task SaveAsync(FileStream file, LaunchReceipt receipt) {
        file.Position = 0;
        await JsonSerializer.SerializeAsync(file, receipt, Json);
        file.SetLength(file.Position);
        await file.FlushAsync();
    }
    internal static bool ImageMatches(string image, string executable) =>
        string.Equals(Path.GetFileName(image), Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string path, string directory) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RejectLinks(string path) {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Linked paths are unsupported.");
    }
}
