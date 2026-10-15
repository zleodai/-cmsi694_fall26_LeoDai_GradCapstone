using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PlaytestOps.Web.Bridge;

public sealed record BuildConfigurationField
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Label { get; init; }
    public required string Kind { get; init; }
    public required string Value { get; init; }
    public required string[] Choices { get; init; }
    public required bool Editable { get; init; }
    public required string Note { get; init; }
}

public sealed record BuildConfigurationSnapshot
{
    public required int ProtocolVersion { get; init; }
    public required string CapturedAtUtc { get; init; }
    public required string Revision { get; init; }
    public required string State { get; init; }
    public required string ErrorCode { get; init; }
    public required string UnityVersion { get; init; }
    public required string ActiveTarget { get; init; }
    public required string ProfileName { get; init; }
    public required string ProfilePath { get; init; }
    public required string Scope { get; init; }
    public required BuildConfigurationField[] Fields { get; init; }
    public required string[] Limitations { get; init; }
}

public sealed record BuildConfigurationResult(BuildConfigurationSnapshot? Snapshot, string? Error);

public interface IProjectBuildConfigurationProvider
{
    Task<BuildConfigurationResult> GetAsync(string projectId, CancellationToken ct);
    Task<BuildConfigurationResult> SetAsync(string projectId, string sessionId, string revision, string settingId, string value, CancellationToken ct);
}

// Settings remain in Unity. Bounded, short-lived schemas guard posted field IDs.
public sealed class BuildConfigurationService(BridgeRegistry registry) : IProjectBuildConfigurationProvider
{
    public const int MaximumFields = 512;
    public const int MaximumValueLength = 16384;
    public const int MaximumEnvelopeBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = new();
    private readonly Dictionary<string, (WebSocket Socket, BuildConfigurationSnapshot Snapshot, DateTimeOffset At)> schemas = new();
    private sealed class Pending
    {
        public required string Id { get; init; }
        public required EditorSession Session { get; init; }
        public required WebSocket Socket { get; init; }
        public TaskCompletionSource<BuildConfigurationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record Reply
    {
        public required string RequestId { get; init; }
        public required BuildConfigurationSnapshot Snapshot { get; init; }
    }

