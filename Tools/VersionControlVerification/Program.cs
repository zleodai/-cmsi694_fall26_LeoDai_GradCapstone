using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlaytestOps.Editor;

try
{
var root = Path.GetFullPath(args[0]);
var appRoot = Path.GetFullPath(args[1]);
var preview = args.Contains("--preview");
var pureOnly = args.Contains("--pure-only");
var httpOnly = args.Contains("--http-only");
if (pureOnly && httpOnly) throw new ArgumentException("Choose either --pure-only or --http-only, not both.");
var fixture = Path.Combine(root, "vcs-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++; Console.WriteLine("PASS " + label);
}
if (!httpOnly) await PureChecks.RunAsync(fixture, Check);
if (pureOnly) { Console.WriteLine($"UVCS PURE VERIFICATION COMPLETE: {checks} checks passed."); return; }

const string marker = "VCS-FIXTURE-WORKSPACE";
const string sourceAttack = "<script>alert('uvcs')</script> & \"quoted\" café 😀";
JsonObject Snapshot(int offset = 0, string scope = "repository", int historyCount = 50, bool hasMore = true)
{
    if (offset >= 120) { historyCount = 0; hasMore = false; }
    object Changeset(long number, string comment, string branch = "/main") => new {
        number, dateUtc = "2026-10-07T20:00:00.0000000Z", author = "alice <reviewer>", comment, branch
    };
    return JsonSerializer.SerializeToNode(new {
        protocolVersion = 1, capturedAtUtc = "2026-10-07T20:01:00.0000000Z", state = "available", errorCode = "",
        workspace = new { name = marker, repository = "Portfolio@cloud", branch = "/main", selectorType = "Branch", currentChangeset = 118L, headChangeset = 120L, workspaceType = "Regular" },
        pending = new { state = "available", errorCode = "", items = new[] {
            new { status = "CO+CH", path = "Assets/Scripts/Player.cs", oldPath = "" },
            new { status = "Private", path = "Assets/Scripts/未提交.cs", oldPath = "" },
            new { status = "Added", path = "Assets/Added.asset", oldPath = "" },
            new { status = "Deleted", path = "Assets/Deleted.asset.meta", oldPath = "" },
            new { status = "Moved", path = "Assets/New.cs", oldPath = "Assets/Old.cs" },
            new { status = "Changed", path = "Assets/Scripts/<img src=x onerror='window.__uvcsXss=true'>.cs", oldPath = "" }
        }, truncated = false },
        incoming = new { state = "available", errorCode = "", changesets = new[] { Changeset(119, "INCOMING-119"), Changeset(120, "INCOMING-120") }, truncated = false },
        history = new { state = "available", errorCode = "", changesets = Enumerable.Range(0, historyCount).Select(index => Changeset(120L - offset - index, index == 0 ? sourceAttack : "HISTORY-" + (120 - offset - index))).ToArray(), hasMore, offset, scope }
    })!.AsObject();
}
JsonObject Unavailable(string state, int offset = 0, string scope = "repository")
{
    var snapshot = Snapshot(offset, scope, 0, false);
    snapshot["state"] = state; snapshot["errorCode"] = "fixtureUnavailable";
    foreach (var section in new[] { "pending", "incoming", "history" }) {
        snapshot[section]!["state"] = "unavailable"; snapshot[section]!["errorCode"] = "fixtureUnavailable";
        snapshot[section]![section == "pending" ? "items" : "changesets"] = new JsonArray();
    }
    snapshot["workspace"]!["name"] = "";
    return snapshot;
}
var address = "http://127.0.0.1:5294";
var projectId = Guid.NewGuid().ToString("N");
var url = "/VersionControl?projectId=" + projectId;
var serverLog = new List<string>();
Process? server = null;
ClientWebSocket? socket = null;
using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(65) };
async Task<JsonElement> Receive(ClientWebSocket target, bool keepOpen = false)
{
    using var timeout = new CancellationTokenSource(keepOpen ? 600000 : 15000);
    using var stream = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult result;
    do {
        result = await target.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
        if (result.MessageType == WebSocketMessageType.Close) throw new Exception("Fixture socket closed.");
        if (stream.Length + result.Count > 65536) throw new Exception("Fixture received an oversized command.");
        stream.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
}
Task Write(ClientWebSocket target, object payload) => target.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(payload)), WebSocketMessageType.Text, true, CancellationToken.None);
Task Reply(ClientWebSocket target, string requestId, JsonObject snapshot) => Write(target, new { type = "vcs.snapshot.result", requestId, snapshot });
async Task<ClientWebSocket> Connect(string token)
{
    var connection = new ClientWebSocket(); connection.Options.SetRequestHeader("Authorization", "Bearer " + token);
    await connection.ConnectAsync(new Uri("ws://127.0.0.1:5294/api/editor/connect"), CancellationToken.None);
    if ((await Receive(connection)).GetProperty("type").GetString() != "session.ready") throw new Exception("Unexpected fixture handshake.");
    return connection;
}
async Task<string> Pair(string id, string name)
{
    var page = await client.GetStringAsync("/Editors");
    var anti = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    var generated = await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent(new Dictionary<string,string> { ["__RequestVerificationToken"] = anti }));
    generated.EnsureSuccessStatusCode();
    var code = Regex.Match(await generated.Content.ReadAsStringAsync(), "id=\"pairing-code\">([^<]+)").Groups[1].Value;
    var paired = await client.PostAsJsonAsync("/api/editor/pair", new { code, projectId = id, projectName = name, unityVersion = "6000.6.0f1" });
    paired.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await paired.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
}
async Task<string> Page(JsonObject? snapshot = null, int offset = 0, string scope = "repository")
{
    var task = client.GetAsync(url + "&offset=" + offset + "&scope=" + scope);
    var command = await Receive(socket!);
    Check(command.GetProperty("type").GetString() == "vcs.snapshot" && Guid.TryParseExact(command.GetProperty("requestId").GetString(), "N", out _) &&
        command.GetProperty("offset").GetInt32() == offset && command.GetProperty("scope").GetString() == scope, "snapshot request relays bounded history offset and scope with correlation ID");
    await Reply(socket!, command.GetProperty("requestId").GetString()!, snapshot ?? Snapshot(offset, scope));
    using var response = await task;
    response.EnsureSuccessStatusCode();
    Check(response.Headers.CacheControl?.NoStore == true, "version-control snapshot response is no-store");
    return await response.Content.ReadAsStringAsync();
}
async Task InvalidReply(Action<JsonObject> change, string label)
{
    var value = Snapshot(); change(value);
    var page = await Page(value);
    Check(!page.Contains(marker) && !page.Contains("INCOMING-119") && page.Contains("invalid", StringComparison.OrdinalIgnoreCase), label);
}
try
{
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = appRoot, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Path.Combine(root, "build/PlaytestOps.Web.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(address);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ConnectionStrings__DefaultConnection"] = "Data Source=" + Path.Combine(fixture, "vcs-check.db");
    server = Process.Start(start)!;
    server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (serverLog) serverLog.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (serverLog) serverLog.Add(e.Data); };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    var ready = false;
    for (var attempt = 0; attempt < 100; attempt++) {
        if (server.HasExited) throw new Exception("Verification server exited during startup.");
        try { if ((await client.GetAsync("/")).IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    Check(ready, "isolated UVCS verification server starts with disposable database");
    Check((await client.GetStringAsync("/VersionControl")).Contains("No Unity Editor connected"), "disconnected dashboard has explicit no-Editor state");
    using (var badHost = new HttpRequestMessage(HttpMethod.Get, "/VersionControl")) {
        badHost.Headers.Host = "attacker.example";
        Check((await client.SendAsync(badHost)).StatusCode == HttpStatusCode.Forbidden, "UVCS dashboard rejects non-local Host before disclosure");
    }
    var token = await Pair(projectId, "UVCS verification fixture");
    Check((await client.GetStringAsync(url)).Contains("connected", StringComparison.OrdinalIgnoreCase) && !(await client.GetStringAsync(url)).Contains(marker), "pairing alone cannot disclose a saved UVCS snapshot");
    socket = await Connect(token);
    foreach (var query in new[] { "&offset=-1", "&offset=100001", "&offset=bad", "&scope=invalid", "&scope=Branch" }) {
        using var rejected = await client.GetAsync(url + query);
        Check(rejected.StatusCode == HttpStatusCode.BadRequest, "invalid history query is rejected without relay: " + query);
    }
    var html = await Page();
    Check(html.Contains(marker) && html.Contains("Portfolio@cloud") && html.Contains("/main") && html.Contains("118") && html.Contains("120"), "workspace identity and loaded/head display changeset numbers render");
    var decoded = WebUtility.HtmlDecode(html);
    var pendingPredicates = new { combined = decoded.Contains("CO+CH"), @private = decoded.Contains("Private"), deleted = decoded.Contains("Deleted"), movedFrom = decoded.Contains("Assets/Old.cs"), incoming = decoded.Contains("INCOMING-119") };
    if (!pendingPredicates.combined || !pendingPredicates.@private || !pendingPredicates.deleted || !pendingPredicates.movedFrom || !pendingPredicates.incoming) {
        Console.WriteLine("SYNTHETIC SNAPSHOT PREDICATES: " + JsonSerializer.Serialize(pendingPredicates));
        File.WriteAllText(Path.Combine(fixture, "synthetic-snapshot-page.html"), html, new UTF8Encoding(false));
        Console.WriteLine("SYNTHETIC SNAPSHOT HTML: " + Path.Combine(fixture, "synthetic-snapshot-page.html"));
    }
    Check(pendingPredicates.combined && pendingPredicates.@private && pendingPredicates.deleted && pendingPredicates.movedFrom && pendingPredicates.incoming, "pending mixed states, moves, meta files and incoming changesets render");
    Check(html.Contains("&lt;script&gt;") && !html.Contains("<script>alert('uvcs')</script>") && html.Contains("&lt;img") && !html.Contains("<img src=x onerror="), "UVCS comments, authors and workspace-relative paths are HTML encoded");
    Check(!html.Contains("hx-trigger=\"every") && !html.Contains("hx-post") && !html.Contains("contenteditable") && !html.Contains("method=\"post\""), "UVCS page has no idle polling or mutation forms");
    Check(html.Contains("data-vcs-page=\"next\"") && !html.Contains("data-vcs-page=\"previous\""), "first history page offers only forward pagination");
    html = await Page(Snapshot(50, "branch", 2, false), 50, "branch");
    Check(html.Contains("data-vcs-offset=\"50\"") && html.Contains("data-vcs-scope=\"branch\"") && html.Contains("data-vcs-page=\"previous\"") && !html.Contains("data-vcs-page=\"next\""), "branch history page preserves offset/scope with bounded backward pagination");
    Check((await Page(Snapshot(100000, "branch"), 100000, "branch")).Contains("data-vcs-offset=\"100000\""), "maximum allowed history offset relays without overflow");
    var sameHead = Snapshot(0, "repository", 0, false);
    sameHead["workspace"]!["currentChangeset"] = 120; sameHead["incoming"]!["changesets"] = new JsonArray(); sameHead["pending"]!["items"] = new JsonArray();
    html = await Page(sameHead);
    Check(html.Contains("No incoming") && !html.Contains("INCOMING-119"), "known head equal to loaded revision displays a real empty incoming state");
    var zeroLoaded = Snapshot(0, "repository", 1, false);
    zeroLoaded["workspace"]!["currentChangeset"] = 0; zeroLoaded["workspace"]!["headChangeset"] = 1;
    zeroLoaded["incoming"]!["changesets"] = JsonSerializer.SerializeToNode(new[] { new { number = 1L, dateUtc = "2026-10-07T20:00:00Z", author = "alice", comment = "ZERO-TO-ONE-INCOMING", branch = "/main" } });
    html = await Page(zeroLoaded);
    Check(html.Contains("ZERO-TO-ONE-INCOMING"), "changeset zero is a valid loaded revision with a positive incoming successor");
    var partial = Snapshot();
    foreach (var section in new[] { "pending", "incoming", "history" }) {
        partial[section]!["state"] = "unavailable"; partial[section]!["errorCode"] = "authFailed";
        partial[section]![section == "pending" ? "items" : "changesets"] = new JsonArray();
    }
    partial["history"]!["hasMore"] = false;
    html = await Page(partial);
    Check(html.Contains(marker) && html.Contains("unavailable", StringComparison.OrdinalIgnoreCase) && !html.Contains("INCOMING-119") && !html.Contains("Assets/Old.cs"), "partial errors retain workspace identity but never appear as stale successful empty sections");
    foreach (var state in new[] { "notUvcs", "missingClient", "busy", "failed" }) {
        html = await Page(Unavailable(state));
        Check(!html.Contains(marker) && !html.Contains("INCOMING-119"), "top-level " + state + " state contains no stale snapshot data");
    }
    var unsupported = Snapshot(); unsupported["incoming"]!["state"] = "unsupported"; unsupported["incoming"]!["errorCode"] = "pinnedSelector"; unsupported["incoming"]!["changesets"] = new JsonArray();
    html = await Page(unsupported);
    Check(html.Contains("unsupported", StringComparison.OrdinalIgnoreCase) && !html.Contains("INCOMING-119"), "unsupported incoming capability is distinct from an empty successful list");
    await InvalidReply(value => value["protocolVersion"] = 2, "unknown snapshot protocol is rejected");
    await InvalidReply(value => value["capturedAtUtc"] = "2026-10-07T20:00:00+01:00", "non-UTC snapshot time is rejected");
    await InvalidReply(value => value["history"]!["offset"] = 50, "mismatched response history offset is rejected");
    await InvalidReply(value => value["history"]!["scope"] = "branch", "mismatched response history scope is rejected");
    await InvalidReply(value => value["workspace"]!["name"] = new string('x', 257), "oversized workspace metadata is rejected");
    await InvalidReply(value => value["pending"]!["items"]![0]!["path"] = "../outside.asset", "traversing pending paths are rejected");
    await InvalidReply(value => value["pending"]!["state"] = "unavailable", "unavailable section with retained items is rejected");
    await InvalidReply(value => value["history"]!["changesets"]![0]!["comment"] = new string('x', 4097), "oversized changeset comment is rejected");
    await InvalidReply(value => value.Remove("workspace"), "missing required snapshot object is rejected");
    await InvalidReply(value => value["incoming"]!["changesets"] = new JsonArray(), "available-empty incoming while behind is rejected as an unknown comparison");
    await InvalidReply(value => value["incoming"]!["changesets"]![0]!["number"] = 118, "incoming revision cannot be the already loaded changeset");
    await InvalidReply(value => value["incoming"]!["changesets"]![0]!["number"] = 121, "incoming revision cannot exceed the branch head");
    await InvalidReply(value => value["history"]!["changesets"]!.AsArray().Add(value["history"]!["changesets"]![0]!.DeepClone()), "history response with more than 50 changesets is rejected");
    await InvalidReply(value => value["pending"]!["items"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 2001).Select(i => new { status = "Changed", path = "Assets/Fixture" + i + ".cs", oldPath = "" })), "pending response with more than 2000 items is rejected");
    await InvalidReply(value => value["workspace"]!["repository"] = "bad\nmetadata", "control characters in repository metadata are rejected");
    var waitPage = client.GetStringAsync(url); var waitRequest = await Receive(socket);
    await Reply(socket, Guid.NewGuid().ToString("N"), Snapshot()); await Task.Delay(150);
    Check(!waitPage.IsCompleted, "unknown correlation ID cannot satisfy a pending snapshot");
    await Reply(socket, waitRequest.GetProperty("requestId").GetString()!, Snapshot());
    Check((await waitPage).Contains(marker), "matching owner reply completes pending snapshot");
    var otherId = Guid.NewGuid().ToString("N"); var otherToken = await Pair(otherId, "Foreign UVCS fixture");
    using (var foreign = await Connect(otherToken)) {
        var ownedPage = client.GetStringAsync(url); var ownedRequest = await Receive(socket);
        await Reply(foreign, ownedRequest.GetProperty("requestId").GetString()!, Snapshot()); await Task.Delay(150);
        Check(!ownedPage.IsCompleted, "foreign authenticated Editor cannot answer a known snapshot correlation ID");
        await Reply(socket, ownedRequest.GetProperty("requestId").GetString()!, Snapshot());
        Check((await ownedPage).Contains(marker), "owner snapshot still succeeds after a foreign-session response");
        foreign.Abort();
    }
    var firstPage = client.GetStringAsync(url); var firstRequest = await Receive(socket);
    var parallelPage = await client.GetStringAsync(url);
    Check(parallelPage.Contains("already", StringComparison.OrdinalIgnoreCase) && !parallelPage.Contains(marker), "concurrent snapshot for one project is rejected without stale data");
    await Reply(socket, firstRequest.GetProperty("requestId").GetString()!, Snapshot()); await firstPage;
    var timedPage = client.GetStringAsync(url); var timedRequest = await Receive(socket);
    var timer = Stopwatch.StartNew(); html = await timedPage; timer.Stop();
    Check(timer.Elapsed < TimeSpan.FromSeconds(40) && html.Contains("30 seconds") && !html.Contains(marker), "unanswered snapshot reaches bounded readable broker timeout");
    await Reply(socket, timedRequest.GetProperty("requestId").GetString()!, Snapshot());
    Check((await Page()).Contains(marker), "late timed-out reply is ignored before a separately correlated fresh snapshot");
    using (var cancel = new CancellationTokenSource(300)) {
        var cancelledPage = client.GetAsync(url, cancel.Token); var cancelledRequest = await Receive(socket);
        try { await cancelledPage; Check(false, "HTTP cancellation observed"); } catch (OperationCanceledException) { Check(true, "HTTP cancellation observed"); }
        await Task.Delay(200);
        await Reply(socket, cancelledRequest.GetProperty("requestId").GetString()!, Snapshot());
        Check((await Page()).Contains(marker), "aborted HTTP snapshot releases request ownership and does not cache late data");
    }
    var oldSocket = socket;
    var replacedPage = client.GetStringAsync(url); var replacedRequest = await Receive(oldSocket);
    socket = await Connect(token);
    await Reply(socket, replacedRequest.GetProperty("requestId").GetString()!, Snapshot());
    html = await replacedPage.WaitAsync(TimeSpan.FromSeconds(3));
    Check(!html.Contains(marker) && (html.Contains("disconnected", StringComparison.OrdinalIgnoreCase) || html.Contains("changed", StringComparison.OrdinalIgnoreCase)), "socket replacement cancels old snapshot and rejects a reply on the new socket");
    oldSocket.Abort(); oldSocket.Dispose();
    Check((await Page()).Contains(marker), "replacement socket serves only a fresh explicitly requested snapshot");
    var disconnectedPage = client.GetStringAsync(url); await Receive(socket); socket.Abort(); socket.Dispose();
    Check(!(await disconnectedPage).Contains(marker), "disconnect during request clears pending snapshot response");
    Check(!(await client.GetStringAsync(url)).Contains(marker), "disconnected project cannot serve cached UVCS metadata");
    socket = await Connect(token);
    Console.WriteLine($"UVCS VERIFICATION COMPLETE: {checks} checks passed.");
    if (preview) {
        Console.WriteLine("PREVIEW READY: " + address + url);
        while (true) {
            JsonElement command;
            try { command = await Receive(socket, true); }
            catch (OperationCanceledException) { Console.WriteLine("Preview idle deadline reached; completed verification remains successful."); break; }
            var offset = command.GetProperty("offset").GetInt32(); var scope = command.GetProperty("scope").GetString()!;
            await Reply(socket, command.GetProperty("requestId").GetString()!, Snapshot(offset, scope, offset == 0 ? 50 : 2, offset == 0));
        }
    }
}
catch { lock (serverLog) foreach (var line in serverLog.TakeLast(12)) Console.Error.WriteLine(line); throw; }
finally {
    socket?.Abort(); socket?.Dispose();
    if (server is { HasExited: false }) { server.Kill(true); server.WaitForExit(); }
    server?.Dispose();
    File.WriteAllLines(Path.Combine(root, "vcs-check-server.log"), serverLog);
}
}
catch (Exception error)
{
    Console.Error.WriteLine("UVCS VERIFICATION FAILED: " + error);
    Environment.ExitCode = 1;
}

