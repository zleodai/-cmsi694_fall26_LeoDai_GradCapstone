using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PlaytestOps.Web.Streaming;

/// <summary>Owns disposable loopback datagram fixtures; never starts a game or changes machine networking.</summary>
internal static class KcpLatencyChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (var settings in new[] { new KcpLatencySettings(), new(true, 0, 0), new(true, 1000, 1000) })
            KcpLatencySettings.Validate(settings);
        check(true, "KCP latency accepts disabled defaults and inclusive zero/1000 ms RTT bounds");
        foreach (var settings in new KcpLatencySettings?[] { null, new(true, -1, 50), new(true, 0, 1001), new(false, 100, 50) })
        {
            try { KcpLatencySettings.Validate(settings!); check(false, "KCP latency rejects null or unordered/out-of-range configuration"); }
            catch (ArgumentException) { check(true, "KCP latency rejects null or unordered/out-of-range configuration"); }
        }
        foreach (var options in new[] { (0, 2, 128), (65536, 2, 128), (1, 0, 128), (1, 65537, 128), (1, 2, 0), (1, 2, 64 * 1024 * 1024 + 1) })
        {
            try { using var invalid = new KcpLatencyRelay(options.Item1, options.Item2, options.Item3); check(false, "KCP relay rejects invalid port or queue limits"); }
            catch (ArgumentException) { check(true, "KCP relay rejects invalid port or queue limits"); }
        }

        using var server = Bind();
        using var client = Bind();
        await using var relay = new KcpLatencyRelay(Port(server));
        int relayPort = relay.LocalPort;
        check(relayPort != Port(server) && !relay.Snapshot.Settings.Enabled && !relay.Snapshot.ClientConnected,
            "KCP relay starts disabled on a private port distinct from the host");
        var baseline = await Exchange(server, client, relayPort, "baseline");
        check(baseline.ServerSource == relayPort && baseline.ClientSource == relayPort && baseline.Payload == "baseline",
            "KCP relay preserves UDP payload and endpoint identity in both directions");
        check(relay.Snapshot.ClientConnected && relay.Snapshot.ForwardedPackets == 2 && relay.Snapshot.DroppedPackets == 0,
            "KCP relay pins the first client and reports actual forwarding");

        var applied = relay.Configure(new(true, 120, 120));
        var delayed = await Exchange(server, client, relayPort, "delayed");
        check(delayed.OutboundMs >= 45 && delayed.InboundMs >= 45 && delayed.OutboundMs + delayed.InboundMs >= 105,
            "Actual UDP packets receive half the configured RTT delay in each direction");
        check(applied.Revision == 1 && applied.Settings == new KcpLatencySettings(true, 120, 120) && relay.LocalPort == relayPort,
            "KCP latency applies live without changing the relay port");
        var beforeInvalid = relay.Snapshot;
        try { relay.Configure(new(true, 900, 100)); check(false, "Invalid live configuration leaves relay settings unchanged"); }
        catch (ArgumentException) { check(relay.Snapshot.Settings == beforeInvalid.Settings && relay.Snapshot.Revision == beforeInvalid.Revision,
            "Invalid live configuration leaves relay settings unchanged"); }
        relay.Configure(new(true, 40, 40));
        var changed = await Exchange(server, client, relayPort, "changed");
        check(changed.OutboundMs >= 12 && changed.InboundMs >= 12 && changed.Payload == "changed" && relay.Snapshot.Revision == 2,
            "An existing UDP session continues after its live delay changes");
        relay.Configure(new(true, 50, 100));
        for (int index = 0; index < 4; index++)
        {
            var variable = await Exchange(server, client, relayPort, "variable-" + index);
            check(variable.OutboundMs >= 18 && variable.InboundMs >= 18 && variable.Payload == "variable-" + index,
                "Variable RTT settings delay actual datagrams in both directions");
        }

        relay.Configure(new(true, 1000, 1000));
        await Send(client, relayPort, "queued-before-reset");
        await Until(() => relay.Snapshot.QueuedPackets == 1, "packet queued before reset");
        relay.Configure(new(true, 0, 0));
        await Send(client, relayPort, "queued-after-lowering");
        await Until(() => relay.Snapshot.QueuedPackets == 2, "lower-delay packet preserves FIFO");
        check(relay.Snapshot.QueuedPackets == 2 && relay.Snapshot.Settings.MinRttMs == 0,
            "Lowering enabled latency retains existing deadlines and queues later packets behind them");
        long pendingRevision = relay.Snapshot.Revision;
        var reset = relay.Configure(new(false, 50, 100));
        var flushed = await Receive(server);
        check(Encoding.UTF8.GetString(flushed.Buffer) == "queued-before-reset" && reset.QueuedPackets == 0 && reset.QueuedBytes == 0 &&
            !reset.Settings.Enabled && reset.Revision == pendingRevision + 1 && relay.LocalPort == relayPort,
            "Disabling latency flushes pending datagrams and preserves the active UDP port");
        var secondFlushed = await Receive(server);
        check(Encoding.UTF8.GetString(secondFlushed.Buffer) == "queued-after-lowering",
            "Reset flushes queued packets in directional FIFO order");
        var afterReset = await Exchange(server, client, relayPort, "after-reset");
        check(afterReset.Payload == "after-reset" && relay.Snapshot.QueuedPackets == 0,
            "The same UDP client continues through the direct path after reset");

        using (var stranger = Bind())
        {
            long dropped = relay.Snapshot.DroppedPackets;
            long forwarded = relay.Snapshot.ForwardedPackets;
            await Send(stranger, relayPort, "unrecognized-endpoint");
            await Until(() => relay.Snapshot.DroppedPackets > dropped, "unrecognized endpoint rejected");
            check(relay.Snapshot.ForwardedPackets == forwarded && relay.Snapshot.ClientConnected,
                "A second loopback endpoint cannot replace or inject into the pinned client link");
        }

        using (var independentClient = Bind())
        await using (var independentRelay = new KcpLatencyRelay(Port(server)))
        {
            relay.Configure(new(true, 1000, 1000));
            await Send(client, relayPort, "isolated-pending");
            await Until(() => relay.Snapshot.QueuedPackets == 1, "isolated pending packet");
            var independent = await Exchange(server, independentClient, independentRelay.LocalPort, "independent");
            check(independent.Payload == "independent" && independent.ServerSource != relayPort && !independentRelay.Snapshot.Settings.Enabled &&
                relay.Snapshot.QueuedPackets == 1, "One client's queued delay does not delay another client's relay");
            relay.Configure(new(false));
            var pending = await Receive(server);
            check(Encoding.UTF8.GetString(pending.Buffer) == "isolated-pending" && pending.RemoteEndPoint.Port == relayPort,
                "Separate relay ports preserve each client's server-side endpoint identity");
        }

        await CheckBounds(check, server);
        relay.Configure(new(true, 1000, 1000));
        await Send(client, relayPort, "discard-on-stop");
        await Until(() => relay.Snapshot.QueuedPackets == 1, "pending stop packet");
        await relay.DisposeAsync();
        check(relay.Snapshot.Stopped && relay.Snapshot.QueuedPackets == 0 && relay.Snapshot.QueuedBytes == 0 && relay.Snapshot.Error is null,
            "KCP relay shutdown discards pending datagrams and completes cleanly");
        using (var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, relayPort)))
            check(Port(rebound) == relayPort, "KCP relay shutdown releases its bound UDP port");
        try { relay.Configure(new()); check(false, "Stopped KCP relay rejects further live changes"); }
        catch (InvalidOperationException) { check(true, "Stopped KCP relay rejects further live changes"); }
    }

    private static async Task CheckBounds(Action<bool, string> check, UdpClient server)
    {
        using var boundedClient = Bind();
        await using (var bounded = new KcpLatencyRelay(Port(server), 2, 1024))
        {
            bounded.Configure(new(true, 1000, 1000));
            for (int index = 0; index < 24; index++) await Send(boundedClient, bounded.LocalPort, new string('x', 64));
            await Until(() => bounded.Snapshot.DroppedPackets > 0, "packet queue bound");
            var state = bounded.Snapshot;
            check(state.QueuedPackets <= 2 && state.QueuedBytes <= 128 && state.DroppedPackets > 0 && !state.Stopped,
                "KCP relay packet queue is bounded and reports overflow drops");
        }
        await using (var bounded = new KcpLatencyRelay(Port(server), 8, 32))
        {
            bounded.Configure(new(true, 1000, 1000));
            await Send(boundedClient, bounded.LocalPort, new string('x', 64));
            await Until(() => bounded.Snapshot.DroppedPackets == 1, "byte queue bound");
            check(bounded.Snapshot.QueuedPackets == 0 && bounded.Snapshot.QueuedBytes == 0,
                "KCP relay rejects a datagram exceeding its queued-byte budget");
        }
    }

    private static UdpClient Bind() => new(new IPEndPoint(IPAddress.Loopback, 0));
    private static int Port(UdpClient socket) => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    private static async Task Send(UdpClient socket, int port, string text) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), new IPEndPoint(IPAddress.Loopback, port));

    private static async Task<UdpReceiveResult> Receive(UdpClient socket)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await socket.ReceiveAsync(deadline.Token);
    }

    private static async Task<(double OutboundMs, double InboundMs, int ServerSource, int ClientSource, string Payload)>
        Exchange(UdpClient server, UdpClient client, int port, string payload)
    {
        long started = Stopwatch.GetTimestamp();
        await Send(client, port, payload);
        var request = await Receive(server);
        double outbound = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        await server.SendAsync(request.Buffer, request.RemoteEndPoint);
        var response = await Receive(client);
        return (outbound, Stopwatch.GetElapsedTime(started).TotalMilliseconds, request.RemoteEndPoint.Port,
            response.RemoteEndPoint.Port, Encoding.UTF8.GetString(response.Buffer));
    }

    private static async Task Until(Func<bool> condition, string phase)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("UDP fixture: " + phase);
            await Task.Delay(5);
        }
    }
}
