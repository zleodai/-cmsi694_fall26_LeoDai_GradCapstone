using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PlaytestOps.Web.Bridge;

public sealed record VcsWorkspace
{
    public required string Name { get; init; }
    public required string Repository { get; init; }
    public required string Branch { get; init; }
    public required string SelectorType { get; init; }
    public required long CurrentChangeset { get; init; }
    public required long HeadChangeset { get; init; }
    public required string WorkspaceType { get; init; }
}

public sealed record VcsPendingItem
{
    public required string Status { get; init; }
    public required string Path { get; init; }
    public required string OldPath { get; init; }
}

public sealed record VcsChangeset
{
    public required long Number { get; init; }
    public required string DateUtc { get; init; }
    public required string Author { get; init; }
    public required string Comment { get; init; }
    public required string Branch { get; init; }
}

public sealed record VcsPendingSection
{
    public required string State { get; init; }
    public required string ErrorCode { get; init; }
    public required VcsPendingItem[] Items { get; init; }
    public required bool Truncated { get; init; }
}

public sealed record VcsIncomingSection
{
    public required string State { get; init; }
    public required string ErrorCode { get; init; }
    public required VcsChangeset[] Changesets { get; init; }
    public required bool Truncated { get; init; }
}

public sealed record VcsHistorySection
{
    public required string State { get; init; }
    public required string ErrorCode { get; init; }
    public required VcsChangeset[] Changesets { get; init; }
    public required bool HasMore { get; init; }
    public required int Offset { get; init; }
    public required string Scope { get; init; }
}

public sealed record VcsSnapshot
{
    public required int ProtocolVersion { get; init; }
    public required string CapturedAtUtc { get; init; }
    public required string State { get; init; }
    public required string ErrorCode { get; init; }
    public required VcsWorkspace Workspace { get; init; }
    public required VcsPendingSection Pending { get; init; }
    public required VcsIncomingSection Incoming { get; init; }
    public required VcsHistorySection History { get; init; }
}

public sealed record VersionControlResult(VcsSnapshot? Snapshot, string? Error);

// A read-only seam for an Editor-local client now and execution-host providers later.
public interface IProjectVersionControlProvider
{
    Task<VersionControlResult> SnapshotAsync(string projectId, int offset, string scope, CancellationToken ct);
}

