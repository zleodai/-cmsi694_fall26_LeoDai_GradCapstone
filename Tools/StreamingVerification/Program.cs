using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;
using PlaytestOps.Web.Streaming;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
var scratch = Path.Combine(Path.GetTempPath(), "PlaytestOps-stream-backend-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = scratch });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
builder.Services.AddAntiforgery(options => options.HeaderName = StreamingProtocol.AntiforgeryHeader);
builder.Services.AddBuildStreaming();
await using var app = builder.Build();
app.UseWebSockets(); app.MapBuildStreaming();
app.MapGet("/test-token", (HttpContext context, IAntiforgery anti) => Results.Ok(new { token = anti.GetAndStoreTokens(context).RequestToken }));
await using (var scope = app.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
try
{
    var context = new DefaultHttpContext();
    context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Request.Scheme = "http"; context.Request.Host = new HostString("127.0.0.1", 5000);
    Check(StreamingEndpoints.BrowserRequest(context), "loopback GET can omit Origin");
    Check(!StreamingEndpoints.BrowserRequest(context, true), "browser writes and WebSockets require Origin");
    context.Request.Headers.Origin = "http://127.0.0.1:5000";
    Check(StreamingEndpoints.BrowserRequest(context, true), "exact same-origin loopback request accepted");
    context.Request.Headers.Origin = "http://localhost:5000";
    Check(!StreamingEndpoints.BrowserRequest(context, true), "different loopback host is a different origin");
    context.Request.Host = new HostString("attacker.example");
    context.Request.Headers.Origin = "http://attacker.example";
    Check(!StreamingEndpoints.BrowserRequest(context, true), "DNS rebinding host rejected");
    context.Request.Host = new HostString("localhost"); context.Request.Headers.Origin = "http://localhost";
    context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.10");
    Check(!StreamingEndpoints.BrowserRequest(context, true), "remote IP rejected even with localhost Host");
    context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");
    Check(StreamingEndpoints.BrowserRequest(context, true), "mapped loopback IP accepted");
    Check(StreamingProtocol.ValidDimensions(1280, 720) && !StreamingProtocol.ValidDimensions(1281, 720) && !StreamingProtocol.ValidDimensions(1922, 1080), "capture bounds and even dimensions enforced");
    var initial = await http.GetFromJsonAsync<JsonElement>("/api/streaming/state");
    Check(initial.GetProperty("session").ValueKind == JsonValueKind.Null && initial.GetProperty("builds").GetArrayLength() == 0 && initial.GetProperty("sessions").GetArrayLength() == 0, "state returns builds, legacy session, and empty sessions");
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/streaming/state"))
    {
        request.Headers.Host = "attacker.example";
        Check((await http.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "HTTP state rejects unapproved Host");
    }
    using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/streaming/state"))
    {
        request.Headers.Add("Origin", "https://attacker.example");
        Check((await http.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "HTTP state rejects cross-origin read");
    }
    Check((await http.PostAsJsonAsync("/api/streaming/start", new { buildId = Guid.NewGuid().ToString("N") })).StatusCode == HttpStatusCode.Forbidden, "start rejects missing Origin and antiforgery");
    var token = (await http.GetFromJsonAsync<JsonElement>("/test-token")).GetProperty("token").GetString()!;
    async Task<HttpResponseMessage> Post(string path, object body, bool withToken = true, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", origin ?? address);
        if (withToken) request.Headers.Add(StreamingProtocol.AntiforgeryHeader, token);
        return await http.SendAsync(request);
    }
    Check((await Post("/api/streaming/start", new { buildId = Guid.NewGuid().ToString("N") }, false)).StatusCode == HttpStatusCode.Forbidden, "same-origin start still requires antiforgery");
    Check((await Post("/api/streaming/start", new { buildId = Guid.NewGuid().ToString("N") }, origin: "https://attacker.example")).StatusCode == HttpStatusCode.Forbidden, "valid antiforgery cannot bypass Origin");
    Check((await Post("/api/streaming/start", new { buildId = Guid.NewGuid().ToString("N") })).StatusCode == HttpStatusCode.BadRequest, "unknown persisted build cannot launch");
    Check((await Post("/api/streaming/start", new { buildId = Guid.NewGuid().ToString("N"), width = 1281, height = 720 })).StatusCode == HttpStatusCode.BadRequest, "HTTP start rejects odd resolution");
    Check((await Post("/api/streaming/start", new { buildId = Guid.NewGuid().ToString("N"), executablePath = "C:/Windows/System32/cmd.exe" })).StatusCode == HttpStatusCode.BadRequest, "arbitrary executable fields rejected");
    Check((await Post("/api/streaming/unknown/viewer-grant", new { })).StatusCode == HttpStatusCode.Conflict, "no viewer grant without a ready owned player");
    Check((await Post("/api/streaming/unknown/stop", new { }, false)).StatusCode == HttpStatusCode.Forbidden, "stop also requires antiforgery");
    Check((await Post("/api/streaming/unknown/stop", new { })).StatusCode == HttpStatusCode.NotFound, "stop cannot target an unrelated process");
    Check((await http.GetAsync("/api/streaming/runtime/unknown/unknown")).StatusCode == HttpStatusCode.Unauthorized, "runtime socket requires current child bearer");

    var build = new UnityBuild { Id = Guid.NewGuid().ToString("N"), RequestKey = Guid.NewGuid().ToString("N"), ProjectId = "fixture",
        ProjectName = "Backend fixture", State = "Succeeded", Revision = "fixture-revision", ActiveTarget = "StandaloneWindows64", FinishedAt = DateTime.UtcNow };
    build.OutputPath = Path.Combine(scratch, "Builds", "PlaytestOps", build.Id);
    build.ExecutablePath = Path.Combine(build.OutputPath, "Player.exe");
    build.ManifestPath = Path.Combine(build.OutputPath, "build-manifest.json");
    Directory.CreateDirectory(build.OutputPath);
    await File.WriteAllBytesAsync(build.ExecutablePath, [1, 2, 3, 4]); // Data fixture, never launched.
    var marker = Path.Combine(build.OutputPath, StreamingProtocol.CapabilityFile);
    var guid = Guid.NewGuid().ToString("N");
    await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { protocolVersion = 1, packageVersion = "0.1.0", buildGuid = guid, capture = "framebuffer", input = "input-system", transport = "webrtc" }));
    var native = Path.Combine(build.OutputPath, "GameAssembly.dll");
    await File.WriteAllBytesAsync(native, [1, 2, 3, 4]);
    object Entry(string path) => new { path = Path.GetFileName(path), bytes = new FileInfo(path).Length, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
    await File.WriteAllTextAsync(build.ManifestPath, JsonSerializer.Serialize(new { protocolVersion = 1, buildId = build.Id, result = "Succeeded", totalErrors = 0, artifactListTruncated = false,
        activeTarget = build.ActiveTarget, revision = build.Revision, buildGuid = guid, artifacts = new[] { Entry(build.ExecutablePath), Entry(marker), Entry(native) } }));
    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(); db.UnityBuilds.Add(build); await db.SaveChangesAsync();
    }
    var validator = app.Services.GetRequiredService<StreamingArtifactValidator>();
    using (var approved = await validator.ValidateAsync(build.Id, CancellationToken.None))
    {
        Check(approved.ExecutablePath == build.ExecutablePath, "persisted successful build and capability/hash validate");
        try { using var write = new FileStream(build.ExecutablePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); Check(false, "executable stays pinned"); }
        catch (IOException) { Check(true, "approved executable cannot be modified while pinned"); }
    }
    var listing = await http.GetFromJsonAsync<JsonElement>("/api/streaming/state");
    Check(listing.GetProperty("builds")[0].GetProperty("streamingCapable").GetBoolean(), "successful compatible build is selectable");
    await File.WriteAllBytesAsync(build.ExecutablePath, [4, 3, 2, 1]);
    try { using var invalid = await validator.ValidateAsync(build.Id, CancellationToken.None); Check(false, "mutated hash rejected"); }
    catch (InvalidDataException ex) { Check(ex.Message.Contains("SHA-256"), "mutated executable rejected before process launch"); }
    await File.WriteAllBytesAsync(build.ExecutablePath, [1, 2, 3, 4]);
    await File.WriteAllBytesAsync(native, [4, 3, 2, 1]);
    try { using var invalid = await validator.ValidateAsync(build.Id, CancellationToken.None); Check(false, "mutated native identity rejected"); }
    catch (InvalidDataException ex) { Check(ex.Message.Contains("SHA-256"), "populated native identity hash is enforced"); }
    await File.WriteAllBytesAsync(native, [1, 2, 3, 4]);
    var markerText = await File.ReadAllTextAsync(marker);
    await File.WriteAllTextAsync(marker, markerText.Replace("0.1.0", "0.1.1"));
    try { using var invalid = await validator.ValidateAsync(build.Id, CancellationToken.None); Check(false, "mutated capability rejected"); }
    catch (InvalidDataException ex) { Check(ex.Message.Contains("SHA-256"), "capability marker requires approved SHA-256"); }
    File.Delete(marker);
    Check(StreamingArtifactValidator.CapabilityReason(build) is not null, "missing runtime capability explains rebuild requirement");
    var stateResponse = await http.GetAsync("/api/streaming/state");
    Check(stateResponse.Headers.CacheControl?.NoStore == true, "state response is no-store");
    Check(app.Services.GetRequiredService<StreamingSupervisor>().Snapshot() is null, "rejected requests never create an owned process/session");
    await SteamLauncherChecks.Run(scratch, Check);
    await SteamLifecycleChecks.Run(Check);
    await KcpLatencyChecks.Run(Check);
    if (args.Length == 2 && args[0] == "--fixture")
        await MultistreamChecks.Run(http, address, token, app.Services, scratch, Path.GetFullPath(args[1]), Check);
    Console.WriteLine($"Passed {checks} backend checks using isolated HTTP, WebSocket, UDP and owned-process fixtures; no Unity player, WebRTC video, KCP game, or input path was exercised.");
}
finally
{
    await app.StopAsync();
    // Keep the bounded disposable fixture for inspection; no user files are touched.
    Console.WriteLine("Fixture: " + scratch);
}
