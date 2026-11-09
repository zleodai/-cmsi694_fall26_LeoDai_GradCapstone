using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Formatting;

namespace PlaytestOps.Web.Streaming;

public static class StreamingEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 4, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static IServiceCollection AddBuildStreaming(this IServiceCollection services)
    {
        services.AddSingleton<StreamingArtifactValidator>();
        services.AddSingleton<SteamStreamingLauncher>();
        services.AddSingleton<ISteamStreamingLauncher>(provider => provider.GetRequiredService<SteamStreamingLauncher>());
        services.AddSingleton<StreamingSupervisor>();
        services.AddHostedService(provider => provider.GetRequiredService<StreamingSupervisor>());
        return services;
    }

    public static void MapBuildStreaming(this WebApplication app)
    {
        app.MapGet("/api/streaming/state", async (HttpContext context, StreamingSupervisor supervisor, ApplicationDbContext db) =>
        {
            if (!BrowserRequest(context)) return Results.StatusCode(403);
            var rows = await db.UnityBuilds.AsNoTracking().Where(b => b.State == "Succeeded")
                .OrderByDescending(b => b.FinishedAt).Take(50).ToArrayAsync(context.RequestAborted);
            var builds = rows.Select(build =>
            {
                var reason = StreamingArtifactValidator.CapabilityReason(build);
                return new { buildId = build.Id, projectName = build.ProjectName,
                    finishedUtc = build.FinishedAt is { } finished ? (DateTime?)PacificTime.Utc(finished) : null,
                    streamingCapable = reason is null, reason,
                    supportsArenaKcp = StreamingArtifactValidator.SupportsLaunchProfile(build, "arena-kcp"),
                    supportsArenaSteam = StreamingArtifactValidator.SupportsLaunchProfile(build, "arena-steam") };
            });
            var sessions = supervisor.Snapshots();
            return Results.Ok(new { builds, session = sessions.FirstOrDefault(), sessions, networkLatency = supervisor.LatencySnapshot() });
        });

        app.MapGet("/api/streaming/steam-environments", (HttpContext context, ISteamStreamingLauncher steam) =>
        {
            if (!BrowserRequest(context)) return Results.StatusCode(403);
            return Results.Ok(steam.InspectEnvironments());
        });

        app.MapPost("/api/streaming/start", async (HttpContext context, IAntiforgery antiforgery, StreamingSupervisor supervisor) =>
        {
            if (!await BrowserWriteAsync(context, antiforgery)) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType() || context.Request.ContentLength is > 4096) return Results.BadRequest(new { error = "Expected a small JSON build selection." });
            StreamingStartRequest? request;
            try
            {
                using var limit = new MemoryStream();
                var buffer = new byte[1024]; int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (limit.Length + read > 4096) return Results.BadRequest(new { error = "Streaming request is oversized." });
                    limit.Write(buffer, 0, read);
                }
                request = JsonSerializer.Deserialize<StreamingStartRequest>(limit.ToArray(), Json);
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid streaming request." }); }
            if (request is null || request.BuildId is null) return Results.BadRequest(new { error = "Select a persisted successful build." });
            var result = await supervisor.StartSessionAsync(request, RequestOrigin(context), context.RequestAborted);
            return result.Error is null ? Results.Ok(result.State) : Results.Json(new { error = result.Error, session = result.State }, statusCode: result.StatusCode);
        });

        app.MapPost("/api/streaming/stop", async (HttpContext context, IAntiforgery antiforgery, StreamingSupervisor supervisor) =>
        {
            if (!await BrowserWriteAsync(context, antiforgery)) return Results.StatusCode(403);
            var result = await supervisor.StopGroupAsync(context.RequestAborted);
            return result.Error is null ? Results.Ok(new { session = result.State, sessions = supervisor.Snapshots() })
                : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        });

        app.MapPost("/api/streaming/groups/{groupId}/latency", async (string groupId, HttpContext context, IAntiforgery antiforgery, StreamingSupervisor supervisor) =>
        {
            if (!await BrowserWriteAsync(context, antiforgery)) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType() || context.Request.ContentLength is > 1024)
                return Results.BadRequest(new { error = "Expected a small JSON latency configuration." });
            KcpLatencySettings? settings;
            try
            {
                using var body = new MemoryStream();
                var buffer = new byte[1024]; int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (body.Length + read > 1024) return Results.BadRequest(new { error = "Latency request is oversized." });
                    body.Write(buffer, 0, read);
                }
                settings = JsonSerializer.Deserialize<KcpLatencySettings>(body.ToArray(), Json);
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid latency configuration." }); }
            if (settings is null) return Results.BadRequest(new { error = "Select a latency configuration." });
            var result = await supervisor.ConfigureLatencyAsync(groupId, settings, context.RequestAborted);
            return result.Error is null ? Results.Ok(new { networkLatency = result.State })
                : Results.Json(new { error = result.Error, networkLatency = result.State }, statusCode: result.StatusCode);
        });

        app.MapPost("/api/streaming/{sessionId}/stop", async (string sessionId, HttpContext context, IAntiforgery antiforgery, StreamingSupervisor supervisor) =>
        {
            if (!await BrowserWriteAsync(context, antiforgery)) return Results.StatusCode(403);
            var result = await supervisor.StopSessionAsync(sessionId, context.RequestAborted);
            return result.Error is null ? Results.Ok(result.State) : Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
        });

        app.MapPost("/api/streaming/{sessionId}/viewer-grant", async (string sessionId, HttpContext context, IAntiforgery antiforgery, StreamingSupervisor supervisor) =>
        {
            if (!await BrowserWriteAsync(context, antiforgery)) return Results.StatusCode(403);
            var grant = supervisor.IssueViewerGrant(sessionId);
            return grant is null ? Results.Json(new { error = "The player is not ready or a viewer/grant already owns this session." }, statusCode: 409) : Results.Ok(grant);
        });

        app.MapGet("/api/streaming/runtime/{sessionId}/{instanceId}", async (string sessionId, string instanceId, HttpContext context, StreamingSupervisor supervisor) =>
        {
            NoStore(context);
            if (!IsLocalRequest(context) || context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
            if (!supervisor.AuthenticateRuntime(sessionId, instanceId, context.Request.Headers.Authorization.ToString(), RequestOrigin(context)))
            { context.Response.StatusCode = 401; return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await supervisor.RuntimeSocketAsync(sessionId, instanceId, socket, context.RequestAborted);
        });

        app.MapGet("/api/streaming/viewer/{sessionId}/{instanceId}", async (string sessionId, string instanceId, HttpContext context, StreamingSupervisor supervisor) =>
        {
            if (!BrowserRequest(context, requireOrigin: true)) { context.Response.StatusCode = 403; return; }
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            if (!supervisor.CanAcceptViewer(sessionId, instanceId)) { context.Response.StatusCode = 409; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await supervisor.ViewerSocketAsync(sessionId, instanceId, socket, context.RequestAborted);
        });
    }

    public static bool IsLocalRequest(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip) &&
        context.Request.Host.Host.Trim('[', ']').ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1";

    public static bool BrowserRequest(HttpContext context, bool requireOrigin = false)
    {
        NoStore(context);
        if (!IsLocalRequest(context) || context.Request.Headers["Sec-Fetch-Site"].ToString() == "cross-site") return false;
        var origins = context.Request.Headers.Origin;
        if (origins.Count == 0) return !requireOrigin;
        return origins.Count == 1 && string.Equals(origins[0], RequestOrigin(context), StringComparison.Ordinal);
    }
    private static string RequestOrigin(HttpContext context) => $"{context.Request.Scheme}://{context.Request.Host}";
    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
    private static async Task<bool> BrowserWriteAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!BrowserRequest(context, requireOrigin: true)) return false;
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