// Only bounded in-flight requests are retained. No workspace metadata or credentials
// are persisted, cached, or written to diagnostic logs.
public sealed class VersionControlService(BridgeRegistry registry) : IProjectVersionControlProvider
{
    public const int PageSize = 50;
    public const int MaximumOffset = 100000;
    public const int MaximumPendingItems = 2000;
    public const int MaximumIncomingChangesets = 200;
    public const int MaximumEnvelopeBytes = 1024 * 1024;
    private const int MaximumPendingRequests = 4;
    private const string InvalidResponse = "Unity returned an invalid UVCS response. Update the PlaytestOps Editor package or reconnect, then refresh.";
    private const string Disconnected = "The Unity Editor disconnected or changed during the UVCS request. Reconnect and refresh.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private static readonly string[] TimestampFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"];
    private readonly object gate = new();
    private readonly Dictionary<string, PendingRequest> pending = new();

    private sealed class PendingRequest
    {
        public required string Id { get; init; }
        public required string ProjectId { get; init; }
        public required int Offset { get; init; }
        public required string Scope { get; init; }
        public required EditorSession Session { get; init; }
        public required WebSocket Socket { get; init; }
        public TaskCompletionSource<VersionControlResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record SnapshotReply
    {
        public required string RequestId { get; init; }
        public required VcsSnapshot Snapshot { get; init; }
    }

    public async Task<VersionControlResult> SnapshotAsync(string projectId, int offset, string scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(projectId, "N", out _)) return Failure("Select a connected Unity project.");
        if (!IsPageRequest(offset, scope)) return Failure("Choose repository or branch history with an offset between 0 and 100,000.");
        PendingRequest request;
        lock (gate)
        {
            var session = registry.ConnectedProject(projectId);
            if (session?.Socket is not { State: WebSocketState.Open } socket)
                return Failure("Connect this project's Unity Editor before viewing version control.");
            if (pending.Values.Any(item => item.ProjectId == projectId))
                return Failure("A UVCS refresh is already active for this project. Wait for it to finish and refresh.");
            if (pending.Count >= MaximumPendingRequests)
                return Failure("The version-control viewer is busy. Wait a moment and refresh.");
            request = new PendingRequest
            {
                Id = Guid.NewGuid().ToString("N"), ProjectId = projectId, Offset = offset, Scope = scope,
                Session = session, Socket = socket
            };
            pending.Add(request.Id, request);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // Never hold CatalogGate awaiting an Editor reply: it also carries test traffic.
            await registry.SendAsync(request.Session, request.Socket,
                new { type = "vcs.snapshot", requestId = request.Id, offset, scope }, deadline.Token);
            var reply = await request.Completion.Task.WaitAsync(deadline.Token);
            return Current(request) ? reply : Failure(Disconnected);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return Failure("Unity did not respond to the UVCS request within 30 seconds. Finish imports or compilation, check the UVCS connection, and ensure the Editor package supports version-control snapshots.");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            return Failure(Disconnected);
        }
        finally
        {
            lock (gate) pending.Remove(request.Id);
        }
    }

    private bool Current(PendingRequest request) => request.Session.ExpiresAt > DateTimeOffset.UtcNow &&
        request.Socket.State == WebSocketState.Open && registry.IsCurrent(request.Session, request.Socket);

    // Consume unsolicited, foreign, and late UVCS replies without retaining or echoing them.
    public bool TryHandleReply(EditorSession session, WebSocket socket, JsonElement envelope, string payload)
    {
        if (envelope.ValueKind != JsonValueKind.Object || !envelope.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "vcs.snapshot.result") return false;
        if (!envelope.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String ||
            !Guid.TryParseExact(id.GetString(), "N", out _)) return true;
        PendingRequest? request;
        lock (gate)
        {
            if (!pending.TryGetValue(id.GetString()!, out request) || !ReferenceEquals(request.Session, session) ||
                !ReferenceEquals(request.Socket, socket) || !Current(request)) return true;
        }
        var result = Failure(InvalidResponse);
        if (Encoding.UTF8.GetByteCount(payload) <= MaximumEnvelopeBytes)
        {
            try
            {
                var reply = JsonSerializer.Deserialize<SnapshotReply>(payload, Json);
                if (reply is not null && reply.RequestId == request.Id && ValidSnapshot(reply.Snapshot, request.Offset, request.Scope))
                    result = new(reply.Snapshot, null);
            }
            catch (JsonException)
            {
                // Neither malformed metadata nor raw client errors belong in logs or UI.
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
                request.Completion.TrySetResult(Failure(Disconnected));
            }
        }
    }

    public static bool IsPageRequest(int offset, string? scope) => offset is >= 0 and <= MaximumOffset && scope is "repository" or "branch";
    private static VersionControlResult Failure(string message) => new(null, message);

    private static bool ValidSnapshot(VcsSnapshot? snapshot, int offset, string scope)
    {
        if (snapshot is null || snapshot.ProtocolVersion != 1 || !IsUtcTimestamp(snapshot.CapturedAtUtc) ||
            snapshot.State is not ("available" or "notUvcs" or "missingClient" or "busy" or "failed") ||
            !Metadata(snapshot.ErrorCode) || snapshot.Workspace is not { } workspace ||
            !Metadata(workspace.Name) || !Metadata(workspace.Repository) || !Metadata(workspace.Branch) ||
            !Metadata(workspace.SelectorType) || !Metadata(workspace.WorkspaceType) ||
            workspace.CurrentChangeset < -1 || workspace.HeadChangeset < -1 ||
            snapshot.Pending is not { } pendingSection || snapshot.Incoming is not { } incoming || snapshot.History is not { } history)
            return false;
        if (snapshot.State == "available" && (workspace.Name.Length == 0 || workspace.Repository.Length == 0)) return false;
        if (pendingSection.State is not ("available" or "unavailable") || !Metadata(pendingSection.ErrorCode) ||
            pendingSection.Items is null || pendingSection.Items.Length > MaximumPendingItems ||
            pendingSection.Items.Any(item => item is null || !Metadata(item.Status, false) || !RelativePath(item.Path, false) || !RelativePath(item.OldPath, true)) ||
            (pendingSection.State != "available" && (pendingSection.Items.Length != 0 || pendingSection.Truncated))) return false;
        if (incoming.State is not ("available" or "unavailable" or "unsupported") || !Metadata(incoming.ErrorCode) ||
            !Changesets(incoming.Changesets, MaximumIncomingChangesets) ||
            (incoming.State != "available" && (incoming.Changesets.Length != 0 || incoming.Truncated))) return false;
        if (incoming.State == "available" &&
            (!string.Equals(workspace.SelectorType, "Branch", StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(workspace.WorkspaceType, "Regular", StringComparison.OrdinalIgnoreCase) ||
             workspace.Branch.Length == 0 ||
             workspace.CurrentChangeset < 0 || workspace.HeadChangeset < workspace.CurrentChangeset ||
             (incoming.Changesets.Length == 0 && workspace.HeadChangeset != workspace.CurrentChangeset) ||
             (incoming.Changesets.Length > 0 && workspace.HeadChangeset == workspace.CurrentChangeset) ||
             incoming.Changesets.Any(item => item.Number <= workspace.CurrentChangeset || item.Number > workspace.HeadChangeset ||
                 !string.Equals(item.Branch, workspace.Branch, StringComparison.Ordinal)))) return false;
        if (history.State is not ("available" or "unavailable") || !Metadata(history.ErrorCode) ||
            !Changesets(history.Changesets, PageSize) || history.Offset != offset || history.Scope != scope ||
            (history.State != "available" && (history.Changesets.Length != 0 || history.HasMore)) ||
            (history.HasMore && history.Changesets.Length != PageSize)) return false;
        if (snapshot.State != "available" && (pendingSection.State == "available" || incoming.State == "available" || history.State == "available")) return false;
        return true;
    }

    private static bool Changesets(VcsChangeset[]? changesets, int limit)
    {
        if (changesets is null || changesets.Length > limit) return false;
        var numbers = new HashSet<long>();
        return changesets.All(item => item is not null && item.Number >= 0 && numbers.Add(item.Number) &&
            IsUtcTimestamp(item.DateUtc) && Metadata(item.Author) && Metadata(item.Branch) &&
            item.Comment is not null && item.Comment.Length <= 4096 &&
            item.Comment.All(character => !char.IsControl(character) || character is '\n' or '\r' or '\t'));
    }

    private static bool Metadata(string? value, bool empty = true) => value is not null && value.Length <= 256 &&
        (empty || value.Length != 0) && !value.Any(char.IsControl);

    private static bool RelativePath(string? value, bool empty)
    {
        if (value is null || value.Length > 1024 || value.Any(char.IsControl) || value.Contains('\\') || value.Contains(':')) return false;
        if (value.Length == 0) return empty;
        return value.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));
    }

    public static bool IsUtcTimestamp(string? value) => value is not null && value.Length <= 64 &&
        DateTimeOffset.TryParseExact(value, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var timestamp) && timestamp.Offset == TimeSpan.Zero;

    // Only these fixed sentences reach the dashboard; never reflect a CLI error or code.
    public static string ErrorMessage(string? code) => code?.ToLowerInvariant() switch
    {
        "clientmissing" or "missingclient" => "The UVCS command-line client is unavailable on the Editor machine. Install or configure the client there, then refresh.",
        "notworkspace" or "notuvcs" => "This Unity project is not in a UVCS workspace.",
        "busy" or "editorbusy" => "Unity is busy importing, compiling, running tests, or changing Play Mode. Wait until the Editor is idle, then refresh.",
        "workspacebusy" => "The UVCS workspace is busy with another operation. Wait for the UVCS client to finish, then refresh.",
        "clientstartfailed" => "The UVCS client could not be started on the Editor machine. Check its installation and executable permissions, then refresh.",
        "unsupportedplatform" => "This PlaytestOps UVCS adapter currently supports Windows Editor hosts only. Use the UVCS client directly on this platform.",
        "timeout" => "The UVCS query reached its safety timeout. Check the client and server connection, then refresh.",
        "outputlimit" => "The UVCS query exceeded its output safety limit. Narrow the history scope or use the UVCS client to inspect the remaining data.",
        "invalidresponse" or "invalidxml" or "invaliddata" => "The UVCS client returned an unsupported response. Check the client version and PlaytestOps Editor package.",
        "invalidworkspace" => "The UVCS workspace configuration could not be read safely. Check the workspace and selector in the UVCS client on the Editor machine, then refresh.",
        "authentication" or "authrequired" or "authenticationrequired" => "UVCS authentication is unavailable. Sign in through the UVCS client on the Editor machine, then refresh.",
        "offline" or "serverunavailable" => "The UVCS server could not be reached. Local pending changes may still be available; retry when the connection is restored.",
        "workspacechanged" => "The workspace or selector changed during refresh. Refresh again for a consistent view.",
        "unsupportedselector" or "unsupportedworkspace" or "incomingunsupported" => "Incoming changes are supported only for a regular workspace tracking a branch with a verified ancestor relationship. Use the UVCS client for this workspace or selector.",
        "ancestryunknown" => "The loaded changeset could not be verified as an ancestor of the branch head. Incoming changes are unavailable; use the UVCS client to inspect this comparison.",
        _ => "UVCS information is unavailable. Check the UVCS client, login, permissions, and server connection on the Editor machine, then refresh."
    };
}
