using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PlaytestOps.Web.Streaming;

internal sealed class StreamingPeer(WebSocket socket) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<string> outgoing = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource closed = new();
    private readonly Task sender = Task.CompletedTask;
    private Task? sending;
    public WebSocket Socket { get; } = socket;
    public CancellationToken Closed => closed.Token;
    public void Start() => sending = SendLoopAsync();
    public bool Send(object message)
    {
        if (!closed.IsCancellationRequested && outgoing.Writer.TryWrite(JsonSerializer.Serialize(message, Json))) return true;
        Abort();
        return false;
    }
    public void Abort()
    {
        closed.Cancel(); outgoing.Writer.TryComplete(); Socket.Abort();
    }
    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in outgoing.Reader.ReadAllAsync(closed.Token))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await Socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, deadline.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { Abort(); }
    }
    public async ValueTask DisposeAsync()
    {
        Abort();
        await (sending ?? sender);
        Socket.Dispose(); closed.Dispose();
    }
    public static async Task<JsonDocument?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > StreamingProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Streaming accepts bounded JSON signaling only.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        var document = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose(); throw new InvalidDataException("Expected a signaling object.");
    }
}
