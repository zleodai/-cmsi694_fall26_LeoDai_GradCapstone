using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;

namespace PlaytestOps.Web.Streaming;

// Keep approved files open without write/delete sharing until the owned child exits.
public sealed class StreamingArtifact(string executablePath, string projectName, IReadOnlyList<FileStream> pins) : IDisposable
{
    public string ExecutablePath { get; } = executablePath;
    public string ProjectName { get; } = projectName;
    public bool SupportsArenaKcp { get; init; }
    public bool SupportsArenaSteam { get; init; }
    public void Dispose() { foreach (var pin in pins) pin.Dispose(); }
}

public sealed class StreamingArtifactValidator(IServiceScopeFactory scopes)
{
    public static bool SupportsLaunchProfile(Models.UnityBuild build, string profile)
    {
        if (CapabilityReason(build) is not null) return false;
        try
        {
            using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(Canonical(build.OutputPath), StreamingProtocol.CapabilityFile)));
            return HasProfile(marker.RootElement, profile) && (profile != "arena-steam" || SteamCapability(marker.RootElement));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        { return false; }
    }

    private static bool HasProfile(JsonElement marker, string profile) =>
        marker.TryGetProperty("launchProfiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array && profiles.GetArrayLength() <= 8 &&
        profiles.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == profile);

    private static bool SteamCapability(JsonElement marker) =>
        marker.TryGetProperty("steamAppId", out var appId) && appId.TryGetInt32(out var value) && value == 480 &&
        marker.TryGetProperty("steamAdapter", out var adapter) && adapter.GetString() == "steam-arena-v1";

    // Listing is a capability hint; launch always performs full identity/hash validation.
    public static string? CapabilityReason(Models.UnityBuild build)
    {
        try
        {
            var output = Canonical(build.OutputPath);
            if (!output.EndsWith(Path.Combine("Builds", "PlaytestOps", build.Id), StringComparison.OrdinalIgnoreCase)) return "Unapproved build output location.";
            CheckOrdinaryPath(output);
            var capability = Path.Combine(output, StreamingProtocol.CapabilityFile);
            if (!File.Exists(capability)) return "Install the streaming runtime SDK and rebuild a Windows64 player.";
            CheckOrdinaryPath(capability);
            if (new FileInfo(capability).Length is <= 0 or > 4096) return "Unsupported streaming capability marker; rebuild with the runtime SDK.";
            using var marker = JsonDocument.Parse(File.ReadAllText(capability), new JsonDocumentOptions { MaxDepth = 4 });
            if (!SupportedCapability(marker.RootElement)) return "Unsupported streaming capability marker; rebuild with the runtime SDK.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException) { return "Build artifacts are unavailable, unsupported or linked."; }
    }

    public async Task<StreamingArtifact> ValidateAsync(string buildId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(buildId, "N", out _)) throw new InvalidDataException("Select a persisted successful build.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var build = await db.UnityBuilds.AsNoTracking().SingleOrDefaultAsync(item => item.Id == buildId, ct);
        if (build?.State != "Succeeded") throw new InvalidDataException("Only a persisted successful build can be streamed.");
        if (build.ActiveTarget != "StandaloneWindows64") throw new InvalidDataException("The streaming runtime SDK requires a Windows64 player build.");
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("Local build streaming requires Windows.");
        var pins = new List<FileStream>();
        try
        {
            var output = Canonical(build.OutputPath);
            if (!output.EndsWith(Path.Combine("Builds", "PlaytestOps", buildId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected build has an unapproved output location.");
            var executable = Path.Combine(output, "Player.exe");
            var manifestPath = Path.Combine(output, "build-manifest.json");
            if (!string.Equals(Canonical(build.ExecutablePath), executable, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Canonical(build.ManifestPath), manifestPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The build's executable or manifest identity changed.");
            CheckOrdinaryPath(output);
            var manifestPin = Pin(manifestPath, pins);
            if (manifestPin.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("The build manifest is missing or oversized.");
            using var manifest = await JsonDocument.ParseAsync(manifestPin, new JsonDocumentOptions { MaxDepth = 16 }, ct);
            var root = manifest.RootElement;
            if (root.GetProperty("protocolVersion").GetInt32() != 1 || root.GetProperty("buildId").GetString() != buildId ||
                root.GetProperty("result").GetString() != "Succeeded" || root.GetProperty("totalErrors").GetInt32() != 0 ||
                root.GetProperty("artifactListTruncated").GetBoolean() || root.GetProperty("activeTarget").GetString() != build.ActiveTarget ||
                root.GetProperty("revision").GetString() != build.Revision)
                throw new InvalidDataException("The manifest does not match the successful build record.");
            var artifacts = root.GetProperty("artifacts");
            if (artifacts.ValueKind != JsonValueKind.Array || artifacts.GetArrayLength() > 10000)
                throw new InvalidDataException("The build manifest has an invalid artifact list.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonElement? player = null, capability = null;
            foreach (var entry in artifacts.EnumerateArray())
            {
                var relative = entry.GetProperty("path").GetString() ?? "";
                if (relative.Length is 0 or > 2048 || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') ||
                    relative.Split('/').Any(part => part is "" or "." or "..") || !seen.Add(relative))
                    throw new InvalidDataException("The build manifest has an unsafe or duplicate artifact path.");
                if (relative == "Player.exe") player = entry;
                if (relative == StreamingProtocol.CapabilityFile) capability = entry;
            }
            if (player is null) throw new InvalidDataException("The manifest does not approve Player.exe.");
            if (capability is null) throw new InvalidDataException("This build lacks PlaytestOps streaming support. Install the streaming runtime SDK and rebuild a Windows64 player.");
            await VerifyAsync(executable, player.Value, true, pins, ct);
            var markerPath = Path.Combine(output, StreamingProtocol.CapabilityFile);
            await VerifyAsync(markerPath, capability.Value, true, pins, ct);
            var markerPin = pins.Last(); markerPin.Position = 0;
            if (markerPin.Length > 4096) throw new InvalidDataException("The streaming capability marker is oversized.");
            using var marker = await JsonDocument.ParseAsync(markerPin, new JsonDocumentOptions { MaxDepth = 4 }, ct);
            if (!SupportedCapability(marker.RootElement) || marker.RootElement.GetProperty("buildGuid").GetString() != root.GetProperty("buildGuid").GetString())
                throw new InvalidDataException("The streaming capability does not match the approved build. Rebuild with the runtime SDK.");
            // Verify all producer-populated identity hashes (Mono assemblies,
            // IL2CPP GameAssembly, UnityPlayer and WebRTC native plugin included).
            foreach (var entry in artifacts.EnumerateArray())
            {
                var relative = entry.GetProperty("path").GetString()!;
                if (relative is "Player.exe" or StreamingProtocol.CapabilityFile || string.IsNullOrEmpty(entry.GetProperty("sha256").GetString())) continue;
                if (pins.Count >= 256) throw new InvalidDataException("The build has too many pinned identity artifacts.");
                await VerifyAsync(Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar)), entry, true, pins, ct);
            }
            var steam = HasProfile(marker.RootElement, "arena-steam");
            if (steam)
            {
                if (!SteamCapability(marker.RootElement)) throw new InvalidDataException("The Steam build has an unsupported game adapter or App ID.");
                foreach (var required in new[] { "steam_appid.txt", "Player_Data/Plugins/x86_64/steam_api64.dll" })
                {
                    var approved = artifacts.EnumerateArray().FirstOrDefault(entry => entry.GetProperty("path").GetString() == required);
                    if (approved.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException("Rebuild the Steam player with its App ID and Steam API artifacts.");
                    var filePath = Path.Combine(output, required.Replace('/', Path.DirectorySeparatorChar));
                    // New manifests hash these artifacts. A previous producer may omit the App ID hash,
                    // but its manifest size and exact development ID are still checked under a pin.
                    if (string.IsNullOrEmpty(approved.GetProperty("sha256").GetString()))
                        await VerifyAsync(filePath, approved, required != "steam_appid.txt", pins, ct);
                }
                var appPin = pins.SingleOrDefault(pin => string.Equals(pin.Name, Path.Combine(output, "steam_appid.txt"), StringComparison.OrdinalIgnoreCase));
                if (appPin is null) throw new InvalidDataException("The Steam App ID was not pinned.");
                appPin.Position = 0;
                using var reader = new StreamReader(appPin, leaveOpen: true);
                if (appPin.Length > 32 || (await reader.ReadToEndAsync(ct)).Trim() != "480")
                    throw new InvalidDataException("This Steam playtest requires its approved App ID 480.");
            }
            return new StreamingArtifact(executable, build.ProjectName, pins) {
                SupportsArenaKcp = HasProfile(marker.RootElement, "arena-kcp"), SupportsArenaSteam = steam };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OperationCanceledException)
        {
            foreach (var pin in pins) pin.Dispose();
            if (exception is OperationCanceledException) throw;
            throw new InvalidDataException(exception is InvalidDataException ? exception.Message : "The approved build files could not be validated. Rebuild before streaming.");
        }
    }

    private static bool SupportedCapability(JsonElement marker) =>
        marker.GetProperty("protocolVersion").GetInt32() == StreamingProtocol.Version && marker.GetProperty("packageVersion").GetString() == "0.1.0" &&
        marker.GetProperty("capture").GetString() == "framebuffer" && marker.GetProperty("input").GetString() == "input-system" &&
        marker.GetProperty("transport").GetString() == "webrtc" && marker.GetProperty("buildGuid").GetString() is { Length: > 0 and <= 64 };

    private static async Task VerifyAsync(string path, JsonElement entry, bool requireHash, List<FileStream> pins, CancellationToken ct)
    {
        var pin = Pin(path, pins);
        if (pin.Length <= 0 || entry.GetProperty("bytes").GetInt64() != pin.Length)
            throw new InvalidDataException("An approved build artifact is missing or changed in size.");
        var expected = entry.GetProperty("sha256").GetString() ?? "";
        if (expected.Length == 0 && !requireHash) return; // Older manifests hash only the executable; runtime readiness still requires its version/capabilities.
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new InvalidDataException("An artifact hash is missing or invalid.");
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(pin, ct));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An approved build artifact failed SHA-256 validation.");
    }

    private static FileStream Pin(string path, List<FileStream> pins)
    {
        CheckOrdinaryPath(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        pins.Add(stream);
        return stream;
    }

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Replace('\\', '/').Split('/').Any(part => part is "." or "..") || path.Skip(2).Contains(':'))
            throw new InvalidDataException("Build streaming accepts ordinary local build paths only.");
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
    }

    private static void CheckOrdinaryPath(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked build artifacts and directories cannot be launched.");
    }
}