static class PureChecks
{
    public static async Task RunAsync(string fixture, Action<bool, string> check)
    {
        void Reject(Action action, string label) {
            var rejected = false;
            try { action(); } catch (VersionControlReadException) { rejected = true; }
            check(rejected, label);
        }
        var fake = new FixtureRunner(fixture);
        var snapshot = await new CliVersionControlReader(fixture, "fake-cm", fake).ReadAsync();
        check(snapshot.state == "available" && snapshot.workspace.name == "fixture-workspace" && snapshot.workspace.repository == "Portfolio" && snapshot.workspace.branch == "/main", "reader maps workspace and selector identity from structured CLI output");
        check(snapshot.workspace.currentChangeset == 7 && snapshot.workspace.headChangeset == 10, "reader reports loaded and branch-head display changeset numbers");
        check(snapshot.pending.state == "available" && snapshot.pending.items.Length == 6 && snapshot.pending.items.Any(item => item.status == "CO+CH") &&
            snapshot.pending.items.Any(item => item.path.EndsWith(".meta")) && snapshot.pending.items.Any(item => item.oldPath == "Assets/Old.cs"), "reader preserves combined checkout/change, add/delete/private/move and meta-file states");
        check(snapshot.pending.items.Any(item => item.path.Contains("未提交")) && snapshot.pending.items.All(item => !item.path.Contains('\\')), "pending paths preserve Unicode while normalizing workspace separators");
        check(snapshot.incoming.state == "available" && snapshot.incoming.changesets.Select(item => item.number).SequenceEqual(new long[] { 10, 9, 8 }), "bounded ancestry comparison identifies only newer changesets on the selected branch");
        check(snapshot.history.state == "available" && snapshot.history.changesets.Length == 50 && snapshot.history.hasMore && snapshot.history.changesets[0].number == 60, "history uses 51-row lookahead and display CHANGESETID rather than internal ID");
        check(snapshot.history.changesets[0].dateUtc.EndsWith('Z') && snapshot.history.changesets[0].comment.Contains("café"), "changeset history preserves text and normalizes CLI timestamps to UTC");
        check(fake.Calls.All(call => call.WorkingDirectory == fixture && VersionControlCommandRunner.IsReadOnlyCommand(call.Arguments)) &&
            fake.Calls.All(call => new[] { "getworkspacefrompath", "status", "find", "log" }.Contains(call.Arguments[0])), "all reader commands use the captured project and a read-only allowlist");
        check(fake.Calls.Last().Arguments[0] == "getworkspacefrompath" && fake.HeaderCalls == 2, "reader revalidates workspace and selector after all remote reads");
        var branchFake = new FixtureRunner(fixture);
        var branch = await new CliVersionControlReader(fixture, "fake-cm", branchFake).ReadAsync(50, "branch");
        check(branch.history.scope == "branch" && branch.history.offset == 50 && branchFake.Calls.Any(call => call.Arguments[0] == "find" && call.Arguments[2].Contains("where branch = '/main'") && call.Arguments[2].Contains("limit 51 offset 50")), "branch history query has explicit branch scope, bounded page size and offset");
        var zero = new FixtureRunner(fixture) { Loaded = 0, Head = 1 };
        var zeroSnapshot = await new CliVersionControlReader(fixture, "fake-cm", zero).ReadAsync();
        check(zeroSnapshot.workspace.currentChangeset == 0 && zeroSnapshot.incoming.state == "available" && zeroSnapshot.incoming.changesets.Length == 1 && zeroSnapshot.incoming.changesets[0].number == 1, "reader proves ancestry when the loaded display revision is zero");
        var equal = new FixtureRunner(fixture) { Head = 7 };
        var equalSnapshot = await new CliVersionControlReader(fixture, "fake-cm", equal).ReadAsync();
        check(equalSnapshot.incoming.state == "available" && equalSnapshot.incoming.changesets.Length == 0 && equal.Calls.All(call => call.Arguments[0] != "log"), "equal loaded/head is a successful empty incoming comparison without remote log work");
        var manyIncoming = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { Loaded = 0, Head = 250 }).ReadAsync();
        check(manyIncoming.incoming.state == "available" && manyIncoming.incoming.changesets.Length == 200 && manyIncoming.incoming.truncated,
            "incoming ancestry output is capped at 200 displayed changesets with explicit truncation");
        foreach (var type in new[] { "Changeset", "Label" }) {
            var pinned = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { SelectorType = type }).ReadAsync();
            check(pinned.state == "available" && pinned.incoming.state == "unsupported" && pinned.history.state == "available", "pinned " + type + " selector exposes repository history without inventing incoming updates");
        }
        var gluon = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { WorkspaceType = "partial" }).ReadAsync();
        check(gluon.state == "available" && gluon.incoming.state == "unsupported" && gluon.history.state == "available", "Gluon/partial workspace marks incoming capability unsupported");
        var absent = new FixtureRunner(fixture);
        var missing = await new CliVersionControlReader(fixture, "", absent).ReadAsync();
        check(missing.state == "missingClient" && absent.Calls.Count == 0, "missing client returns explicit unavailable state without launching a command");
        var nonWorkspace = new FixtureRunner(fixture) { Override = arguments => arguments[0] == "getworkspacefrompath" ? new() { exitCode = 1, stderr = "not in a workspace SECRET-TOKEN" } : null };
        var notUvcs = await new CliVersionControlReader(fixture, "fake-cm", nonWorkspace).ReadAsync();
        check(notUvcs.state == "notUvcs" && !JsonSerializer.Serialize(notUvcs, new JsonSerializerOptions { IncludeFields = true }).Contains("SECRET-TOKEN"), "not-UVCS diagnostic is classified without echoing CLI text or credentials");
        var auth = new FixtureRunner(fixture) { Override = arguments => arguments[0] == "find" ? new() { exitCode = 1, stderr = "authentication token SECRET-TOKEN denied" } : null };
        var authSnapshot = await new CliVersionControlReader(fixture, "fake-cm", auth).ReadAsync();
        check(authSnapshot.state == "available" && authSnapshot.history.state == "unavailable" && authSnapshot.history.errorCode == "authenticationRequired" && authSnapshot.pending.state == "available", "authentication failure is a partial history error rather than false successful zero");
        check(!JsonSerializer.Serialize(authSnapshot, new JsonSerializerOptions { IncludeFields = true }).Contains("SECRET-TOKEN"), "partial CLI errors do not reflect arbitrary stderr in snapshots");
        var switched = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { SwitchBranchAtEnd = true }).ReadAsync();
        check(switched.state == "failed" && switched.errorCode == "workspaceChanged" && switched.pending.items.Length == 0, "branch change during capture invalidates the entire mixed snapshot");
        var replaced = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { SwitchWorkspaceAtEnd = true }).ReadAsync();
        check(replaced.state == "failed" && replaced.errorCode == "workspaceChanged", "workspace identity replacement during capture invalidates the response");
        var unknownAncestry = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { OmitLoadedAncestor = true }).ReadAsync();
        check(unknownAncestry.incoming.state == "unavailable" && unknownAncestry.incoming.errorCode == "ancestryUnknown", "unproven loaded ancestry never becomes an empty incoming success");
        var extraBranch = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { IncludeForeignBranch = true }).ReadAsync();
        check(extraBranch.incoming.state == "available" && extraBranch.incoming.changesets.All(item => item.branch == "/main"), "incoming display filters changesets from a different branch");
        var oversized = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { Override = arguments => arguments[0] == "getworkspacefrompath" ? new() { stdout = new string('x', CliVersionControlReader.MaxCliOutputCharacters + 1) } : null }).ReadAsync();
        check(oversized.state == "failed" && oversized.errorCode == "outputLimit", "oversized injected CLI output is rejected before parsing");
        var invalidRequestFake = new FixtureRunner(fixture);
        var invalidOffset = await new CliVersionControlReader(fixture, "fake-cm", invalidRequestFake).ReadAsync(100001);
        var invalidScope = await new CliVersionControlReader(fixture, "fake-cm", invalidRequestFake).ReadAsync(0, "all");
        check(invalidOffset.errorCode == "invalidRequest" && invalidScope.errorCode == "invalidRequest" && invalidRequestFake.Calls.Count == 0, "invalid history offset/scope never reaches even an injected command runner");
        using (var cancelled = new CancellationTokenSource(80)) {
            var cancellationObserved = false;
            try { await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { Delay = true }).ReadAsync(0, "repository", cancelled.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            check(cancellationObserved, "caller cancellation propagates without becoming a misleading failed workspace result");
        }
        var watch = Stopwatch.StartNew();
        var timed = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { Delay = true }).ReadAsync();
        watch.Stop();
        check(timed.errorCode == "timeout" && watch.Elapsed >= TimeSpan.FromSeconds(20) && watch.Elapsed < TimeSpan.FromSeconds(35), "reader applies its real 25-second aggregate deadline to injected command work");
        var pending = CliVersionControlReader.ParsePending(FixtureRunner.Pending());
        check(pending.items.Length == 6, "public pending parser covers complete status output");
        var hugePending = CliVersionControlReader.ParsePending("<StatusOutput>" + string.Concat(Enumerable.Range(0, 2001).Select(i => "<Change><Type>CH</Type><Path>Assets/File" + i + ".cs</Path><OldPath/></Change>")) + "</StatusOutput>");
        check(hugePending.items.Length == 2000 && hugePending.truncated, "pending parser enforces its 2000-entry cap and reports truncation");
        var displayNumber = CliVersionControlReader.ParseChangesets(FixtureRunner.Find(new long[] { 7 }), false).Single();
        check(displayNumber.number == 7, "find XML display changeset number is not the internal object ID");
        var logNumber = CliVersionControlReader.ParseChangesets(FixtureRunner.Log(new long[] { 1 }), true).Single();
        check(logNumber.number == 1, "log XML display ChangesetId is not the internal ObjId");
        var longCommentXml = FixtureRunner.Find(new long[] { 7 }).Replace("History café 日本語", new string('x', 5000));
        check(CliVersionControlReader.ParseChangesets(longCommentXml, false).Single().comment.Length == 4096, "CLI parser caps general changeset comments at 4096 characters");
        var clippedHistory = await new CliVersionControlReader(fixture, "fake-cm", new FixtureRunner(fixture) { Override = arguments => arguments[0] == "find" ? new() { stdout = longCommentXml } : null }).ReadAsync();
        check(clippedHistory.history.changesets.Single().comment.Length == 1024, "paged history has an explicit 1024-character preview cap");
        Reject(() => CliVersionControlReader.ParseChangesets("<!DOCTYPE PLASTICQUERY [<!ENTITY leak SYSTEM 'file:///never-read'>]><PLASTICQUERY>&leak;</PLASTICQUERY>", false), "XML DTD and external entity resolution are prohibited");
        Reject(() => CliVersionControlReader.ParsePending("<StatusOutput><Change><Type>CH</Type><Path>Assets/\u0001Bad.cs</Path></Change></StatusOutput>"), "invalid XML control characters are rejected");
        Reject(() => CliVersionControlReader.ParseChangesets(FixtureRunner.Find(new long[] { 7, 7 }), false), "duplicate display changeset identities are rejected");
        Reject(() => CliVersionControlReader.ParseChangesets(FixtureRunner.Find(new long[] { 7 }).Replace("<OWNER>alice</OWNER>", "<OWNER>alice&#x9;control</OWNER>"), false), "author metadata cannot contain decoded XML control characters");
        Reject(() => CliVersionControlReader.ParseChangesets(FixtureRunner.Find(new long[] { 7 }).Replace("<CHANGESETID>7</CHANGESETID>", "<CHANGESETID>-1</CHANGESETID>"), false), "negative display changeset numbers are rejected");
        foreach (var command in new[] { "update", "checkin", "checkout", "add", "remove", "delete", "merge", "switch", "undo", "uncheckout", "configure", "login" })
            check(!VersionControlCommandRunner.IsReadOnlyCommand(new[] { command, fixture }), "production runner rejects mutation command " + command);
        check(!VersionControlCommandRunner.IsReadOnlyCommand(new[] { "status", fixture, "--xml", "--encoding=utf-8", "--iscochanged", "--extra" }), "production allowlist rejects extra browser-shaped options");
        check(!VersionControlCommandRunner.IsTrustedExecutable(Path.Combine(fixture, "cm.exe"), fixture), "project-local executable cannot be trusted as the system UVCS client");
        check(VersionControlCommandRunner.QuoteArgument("a\"b\\").StartsWith('"') && VersionControlCommandRunner.QuoteArgument("a\"b\\").EndsWith('"'), "command arguments quote literal quotes and trailing backslashes without a shell");
        Reject(() => VersionControlCommandRunner.QuoteArgument("bad\ncheckin"), "CLI argument controls cannot create a second command");
    }
}

