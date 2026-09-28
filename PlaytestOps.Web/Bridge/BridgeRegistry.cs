using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace PlaytestOps.Web.Bridge;

public sealed record PairRequest(string Code, string ProjectId, string ProjectName, string UnityVersion);
public sealed record PairResponse(string SessionId, string Token, string ProjectId, DateTimeOffset ExpiresAt);
public sealed record SessionView(string SessionId, string ProjectId, string ProjectName, string UnityVersion, bool Connected, DateTimeOffset? LastConnectedAt);

public sealed class EditorSession
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required string UnityVersion { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public WebSocket? Socket { get; set; }
    // Accessed only while CatalogGate is held; reset when a socket attaches.
    public string? RunProbeId { get; set; }
    public bool RunReady { get; set; }
    public SemaphoreSlim SendGate { get; } = new(1, 1);
    public DateTimeOffset? LastConnectedAt { get; set; }
}

// Credentials deliberately live only in memory. A server restart requires pairing again.
public sealed class BridgeRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> codes = new();
    private readonly Dictionary<string, EditorSession> sessions = new();
    public SemaphoreSlim CatalogGate { get; } = new(1, 1);

    public static bool IsLocalOperator(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip) &&
        context.Request.Host.Host.Trim('[', ']').ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1";

    public (string Code, DateTimeOffset ExpiresAt) CreateCode()
    {
        lock (gate)
        {
            Prune();
            if (codes.Count >= 8) codes.Remove(codes.Keys.First());
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
            var expires = DateTimeOffset.UtcNow.AddMinutes(5);
            codes.Add(code, expires);
            return (code, expires);
        }
    }

    public PairResponse? Pair(PairRequest request)
    {
        lock (gate)
        {
            Prune();
            if (!codes.Remove(request.Code.Trim().ToUpperInvariant(), out var expires) || expires <= DateTimeOffset.UtcNow)
                return null;
            // A project has one paired Editor in this first version.
            foreach (var entry in sessions.Where(x => x.Value.ProjectId == request.ProjectId).ToArray())
            {
                entry.Value.Socket?.Abort();
                sessions.Remove(entry.Key);
            }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var session = new EditorSession {
                Id = Guid.NewGuid().ToString("N"), ProjectId = request.ProjectId,
                ProjectName = request.ProjectName, UnityVersion = request.UnityVersion,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(12)
            };
            sessions.Add(Hash(token), session);
            return new(session.Id, token, session.ProjectId, session.ExpiresAt);
        }
    }

    public EditorSession? Authenticate(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length != 71) return null;
        lock (gate)
        {
            Prune();
            return sessions.GetValueOrDefault(Hash(header[7..]));
        }
    }

    public bool Attach(EditorSession session, WebSocket socket)
    {
        lock (gate)
        {
            if (!sessions.Values.Contains(session) || session.ExpiresAt <= DateTimeOffset.UtcNow) return false;
            // A restored domain can reconnect before the server notices the old socket died.
            session.Socket?.Abort();
            session.Socket = socket;
            session.RunProbeId = null;
            session.RunReady = false;
            session.LastConnectedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void Detach(EditorSession session, WebSocket socket)
    {
        lock (gate) { if (ReferenceEquals(session.Socket, socket)) session.Socket = null; }
    }

    public bool IsCurrent(EditorSession session, WebSocket socket)
    {
        lock (gate) return sessions.Values.Contains(session) && ReferenceEquals(session.Socket, socket);
    }

    public void Revoke(EditorSession session)
    {
        lock (gate)
        {
            foreach (var entry in sessions.Where(x => x.Value == session).ToArray()) sessions.Remove(entry.Key);
            session.Socket?.Abort();
        }
    }

    public IReadOnlyList<SessionView> List()
    {
        lock (gate)
        {
            Prune();
            return sessions.Values.Select(x => new SessionView(x.Id, x.ProjectId, x.ProjectName, x.UnityVersion,
                x.Socket?.State == WebSocketState.Open, x.LastConnectedAt)).ToList();
        }
    }

    public EditorSession? ConnectedProject(string projectId)
    {
        lock (gate) { Prune(); return sessions.Values.FirstOrDefault(x => x.ProjectId == projectId && x.Socket?.State == WebSocketState.Open); }
    }

    public EditorSession? ProjectSession(string projectId)
    {
        lock (gate) { Prune(); return sessions.Values.FirstOrDefault(x => x.ProjectId == projectId); }
    }

    public bool IsConnected(string sessionId)
    {
        lock (gate) return sessions.Values.Any(x => x.Id == sessionId && x.ExpiresAt > DateTimeOffset.UtcNow && x.Socket?.State == WebSocketState.Open);
    }

    public async Task SendAsync(EditorSession session, WebSocket socket, object message, CancellationToken ct)
    {
        await session.SendGate.WaitAsync(ct);
        try
        {
            if (!IsCurrent(session, socket)) throw new WebSocketException("Editor connection changed.");
            await socket.SendAsync(new ArraySegment<byte>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message,
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))), WebSocketMessageType.Text, true, ct);
        }
        finally { session.SendGate.Release(); }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in codes.Where(x => x.Value <= now).Select(x => x.Key).ToArray()) codes.Remove(key);
        foreach (var entry in sessions.Where(x => x.Value.ExpiresAt <= now).ToArray())
        {
            entry.Value.Socket?.Abort();
            sessions.Remove(entry.Key);
        }
    }
}
