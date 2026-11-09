using System.Text;
using System.Text.Json;

namespace PlaytestOps.Web.Streaming;

// Bounded, secret-free sidecar history. It is informational and never authorizes
// reconnecting to or terminating a process after the dashboard has restarted.
internal sealed class StreamingAudit : IDisposable
{
    private readonly FileStream events;
    private readonly StreamWriter writer;
    private bool exhausted;
    private const int MaximumBytes = 1024 * 1024;
    public StreamingAudit(string contentRoot, string sessionId, string instanceId, string buildId, int width, int height)
    {
        var root = Path.Combine(contentRoot, "App_Data", "Streaming", sessionId);
        for (var path = root; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Streaming history must use ordinary local directories.");
        Directory.CreateDirectory(root);
        using (var metadata = new FileStream(Path.Combine(root, "session.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(metadata, new { sessionId, instanceId, buildId, width, height, startedUtc = DateTimeOffset.UtcNow, protocolVersion = StreamingProtocol.Version });
        events = new FileStream(Path.Combine(root, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        writer = new StreamWriter(events, new UTF8Encoding(false)) { AutoFlush = true };
    }
    public void Append(StreamingLog entry)
    {
        if (exhausted) return;
        try
        {
            if (events.Length >= MaximumBytes) { exhausted = true; return; }
            writer.WriteLine(JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException) { exhausted = true; }
    }
    public void Dispose() { try { writer.Dispose(); } catch (IOException) { events.Dispose(); } }
}