sealed class FixtureRunner(string root) : IVersionControlCommandRunner
{
    public long Loaded = 7, Head = 10;
    public string SelectorType = "Branch", WorkspaceType = "regular";
    public bool SwitchBranchAtEnd, SwitchWorkspaceAtEnd, OmitLoadedAncestor, IncludeForeignBranch, Delay;
    public int HeaderCalls, WorkspaceCalls;
    public Func<string[], VersionControlCommandResult?>? Override;
    public List<(string WorkingDirectory, string[] Arguments)> Calls { get; } = [];
    public async Task<VersionControlCommandResult> RunAsync(string executable, string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((workingDirectory, arguments.ToArray()));
        if (Delay) await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        var replacement = Override?.Invoke(arguments); if (replacement is not null) return replacement;
        string output;
        switch (arguments[0]) {
            case "getworkspacefrompath":
                WorkspaceCalls++;
                output = "fixture-workspace\t" + root + "\t" + (SwitchWorkspaceAtEnd && WorkspaceCalls > 1 ? "different-guid" : "fixture-guid") + "\t" + WorkspaceType + "\tstatic";
                break;
            case "status":
                if (!arguments.Contains("--header")) output = Pending();
                else if (arguments.Contains("--head")) output = Status(Head, SelectorType, "/main");
                else { HeaderCalls++; output = Status(Loaded, SelectorType, SwitchBranchAtEnd && HeaderCalls > 1 ? "/switched" : "/main"); }
                break;
            case "find": output = Find(Enumerable.Range(0, 51).Select(i => 60L - i)); break;
            case "log":
                var range = arguments.Any(argument => argument.StartsWith("--from="));
                var numbers = range ? Enumerable.Range(0, checked((int)(Head - Loaded))).Select(i => Head - i)
                    : Enumerable.Range(0, checked((int)(Head - Loaded + 1))).Select(i => Head - i).Concat(new long[] { 0 });
                if (!range && OmitLoadedAncestor) numbers = numbers.Where(number => number != Loaded);
                output = Log(numbers, range && IncludeForeignBranch);
                break;
            default: throw new Exception("Unexpected fixture command: " + arguments[0]);
        }
        return new() { stdout = output, exitCode = 0 };
    }
    static string Xml(string value) => System.Security.SecurityElement.Escape(value)!;
    static string Status(long changeset, string selectorType, string branch) => "<StatusOutput><WorkspaceStatus><Status><RepSpec><Server>fixture@cloud</Server><Name>Portfolio</Name></RepSpec><Changeset>" + changeset + "</Changeset></Status></WorkspaceStatus><WkConfigType>" + Xml(selectorType) + "</WkConfigType><WkConfigName>" + (selectorType == "Branch" ? Xml(branch + "@Portfolio@fixture@cloud") : "cs:" + changeset + "@Portfolio@fixture@cloud") + "</WkConfigName></StatusOutput>";
    public static string Pending() => "<StatusOutput><Changes>" +
        "<Change><Type>CO+CH</Type><Path>Assets\\Player.cs</Path><OldPath/></Change>" +
        "<Change><Type>PR</Type><Path>Assets\\未提交.cs</Path><OldPath/></Change>" +
        "<Change><Type>AD</Type><Path>Assets\\New.asset</Path><OldPath/></Change>" +
        "<Change><Type>DE</Type><Path>Assets\\Old.asset.meta</Path><OldPath/></Change>" +
        "<Change><Type>MV</Type><Path>Assets\\New.cs</Path><OldPath>Assets\\Old.cs</OldPath></Change>" +
        "<Change><Type>CH</Type><Path>Assets\\Scene.unity</Path><OldPath/></Change></Changes></StatusOutput>";
    public static string Find(IEnumerable<long> numbers) => "<PLASTICQUERY>" + string.Concat(numbers.Select(number => "<CHANGESET><ID>" + (100000 + number) + "</ID><CHANGESETID>" + number + "</CHANGESETID><COMMENT>History café 日本語</COMMENT><DATE>2026-10-07T13:00:00-07:00</DATE><OWNER>alice</OWNER><BRANCH>/main</BRANCH></CHANGESET>")) + "</PLASTICQUERY>";
    public static string Log(IEnumerable<long> numbers, bool foreign = false) => "<LogList>" + string.Concat(numbers.Distinct().Select(number => "<Changeset><ObjId>" + (70000 + number) + "</ObjId><ChangesetId>" + number + "</ChangesetId><Branch>" + (foreign && number == 8 ? "/other" : "/main") + "</Branch><Comment>Incoming " + number + "</Comment><Owner>alice</Owner><Date>2026-10-07T20:00:00Z</Date></Changeset>")) + "</LogList>";
}
