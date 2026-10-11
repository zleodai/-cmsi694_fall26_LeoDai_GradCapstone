using System.Data.Common;
using System.Net.WebSockets;
using System.Text.Json;

namespace PlaytestOps.Web.Bridge;

public static class BridgeEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    public static void MapEditorBridge(this WebApplication app)
    {
        app.MapPost("/api/editor/pair", (PairRequest request, BridgeRegistry registry) =>
        {
            if (request.Code is null || request.Code.Length > 32 || !Guid.TryParseExact(request.ProjectId, "N", out _) ||
                string.IsNullOrWhiteSpace(request.ProjectName) || request.ProjectName.Length > 160 ||
                string.IsNullOrWhiteSpace(request.UnityVersion) || request.UnityVersion.Length > 64)
                return Results.BadRequest(new { error = "Invalid pairing fields." });
            var result = registry.Pair(request);
            return result is null ? Results.Json(new { error = "Invalid or expired pairing code." }, statusCode: 401) : Results.Ok(result);
        }).RequireRateLimiting("pair");

        app.MapDelete("/api/editor/session", (HttpContext context, BridgeRegistry registry) =>
        {
            var session = registry.Authenticate(context);
            if (session is null) return Results.Unauthorized();
            registry.Revoke(session);
            return Results.NoContent();
        });

        app.MapGet("/api/editor/connect", HandleSocket);
    }

    private static async Task HandleSocket(HttpContext context, BridgeRegistry registry, IServiceScopeFactory scopes, ILoggerFactory loggers, RunMonitor monitor, SourceService sources, VersionControlService versionControl)
    {
        var session = registry.Authenticate(context);
        if (session is null) { context.Response.StatusCode = 401; return; }
        // The protocol is for an Editor client, never cross-origin browser JavaScript.
        if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await registry.CatalogGate.WaitAsync(context.RequestAborted);
        try
        {
            if (!registry.Attach(session, socket)) { socket.Abort(); return; }
            await registry.SendAsync(session, socket, new { type = "session.ready", sessionId = session.Id, projectId = session.ProjectId, expiresAt = session.ExpiresAt }, context.RequestAborted);
        }
        finally { registry.CatalogGate.Release(); }
        monitor.Wake();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(session.ExpiresAt - DateTimeOffset.UtcNow);
        var ct = deadline.Token;
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var payload = await Receive(socket, ct);
                if (payload is null) break;
                if (!registry.IsCurrent(session, socket)) break;
                // Route supported messages only; never execute arbitrary Editor methods.
                try
                {
                    using var document = JsonDocument.Parse(payload);
                    if (sources.TryHandleReply(session, socket, document.RootElement, payload)) continue;
                    if (versionControl.TryHandleReply(session, socket, document.RootElement, payload)) continue;
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("type", out var messageType) && messageType.ValueKind == JsonValueKind.String && messageType.GetString() == "run.ready")
                    {
                        var requestId = document.RootElement.TryGetProperty("requestId", out var probe) && probe.ValueKind == JsonValueKind.String ? probe.GetString() : null;
                        await registry.CatalogGate.WaitAsync(ct);
                        try
                        {
                            if (!registry.IsCurrent(session, socket)) break;
                            if (requestId is not null && requestId == session.RunProbeId)
                            {
                                session.RunReady = true;
                                monitor.Wake();
                            }
                        }
                        finally { registry.CatalogGate.Release(); }
                        continue;
                    }
                    if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "run.update")
                    {
                        var update = JsonSerializer.Deserialize<RunUpdate>(payload, Json)!;
                        await registry.CatalogGate.WaitAsync(ct);
                        try
                        {
                            if (!registry.IsCurrent(session, socket)) break;
                            await using var scope = scopes.CreateAsyncScope();
                            var failure = await scope.ServiceProvider.GetRequiredService<RunService>().UpdateAsync(session, update, ct);
                            await registry.SendAsync(session, socket, failure is null
                                ? (object)new { type = "run.accepted", runId = update.RunId, sequence = update.Sequence }
                                : new { type = "error", runId = update.RunId, error = failure }, ct);
                        }
                        finally { registry.CatalogGate.Release(); }
                        continue;
                    }
                }
                catch (JsonException) { await registry.SendAsync(session, socket, new { type = "error", error = "Invalid JSON message." }, ct); continue; }
                catch (Exception ex) when (ex is DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
                {
                    loggers.CreateLogger("EditorBridge").LogError(ex, "Run persistence failed.");
                    // Reconnection replays the client's saved result; do not acknowledge a failed write.
                    socket.Abort(); break;
                }
                CatalogMessage? message;
                try { message = JsonSerializer.Deserialize<CatalogMessage>(payload, Json); }
                catch (JsonException) { await registry.SendAsync(session, socket, new { type = "error", requestId = "", error = "Invalid JSON message." }, ct); continue; }
                var error = message is null ? "A JSON object is required." : CatalogService.Validate(message);
                if (error is not null) { await registry.SendAsync(session, socket, new { type = "error", requestId = message?.RequestId ?? "", error }, ct); continue; }
                try
                {
                    await registry.CatalogGate.WaitAsync(ct);
                    try
                    {
                    if (!registry.IsCurrent(session, socket)) break;
                    // A short-lived DbContext per message avoids retaining every catalog entity for the socket lifetime.
                    await using var scope = scopes.CreateAsyncScope();
                    var archived = await scope.ServiceProvider.GetRequiredService<CatalogService>().ReplaceAsync(session, message!, ct);
                    await registry.SendAsync(session, socket, new { type = "catalog.accepted", requestId = message!.RequestId, count = message.Tests.Length, unavailableCount = archived }, ct);
                    }
                    finally { registry.CatalogGate.Release(); }
                }
                catch (Exception ex) when (ex is DbException or Microsoft.EntityFrameworkCore.DbUpdateException)
                {
                    loggers.CreateLogger("EditorBridge").LogError(ex, "Catalog persistence failed.");
                    await registry.SendAsync(session, socket, new { type = "error", requestId = message!.RequestId, error = "Catalog could not be saved. Retry discovery." }, ct);
                }
            }
            if (socket.State == WebSocketState.CloseReceived)
            {
                await session.SendGate.WaitAsync(ct);
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Disconnected", ct); }
                finally { session.SendGate.Release(); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidDataException or ObjectDisposedException)
        {
            socket.Abort();
        }
        finally
        {
            registry.Detach(session, socket);
            sources.DisconnectedSocket(session, socket);
            versionControl.DisconnectedSocket(session, socket);
        }
    }

    private static async Task<string?> Receive(WebSocket socket, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > 2 * 1024 * 1024)
                throw new InvalidDataException("Expected a text message of at most 2 MiB.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
