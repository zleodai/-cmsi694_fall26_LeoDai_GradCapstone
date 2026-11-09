namespace PlaytestOps.Web.Streaming;

/// <summary>Additional round-trip delay. Each direction samples half of these bounds.</summary>
public sealed record KcpLatencySettings(bool Enabled = false, int MinRttMs = 50, int MaxRttMs = 100)
{
    public static void Validate(KcpLatencySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.MinRttMs is < 0 or > 1000 || settings.MaxRttMs is < 0 or > 1000 || settings.MinRttMs > settings.MaxRttMs)
            throw new ArgumentException("Additional RTT must be an ordered range between 0 and 1000 milliseconds.", nameof(settings));
    }
}

public sealed record KcpLatencyRelaySnapshot(KcpLatencySettings Settings, long Revision, int QueuedPackets,
    long QueuedBytes, long ForwardedPackets, long DroppedPackets, bool ClientConnected, bool Stopped, string? Error = null);
