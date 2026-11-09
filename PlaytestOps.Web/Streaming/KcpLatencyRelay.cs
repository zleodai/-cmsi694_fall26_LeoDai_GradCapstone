using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PlaytestOps.Web.Streaming;

/// <summary>A single-client IPv4 loopback UDP link, including KCP acknowledgments and retransmissions.</summary>
public sealed class KcpLatencyRelay : IDisposable, IAsyncDisposable
{
    private sealed record Datagram(byte[] Payload, IPEndPoint Destination);

    private readonly object gate = new();
    private readonly Socket socket;
    private readonly IPEndPoint server;
    private readonly int maximumQueuedPackets;
    private readonly int maximumQueuedBytes;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private readonly PriorityQueue<Datagram, (long Due, long Order)> queue = new();
    private readonly Task receiving;
    private readonly Task forwarding;
    private IPEndPoint? client;
    private KcpLatencySettings settings = new();
    private int queuedBytes;
    private long revision, sequence, lastToServer, lastToClient, forwardedPackets, droppedPackets;
    private bool stopped;
    private int synchronizationDisposed;
    private string? error;

    public KcpLatencyRelay(int serverPort, int maximumQueuedPackets = 1024, int maximumQueuedBytes = 4 * 1024 * 1024)
    {
        if (serverPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(serverPort));
        if (maximumQueuedPackets is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(maximumQueuedPackets));
        if (maximumQueuedBytes is < 1 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumQueuedBytes));
        this.maximumQueuedPackets = maximumQueuedPackets;
        this.maximumQueuedBytes = maximumQueuedBytes;
        server = new IPEndPoint(IPAddress.Loopback, serverPort);
        socket = BindSocket(serverPort);
        LocalPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        receiving = ReceiveAsync();
        forwarding = ForwardAsync();
    }

    public int LocalPort { get; }

    public KcpLatencyRelaySnapshot Snapshot
    {
        get { lock (gate) return ReadSnapshot(); }
    }

    public KcpLatencyRelaySnapshot Configure(KcpLatencySettings configuration)
    {
        KcpLatencySettings.Validate(configuration);
        lock (gate)
        {
            if (stopped) throw new InvalidOperationException("The KCP latency relay has stopped.");
            settings = configuration;
            revision++;
            if (!configuration.Enabled)
            {
                // Flush under the routing gate before new packets can take the direct path.
                while (queue.TryDequeue(out var packet, out _))
                {
                    queuedBytes -= packet.Payload.Length;
                    Send(packet);
                }
                lastToServer = lastToClient = 0;
            }
            // Enabled changes affect new packets. Existing queued deadlines remain in force,
            // including when a lower delay is selected while older packets are pending.
            WakeForwarder();
            return ReadSnapshot();
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[65535];
        EndPoint source = new IPEndPoint(IPAddress.Loopback, 0);
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, source, stopping.Token);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows can deliver an ICMP port-unreachable as a UDP receive reset.
                    // The host may still be starting, so keep this same socket and endpoint.
                    continue;
                }
                if (received.RemoteEndPoint is not IPEndPoint sender) continue;
                lock (gate)
                {
                    if (stopped) return;
                    if (!sender.Address.Equals(IPAddress.Loopback)) { droppedPackets++; continue; }
                    bool fromServer = sender.Port == server.Port;
                    IPEndPoint destination;
                    if (fromServer)
                    {
                        if (client is null) { droppedPackets++; continue; }
                        destination = client;
                    }
                    else
                    {
                        client ??= new IPEndPoint(IPAddress.Loopback, sender.Port);
                        if (sender.Port != client.Port) { droppedPackets++; continue; }
                        destination = server;
                    }
                    // The receive buffer is reused. Every queued packet owns its bytes.
                    var packet = new Datagram(buffer.AsSpan(0, received.ReceivedBytes).ToArray(), destination);
                    if (!settings.Enabled)
                    {
                        Send(packet);
                        continue;
                    }
                    if (queue.Count >= maximumQueuedPackets || packet.Payload.Length > maximumQueuedBytes - queuedBytes)
                    {
                        droppedPackets++;
                        continue;
                    }
                    double delayMs = (settings.MinRttMs + Random.Shared.NextDouble() * (settings.MaxRttMs - settings.MinRttMs)) / 2d;
                    long due = Stopwatch.GetTimestamp() + (long)(delayMs * Stopwatch.Frequency / 1000d);
                    if (fromServer)
                    {
                        due = Math.Max(due, lastToClient);
                        lastToClient = due;
                    }
                    else
                    {
                        due = Math.Max(due, lastToServer);
                        lastToServer = due;
                    }
                    queue.Enqueue(packet, (due, sequence++));
                    queuedBytes += packet.Payload.Length;
                    WakeForwarder();
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            if (!IsStopped()) Stop("The KCP relay receive loop stopped unexpectedly.");
        }
        catch (SocketException exception)
        {
            if (!IsStopped()) Stop("The KCP relay could not receive UDP packets (" + exception.SocketErrorCode + ").");
        }
        catch (Exception)
        {
            Stop("The KCP relay receive loop failed.");
        }
    }

    private async Task ForwardAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                TimeSpan wait;
                lock (gate)
                {
                    if (stopped) return;
                    long now = Stopwatch.GetTimestamp();
                    while (queue.TryPeek(out _, out var next) && next.Due <= now)
                    {
                        var packet = queue.Dequeue();
                        queuedBytes -= packet.Payload.Length;
                        Send(packet);
                    }
                    wait = queue.TryPeek(out _, out var upcoming)
                        ? TimeSpan.FromMilliseconds(Math.Max(1d, (upcoming.Due - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency))
                        : Timeout.InfiniteTimeSpan;
                }
                await changed.WaitAsync(wait, stopping.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            if (!IsStopped()) Stop("The KCP relay scheduler stopped unexpectedly.");
        }
        catch (Exception)
        {
            Stop("The KCP relay scheduler failed.");
        }
    }

    // The socket is nonblocking, so a flush has a bounded number of immediate send attempts.
    // Backpressure and socket failures drop only that packet and remain visible in statistics.
    private void Send(Datagram packet)
    {
        try
        {
            int sent = socket.SendTo(packet.Payload, SocketFlags.None, packet.Destination);
            if (sent == packet.Payload.Length) forwardedPackets++;
            else droppedPackets++;
        }
        catch (SocketException) { droppedPackets++; }
        catch (ObjectDisposedException) { droppedPackets++; }
    }

    private void WakeForwarder()
    {
        if (changed.CurrentCount == 0) changed.Release();
    }

    private KcpLatencyRelaySnapshot ReadSnapshot() => new(settings, revision, queue.Count, queuedBytes,
        forwardedPackets, droppedPackets, client is not null, stopped, error);

    private bool IsStopped() { lock (gate) return stopped; }

    private void Stop(string? failure)
    {
        lock (gate)
        {
            if (stopped) return;
            stopped = true;
            error = failure;
            droppedPackets += queue.Count;
            queue.Clear();
            queuedBytes = 0;
        }
        stopping.Cancel();
        socket.Dispose();
    }

    private static Socket BindSocket(int serverPort)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                candidate.Blocking = false;
                candidate.ReceiveBufferSize = 1024 * 1024;
                candidate.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                // The host's selected port can be unbound while the game starts.
                if (((IPEndPoint)candidate.LocalEndPoint!).Port == serverPort)
                {
                    candidate.Dispose();
                    continue;
                }
                if (OperatingSystem.IsWindows())
                {
                    try { candidate.IOControl(unchecked((int)0x9800000C), new byte[4], null); }
                    catch (SocketException) { }
                }
                return candidate;
            }
            catch { candidate.Dispose(); throw; }
        }
        throw new InvalidOperationException("Could not allocate a KCP relay port distinct from the host port.");
    }

    public void Dispose()
    {
        Stop(null);
        try { Task.WhenAll(receiving, forwarding).Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        DisposeSynchronizationIfCompleted();
    }

    public async ValueTask DisposeAsync()
    {
        Stop(null);
        try { await Task.WhenAll(receiving, forwarding).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { }
        DisposeSynchronizationIfCompleted();
    }

    private void DisposeSynchronizationIfCompleted()
    {
        if (!receiving.IsCompleted || !forwarding.IsCompleted || Interlocked.Exchange(ref synchronizationDisposed, 1) != 0) return;
        stopping.Dispose();
        changed.Dispose();
    }
}