    public Task<BuildConfigurationResult> GetAsync(string projectId, CancellationToken ct) => RequestAsync(projectId, null, "", "", "", ct);
    public Task<BuildConfigurationResult> SetAsync(string projectId, string sessionId, string revision, string settingId, string value, CancellationToken ct)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _) || !Revision(revision) || !Identifier(settingId) || value is null || value.Length > MaximumValueLength || value.Contains('\0'))
            return Task.FromResult(Failure("Invalid settings edit. Refresh the page before trying again."));
        return RequestAsync(projectId, sessionId, revision, settingId, value, ct);
    }

    private async Task<BuildConfigurationResult> RequestAsync(string projectId, string? sessionId, string revision, string settingId, string value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(projectId, "N", out _)) return Failure("Select a connected Unity project.");
        Pending request;
        lock (gate)
        {
            var session = registry.ConnectedProject(projectId);
            if (session?.Socket is not { State: WebSocketState.Open } socket || (sessionId is not null && session.Id != sessionId))
                return Failure("The selected Editor disconnected or was replaced. Refresh before editing settings.");
            if (sessionId is not null)
            {
                if (!schemas.TryGetValue(sessionId, out var schema) || !ReferenceEquals(schema.Socket, socket) || schema.At < DateTimeOffset.UtcNow.AddMinutes(-5) || schema.Snapshot.Revision != revision)
                    return Failure("The settings form is stale. Refresh and review current values before editing.");
                var field = schema.Snapshot.Fields.FirstOrDefault(item => item.Id == settingId);
                if (schema.Snapshot.State != "available" || field is not { Editable: true } || !ValidValue(field, value))
                    return Failure("This field is read-only, unavailable, or has an invalid value. Refresh and check its capability notes.");
            }
            else schemas.Remove(session.Id);
            if (pending.Count >= 4 || pending.Values.Any(item => item.Session.ProjectId == projectId))
                return Failure("A build settings request is already active. Wait for it to finish, then refresh.");
            request = new Pending { Id = Guid.NewGuid().ToString("N"), Session = session, Socket = socket };
            pending.Add(request.Id, request);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await registry.SendAsync(request.Session, request.Socket, new
            {
                type = sessionId is null ? "build.settings.get" : "build.settings.set", requestId = request.Id, revision, settingId, value
            }, deadline.Token);
            var result = await request.Completion.Task.WaitAsync(deadline.Token);
            if (!Current(request)) return Failure("Unity reconnected during this request. The edit may have been applied; refresh to confirm before retrying.");
            if (result.Snapshot is { } snapshot)
                lock (gate)
                {
                    if (schemas.Count >= 4 && !schemas.ContainsKey(request.Session.Id)) schemas.Remove(schemas.MinBy(item => item.Value.At).Key);
                    schemas[request.Session.Id] = (request.Socket, snapshot, DateTimeOffset.UtcNow);
                }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure("Unity did not respond within 30 seconds. An edit may have been applied. Wait for compilation or import to finish, then refresh to confirm; edits are never automatically replayed."); }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException)
        { return Failure("Unity disconnected during this request. An edit may have been applied. Reconnect and refresh before retrying."); }
        finally { lock (gate) pending.Remove(request.Id); }
    }

    private bool Current(Pending request) => request.Session.ExpiresAt > DateTimeOffset.UtcNow && request.Socket.State == WebSocketState.Open && registry.IsCurrent(request.Session, request.Socket);

    public bool TryHandleReply(EditorSession session, WebSocket socket, JsonElement envelope, string payload)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "build.settings.result") return false;
        if (!envelope.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParseExact(id.GetString(), "N", out _)) return true;
        Pending? request;
        lock (gate)
        {
            if (!pending.TryGetValue(id.GetString()!, out request) || !ReferenceEquals(request.Session, session) || !ReferenceEquals(request.Socket, socket) || !Current(request)) return true;
        }
        var result = Failure("Unity returned an invalid build settings response. Update the Editor package, reconnect and refresh.");
        if (Encoding.UTF8.GetByteCount(payload) <= MaximumEnvelopeBytes)
        {
            try
            {
                var reply = JsonSerializer.Deserialize<Reply>(payload, Json);
                if (reply?.RequestId == request.Id && ValidSnapshot(reply.Snapshot)) result = new(reply.Snapshot, null);
            }
            catch (JsonException) { }
        }
        request.Completion.TrySetResult(result);
        return true;
    }

    public void DisconnectedSocket(EditorSession session, WebSocket socket)
    {
        lock (gate)
        {
            if (schemas.TryGetValue(session.Id, out var schema) && ReferenceEquals(schema.Socket, socket)) schemas.Remove(session.Id);
            foreach (var request in pending.Values.Where(item => ReferenceEquals(item.Session, session) && ReferenceEquals(item.Socket, socket)).ToArray())
            {
                pending.Remove(request.Id);
                request.Completion.TrySetResult(Failure("Unity disconnected. An edit may have been applied; reconnect and refresh to confirm before retrying."));
            }
        }
    }

    private static bool ValidValue(BuildConfigurationField field, string value) => field.Kind switch
    {
        "bool" => value is "true" or "false",
        "integer" => long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _),
        "number" => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number),
        "enum" => field.Choices.Contains(value, StringComparer.Ordinal),
        "asset" => AssetValue(value),
        "json" => JsonValue(value),
        _ => true
    };
    private static bool JsonValue(string value)
    {
        try { using var json = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 16 }); return json.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array; }
        catch (JsonException) { return false; }
    }
    private static bool AssetValue(string value)
    {
        if (value.Length == 0) return true;
        var hash = value.LastIndexOf('#'); var path = hash < 0 ? value : value[..hash];
        return (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) && path.Length <= 1024 &&
            !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) && path.Split('/').All(part => part.Length > 0 && part is not ("." or "..")) &&
            (hash < 0 || long.TryParse(value[(hash + 1)..], out _));
    }

    public static bool Revision(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool Identifier(string? value) => value is { Length: > 0 and <= 256 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '[' or ']');
    private static bool Text(string? value, int maximum = 512) => value is not null && value.Length <= maximum && !value.Any(char.IsControl);
    private static bool ValidSnapshot(BuildConfigurationSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.ProtocolVersion != 1 || !VersionControlService.IsUtcTimestamp(snapshot.CapturedAtUtc) || !Revision(snapshot.Revision) ||
            snapshot.State is not ("available" or "busy" or "failed") || !Text(snapshot.ErrorCode, 64) || !Text(snapshot.UnityVersion, 64) || !Text(snapshot.ActiveTarget, 64) ||
            !Text(snapshot.ProfileName, 256) || !Text(snapshot.ProfilePath, 1024) || !Text(snapshot.Scope) || snapshot.Fields is null || snapshot.Fields.Length > MaximumFields ||
            snapshot.Limitations is null || snapshot.Limitations.Length > 128 || snapshot.Limitations.Any(item => !Text(item, 1024))) return false;
        if (snapshot.State == "available" && snapshot.Fields.Length == 0) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return snapshot.Fields.All(field => field is not null && Identifier(field.Id) && ids.Add(field.Id) && Text(field.Category, 128) && Text(field.Label, 256) &&
            field.Kind is "bool" or "integer" or "number" or "enum" or "string" or "json" or "asset" &&
            field.Value is not null && field.Value.Length <= MaximumValueLength && !field.Value.Contains('\0') && Text(field.Note, 1024) &&
            field.Choices is not null && field.Choices.Length <= 256 && field.Choices.All(choice => Text(choice, 256)));
    }

    private static BuildConfigurationResult Failure(string message) => new(null, message);
    public static string StatusMessage(string code) => code switch
    {
        "" => "", "saved" => "Setting saved in Unity. Refresh after any script reload to confirm the final value.",
        "busy" => "Unity is busy compiling, importing, building, running tests or changing Play Mode. Wait until it is idle, then refresh.",
        "stale" => "Unity settings changed since this page was read. Nothing was changed by this request. Review the refreshed values and try again.",
        "invalidSetting" => "This setting is unavailable or read-only in this Editor. Refresh and check the capability notes.",
        "invalidValue" => "The value is invalid for this setting. Nothing was changed. Check the type and format shown below.",
        "applyFailed" => "Unity could not apply the setting. The original value was restored when possible; refresh and check the Unity Console before retrying.",
        "unsupportedPlatform" => "This build settings adapter requires a Windows Unity Editor.",
        "outputLimit" => "The settings snapshot exceeded a safety limit. No partial values are editable.",
        _ => "Settings could not be read safely. Check the Unity Console and Editor package, then refresh."
    };
}
