namespace PlaytestOps.Web.Streaming;

public sealed record StreamingStartRequest(string BuildId, int Width = 1280, int Height = 720, bool Diagnostics = false,
    int InstanceCount = 1, string LaunchProfile = "single", string[]? SteamSandboxes = null);
public sealed record StreamingResult(StreamingState? State, string? Error, int StatusCode = 200);
public sealed record StreamingLog(long Sequence, DateTimeOffset Utc, string Level, string Message);
public sealed record StreamingState(string? SessionId, string? InstanceId, string? BuildId, string? ProjectName,
    string State, string Message, int Width, int Height, int FrameRate, int MaxBitrateKbps,
    int ActualWidth, int ActualHeight, bool RuntimeConnected, bool ViewerConnected, bool ControllerActive,
    DateTimeOffset? StartedUtc, DateTimeOffset? LastHeartbeatUtc,
    int? ExitCode, IReadOnlyList<StreamingLog> Logs, string? Error, bool Diagnostics, bool Exited,
    string? GroupId = null, int Index = 0, string Role = "single", string Label = "Single player",
    string LaunchProfile = "single", bool GameReady = false, bool MouseLockRequested = false);
public sealed record StreamingBrowserGrant(string SessionId, string InstanceId, string Token,
    string ViewerWebSocketPath, DateTimeOffset ExpiresUtc, object[] IceServers);
public sealed record StreamingLatencyState(string GroupId, bool Enabled, int MinRttMs, int MaxRttMs,
    long Revision, IReadOnlyList<StreamingLatencyLinkState> Links);
public sealed record StreamingLatencyLinkState(string InstanceId, int Index, string Label,
    int QueuedPackets, long QueuedBytes, long DroppedPackets, long ForwardedPackets,
    double? GameRttMs, double? GameRttStdDevMs, DateTimeOffset? GameRttSampleUtc);
public sealed record StreamingLatencyResult(StreamingLatencyState? State, string? Error, int StatusCode = 200);

public static class StreamingProtocol
{
    public const int Version = 1;
    public const string RuntimeAssembly = "Player_Data/Managed/PlaytestOps.Runtime.dll";
    public const string CapabilityFile = "playtestops-streaming.json";
    public const int MaximumMessageBytes = 65536;
    public const string AntiforgeryHeader = "RequestVerificationToken";
    // Acquisition: backend sends control.granted only to runtime; runtime replies
    // control.ready {leaseId} after installing it, then backend grants the viewer.
    // Browser sends control.heartbeat {leaseId} every 500 ms. The backend extends
    // the five-second lease and sends control.renewed {leaseId,expiresUtc} to both.
    // Runtime input liveness independently uses the shorter timeout below.
    public const int ControllerTimeoutMs = 1500;
    public const int ControllerLeaseSeconds = 5;
    public const int MaximumInstances = 4;
    public static bool ValidLaunchProfile(string? profile, int count) =>
        profile == "single" && count == 1 || profile is "arena-kcp" or "arena-steam" && count is >= 2 and <= MaximumInstances;
    public static bool ValidDimensions(int width, int height) =>
        width is >= 320 and <= 1920 && height is >= 180 and <= 1080 && width % 2 == 0 && height % 2 == 0;
}
