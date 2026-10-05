using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PlaytestOps.Web.Bridge;

public sealed record SourceFileEntry(string Path, long ByteLength);
public sealed record SourceListResult(IReadOnlyList<string> Roots, IReadOnlyList<SourceFileEntry> Files, bool Truncated, string? Error);
public sealed record SourceReadResult(string Path, long ByteLength, string Content, string Sha256, string LastModifiedUtc, string? Error);

// Keeps the working-copy transport separate from future committed-revision/VCS providers.
public interface IProjectSourceProvider
{
    Task<SourceListResult> ListAsync(string projectId, CancellationToken ct);
    Task<SourceReadResult> ReadAsync(string projectId, string path, CancellationToken ct);
}

// This broker keeps only bounded, in-flight requests. Source is never persisted or logged.
public sealed class SourceService(BridgeRegistry registry) : IProjectSourceProvider
{
    public const int MaxFileBytes = 128 * 1024;
    private const int MaxEnvelopeBytes = 1024 * 1024;
    private const int MaxPending = 4;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Plugins", "Samples", "TutorialInfo", "ThirdParty", "Third-Party", "Vendor", "External",
        "Packages", "Library", "PackageCache", "ProjectSettings", "UserSettings", "Temp", "obj", "Logs",
        ".git", ".svn", "Samples~", "Standard Assets", "Templates", "Template", "Examples", "Example"
    };
    private static readonly string[] TimestampFormats = [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz"
    ];
    private const string InvalidResponse = "Unity returned an invalid script response. Refresh or reconnect the Editor and try again.";
    private const string Disconnected = "The Unity Editor disconnected or changed during the script request. Reconnect and refresh.";
    private readonly object gate = new();
    private readonly Dictionary<string, PendingRequest> pending = new();

    private sealed class PendingRequest
    {
        public required string Id { get; init; }
        public required string ProjectId { get; init; }
        public required string Kind { get; init; }
        public required EditorSession Session { get; init; }
        public required WebSocket Socket { get; init; }
        public string? Path { get; init; }
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record RequestFailure(string Message);
    private sealed record ListReply(string? RequestId, string[]? Roots, SourceFileEntry[]? Files, bool Truncated, string? Error);
    private sealed record ReadReply(string? RequestId, string? Path, long ByteLength, string? Content, string? Sha256, string? LastModifiedUtc, string? Error);

    public async Task<SourceListResult> ListAsync(string projectId, CancellationToken ct)
    {
        var reply = await RequestAsync(projectId, "source.list", null, ct);
        return reply is SourceListResult result ? result : new([], [], false, ((RequestFailure)reply).Message);
    }

    public async Task<SourceReadResult> ReadAsync(string projectId, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsScriptPath(path)) return new("", 0, "", "", "", "Select an allowed project C# script to view.");
        var reply = await RequestAsync(projectId, "source.read", path, ct);
        return reply is SourceReadResult result ? result : new(path, 0, "", "", "", ((RequestFailure)reply).Message);
    }

    private async Task<object> RequestAsync(string projectId, string kind, string? path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(projectId, "N", out _)) return new RequestFailure("Select a connected Unity project.");
        PendingRequest request;
        lock (gate)
        {
            var session = registry.ConnectedProject(projectId);
            if (session?.Socket is not { State: WebSocketState.Open } socket)
                return new RequestFailure("Connect this project's Unity Editor before browsing scripts.");
            if (pending.Values.Any(item => item.ProjectId == projectId))
                return new RequestFailure("A script request is already active for this project. Wait for it to finish and refresh.");
            if (pending.Count >= MaxPending)
                return new RequestFailure("The script browser is busy. Wait a moment and try again.");
            request = new PendingRequest
            {
                Id = Guid.NewGuid().ToString("N"), ProjectId = projectId, Kind = kind,
                Session = session, Socket = socket, Path = path
            };
            pending.Add(request.Id, request);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            object command = path is null ? new { type = kind, requestId = request.Id }
                : new { type = kind, requestId = request.Id, path };
            // Do not hold CatalogGate while awaiting a reply: the same socket must process it.
            await registry.SendAsync(request.Session, request.Socket, command, deadline.Token);
            var reply = await request.Completion.Task.WaitAsync(deadline.Token);
            return Current(request) ? reply : new RequestFailure(Disconnected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return new RequestFailure("Unity did not respond to the script request within 10 seconds. Finish imports or compilation, then refresh.");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            return new RequestFailure(Disconnected);
        }
        finally
        {
            lock (gate) pending.Remove(request.Id);
        }
    }

    private bool Current(PendingRequest request) => request.Session.ExpiresAt > DateTimeOffset.UtcNow &&
        request.Socket.State == WebSocketState.Open && registry.IsCurrent(request.Session, request.Socket);

    // Supported but unsolicited/late/foreign replies are consumed without caching or echoing them.
    public bool TryHandleReply(EditorSession session, WebSocket socket, JsonElement envelope, string payload)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() is not ("source.list.result" or "source.read.result"))
            return false;
        if (!envelope.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(id.GetString(), "N", out _)) return true;
        PendingRequest? request;
        lock (gate)
        {
            if (!pending.TryGetValue(id.GetString()!, out request) || !ReferenceEquals(request.Session, session) ||
                !ReferenceEquals(request.Socket, socket) || !Current(request)) return true;
        }
        object result = new RequestFailure(InvalidResponse);
        if (Encoding.UTF8.GetByteCount(payload) <= MaxEnvelopeBytes && type.GetString() == request.Kind + ".result")
        {
            try
            {
                result = request.Kind == "source.list" ? ValidateList(payload)
                    : ValidateRead(payload, request.Path!);
            }
            catch (Exception ex) when (ex is JsonException or EncoderFallbackException or FormatException)
            {
                // Untrusted source/metadata must not enter exception logs or user-visible errors.
            }
        }
        request.Completion.TrySetResult(result);
        return true;
    }

    public void DisconnectedSocket(EditorSession session, WebSocket socket)
    {
        lock (gate)
        {
            foreach (var request in pending.Values.Where(item => ReferenceEquals(item.Session, session) && ReferenceEquals(item.Socket, socket)).ToArray())
            {
                pending.Remove(request.Id);
                request.Completion.TrySetResult(new RequestFailure(Disconnected));
            }
        }
    }

    private static object ValidateList(string payload)
    {
        var reply = JsonSerializer.Deserialize<ListReply>(payload, Json);
        if (reply is null) return new RequestFailure(InvalidResponse);
        if (!string.IsNullOrEmpty(reply.Error)) return new RequestFailure("Unity could not list scripts. Check the configured script roots and Editor state, then refresh.");
        if (reply.Roots is null || reply.Files is null || reply.Roots.Length > 16 || reply.Files.Length > 1000 ||
            reply.Roots.Any(root => !IsRootPath(root)) ||
            reply.Roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != reply.Roots.Length)
            return new RequestFailure(InvalidResponse);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in reply.Files)
        {
            if (file is null || !IsScriptPath(file.Path) || file.ByteLength < 0 || !paths.Add(file.Path) ||
                !reply.Roots.Any(root => file.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
                return new RequestFailure(InvalidResponse);
        }
        return new SourceListResult(reply.Roots, reply.Files, reply.Truncated, null);
    }

    private static object ValidateRead(string payload, string requestedPath)
    {
        var reply = JsonSerializer.Deserialize<ReadReply>(payload, Json);
        if (reply is null || reply.Path != requestedPath) return new RequestFailure(InvalidResponse);
        if (!string.IsNullOrEmpty(reply.Error)) return new RequestFailure("Unity could not read this script. It may be unavailable, outside the configured roots, or larger than 128 KiB. Refresh the list.");
        if (!IsScriptPath(reply.Path) || reply.ByteLength < 0 || reply.ByteLength > MaxFileBytes ||
            reply.Content is null || reply.Content.Length > MaxFileBytes || reply.Content.Contains('\0') ||
            reply.Sha256 is null || reply.Sha256.Length != 64 ||
            reply.Sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            reply.LastModifiedUtc is null || reply.LastModifiedUtc.Length > 64 ||
            !DateTimeOffset.TryParseExact(reply.LastModifiedUtc, TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var modified) || modified.Offset != TimeSpan.Zero)
            return new RequestFailure(InvalidResponse);
        var bytes = StrictUtf8.GetBytes(reply.Content);
        if (bytes.Length != reply.ByteLength || Convert.ToHexStringLower(SHA256.HashData(bytes)) != reply.Sha256)
            return new RequestFailure(InvalidResponse);
        return new SourceReadResult(reply.Path, reply.ByteLength, reply.Content, reply.Sha256, reply.LastModifiedUtc, null);
    }

    public static bool IsScriptPath(string? path) => IsCanonicalAssetPath(path) && path!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    private static bool IsRootPath(string? path) => IsCanonicalAssetPath(path);
    private static bool IsCanonicalAssetPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 512 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)) return false;
        var parts = path.Split('/');
        return parts.Length >= 2 && parts[0] == "Assets" &&
            parts.All(part => part.Length > 0 && part is not ("." or "..") && part == part.Trim() &&
                !part.EndsWith(".", StringComparison.Ordinal) && part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) < 0 &&
                !ExcludedSegments.Contains(part));
    }
}
