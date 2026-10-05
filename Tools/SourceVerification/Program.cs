using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlaytestOps.Editor;

var root = Path.GetFullPath(args[0]);
var appRoot = Path.GetFullPath(args[1]);
var preview = args.Contains("--preview");
var fixture = Path.Combine(root, "source-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var checks = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
void FileAt(string path, string content) {
    var target = Path.Combine(fixture, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllText(target, content, new UTF8Encoding(false));
}
void Reject(Action operation, string label) {
    try { operation(); throw new Exception("Unexpected acceptance: " + label); }
    catch (Exception ex) when (ex is not Exception || !ex.Message.StartsWith("Unexpected acceptance:")) { checks++; Console.WriteLine("PASS " + label); }
}
const string codeText = "\uFEFF" + """"
// Saved working-copy preview · café 日本語 😀
using System;
using System.Collections.Generic;
using UnityEngine;

#nullable enable
namespace PlaytestOps.Preview
{
    public interface IHealth { int Health { get; } }

    [Serializable]
    public class PlayerHealth : MonoBehaviour, IHealth
    {
        public int Health { get; private set; } = 100;
        private readonly List<int> damageHistory = new List<int>();
        public bool IsAlive => Health > 0;
        public string? Label = null;
        private const int Mask = 0x2A;
        private const double Tuning = 1.25e-3;
        private const decimal Price = 12.50m;
        private const char Marker = 'A';
        private const string Escaped = "quoted\"literal";
        private const string Verbatim = @"C:\Unity\Scripts";
        private const string Raw = """<tag>Raw C# string</tag>""";
        // WhitespaceMarker: regular space; TAB.
        /* Multiline <b>comment</b>
           Debug.Log("not code inside the comment");
        */
        // <script>alert('source')</script> & "quoted"
        // </code></pre><img src=x onerror="window.__sourceXss = true">
        public void ApplyDamage(int amount)
        {
            Health = Mathf.Max(0, Health - amount);
            damageHistory.Add(amount);
            Debug.Log($"Health: {Health}, mask: {Mask:X2}");
        }
    }
}
#nullable restore
"""" + "\n";
const string scriptPath = "Assets/Scripts/PlayerHealth.cs";
FileAt(scriptPath, codeText);
FileAt("Assets/Tests/HealthTests.cs", "// User-owned test fixture\npublic class HealthTests {}\n");
FileAt("Assets/Scripts/Unicode.cs", "// café 日本語 😀\n");
FileAt("Assets/Scripts/Notes.txt", "not C#");
FileAt("Assets/Samples/Imported.cs", "package sample");
FileAt("Assets/TutorialInfo/Readme.cs", "Unity template");
FileAt("Packages/com.vendor/Foo.cs", "package");
FileAt("Library/Foo.cs", "library");
foreach (var folder in new[] { "Plugins", "ThirdParty", "Third-Party", "Vendor", "External", "Samples", "TutorialInfo", "Packages", "Library", "PackageCache", "Temp", "obj", "Logs", ".git", ".svn", "Samples~", "Standard Assets" })
    FileAt("Assets/Scripts/" + folder + "/Excluded.cs", "excluded");

// The same pure reader is compiled into Unity and into this verifier.
var reader = new SourceReader(fixture, new[] { "Assets/Scripts", "Assets/Tests" });
var listed = reader.List(CancellationToken.None);
Check(listed.files.Length == 3, "reader lists only .cs files from declared author folders");
Check(listed.files.Select(f => f.path).SequenceEqual(listed.files.Select(f => f.path).OrderBy(p => p, StringComparer.Ordinal)), "reader returns stable ordinal path ordering");
var read = reader.Read(scriptPath, CancellationToken.None);
Check(read.content == codeText && read.byteLength == Encoding.UTF8.GetByteCount(codeText), "reader returns exact saved UTF8 source and byte length");
Check(read.sha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(codeText))).ToLowerInvariant(), "reader hashes exact content bytes");
Check(DateTimeOffset.TryParse(read.lastModifiedUtc, out var modified) && modified.Offset == TimeSpan.Zero, "reader supplies UTC modification time");
foreach (var path in new[] { "../secret.cs", "Assets/Scripts/../Secret.cs", "Assets\\Scripts\\PlayerHealth.cs", "/Assets/Scripts/PlayerHealth.cs", "C:/Secret.cs", "Assets/Scripts/Notes.txt", "Packages/com.vendor/Foo.cs", "Assets/Samples/Imported.cs", "Assets/TutorialInfo/Readme.cs", "Assets/Scripts/Plugins/Excluded.cs", "Assets/Scripts/Bad\n.cs" })
    Reject(() => reader.Read(path, CancellationToken.None), "reader rejects disallowed path " + path.Replace('\n', ' '));
foreach (var roots in new[] { new[] { "Assets" }, new[] { "Packages" }, new[] { "Assets/../Library" }, new[] { "Assets/Samples" }, new[] { "Assets\\Scripts" } })
    Reject(() => new SourceReader(fixture, roots).List(CancellationToken.None), "reader rejects unsafe or broad root " + roots[0]);
Check(new SourceReader(fixture, Array.Empty<string>()).List(CancellationToken.None).files.Length == 0, "explicit empty author-folder configuration disables browsing");
Check(new SourceReader(fixture, new[] { "Assets/NotCreatedYet" }).List(CancellationToken.None).files.Length == 0, "missing author folder is a valid empty result");
FileAt("Assets/Scripts/Huge.cs", new string('x', 128 * 1024 + 1));
Reject(() => reader.Read("Assets/Scripts/Huge.cs", CancellationToken.None), "reader refuses source larger than 128KiB");
FileAt("Assets/Scripts/Binary.cs", "hello\0world");
Reject(() => reader.Read("Assets/Scripts/Binary.cs", CancellationToken.None), "reader refuses binary/NUL content");
var invalidUtf8 = Path.Combine(fixture, "Assets/Scripts/InvalidUtf8.cs"); File.WriteAllBytes(invalidUtf8, [0xff, 0xfe, 0xff]);
Reject(() => reader.Read("Assets/Scripts/InvalidUtf8.cs", CancellationToken.None), "reader refuses non-UTF8 input");
var cancelled = new CancellationToken(true);
Reject(() => reader.List(cancelled), "reader honors cancellation");
var outside = Path.Combine(fixture, "Outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "Secret.cs"), "outside source");
try {
    var link = Path.Combine(fixture, "Assets/Scripts/Linked");
    try { Directory.CreateSymbolicLink(link, outside); }
    catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException) {
        // Windows junctions exercise the same ReparsePoint guard without symlink privilege.
        var junction = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        junction.ArgumentList.Add("-NoProfile"); junction.ArgumentList.Add("-Command");
        junction.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' | Out-Null");
        using var creation = Process.Start(junction)!; creation.WaitForExit();
        if (creation.ExitCode != 0) throw new UnauthorizedAccessException("Could not create fixture junction.");
    }
    Reject(() => reader.Read("Assets/Scripts/Linked/Secret.cs", CancellationToken.None), "reader refuses a symlinked directory escape");
    Check(!reader.List(CancellationToken.None).files.Any(f => f.path.Contains("Linked/")), "listing skips symlink directories");
} catch (UnauthorizedAccessException) { Console.WriteLine("NOT RUN symlink creation requires platform privilege"); }
for (var i = 0; i < 1002; i++) FileAt($"Assets/Many/File{i:D4}.cs", "// fixture\n");
var many = new SourceReader(fixture, new[] { "Assets/Many" }).List(CancellationToken.None);
Check(many.files.Length == 1000 && many.truncated, "reader bounds large file catalogs and reports truncation");
const string unicodeContent = "\uFEFF// UnicodeMarker café 日本語 😀\npublic class UnicodeFixture {}\n";
var unicodeBytes = Encoding.UTF8.GetBytes(unicodeContent);
var unicodeHash = Convert.ToHexString(SHA256.HashData(unicodeBytes)).ToLowerInvariant();
FileAt("Assets/Tests/Utf8Bom.cs", unicodeContent);
var unicodeRead = reader.Read("Assets/Tests/Utf8Bom.cs", CancellationToken.None);
Check(unicodeRead.content == unicodeContent && unicodeRead.content[0] == '\uFEFF', "reader preserves UTF8 BOM and multibyte Unicode text exactly");
Check(unicodeRead.byteLength == unicodeBytes.Length && unicodeRead.sha256 == unicodeHash, "reader BOM and Unicode hash match original saved bytes");

var address = "http://127.0.0.1:5292";
Process? server = null;
var serverLog = new List<string>();
ClientWebSocket? socket = null;
using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
async Task<JsonElement> Receive(ClientWebSocket target, bool keepOpen = false) {
    using var timeout = new CancellationTokenSource(keepOpen ? 600000 : 15000); using var stream = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult result;
    do { result = await target.ReceiveAsync(buffer, timeout.Token); if (result.MessageType == WebSocketMessageType.Close) throw new Exception("Socket closed"); stream.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
    return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
}
Task Write(ClientWebSocket target, object payload) => target.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(payload)), WebSocketMessageType.Text, true, CancellationToken.None);
var projectId = Guid.NewGuid().ToString("N");
var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(codeText))).ToLowerInvariant();
object ListReply(string requestId, object? files = null, string[]? roots = null, string error = "", bool truncated = false) => new {
    type = "source.list.result", requestId, roots = roots ?? new[] { "Assets/Scripts", "Assets/Tests" },
    files = files ?? new[] { new { path = scriptPath, byteLength = (long)Encoding.UTF8.GetByteCount(codeText) }, new { path = "Assets/Tests/HealthTests.cs", byteLength = 57L } }, error, truncated };
object ReadReply(string requestId, string path = scriptPath, string? content = null, string? sha256 = null, long? byteLength = null, string error = "", string time = "2026-10-05T20:00:00.0000000Z") => new {
    type = "source.read.result", requestId, path, byteLength = byteLength ?? Encoding.UTF8.GetByteCount(content ?? codeText), content = content ?? codeText,
    sha256 = sha256 ?? hash, lastModifiedUtc = time, error };
async Task<string> ListPage(object? files = null, string[]? roots = null, string error = "", bool truncated = false, string? url = null) {
    var page = client.GetStringAsync(url ?? "/Scripts?projectId=" + projectId);
    var request = await Receive(socket!); Check(request.GetProperty("type").GetString() == "source.list", "source page requests metadata on demand");
    await Write(socket!, ListReply(request.GetProperty("requestId").GetString()!, files, roots, error, truncated)); return await page;
}
async Task<string> ReadPage(Func<string, object>? response = null, string? path = null) {
    var page = client.GetStringAsync("/Scripts?projectId=" + projectId + "&path=" + Uri.EscapeDataString(path ?? scriptPath));
    var listRequest = await Receive(socket!); await Write(socket!, ListReply(listRequest.GetProperty("requestId").GetString()!));
    var request = await Receive(socket!); Check(request.GetProperty("type").GetString() == "source.read", "selected file requests bounded source on demand");
    var requestId = request.GetProperty("requestId").GetString()!; await Write(socket!, response?.Invoke(requestId) ?? ReadReply(requestId)); return await page;
}
try {
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = appRoot, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Path.Combine(root, "build/PlaytestOps.Web.dll")); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(address);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["ConnectionStrings__DefaultConnection"] = "Data Source=" + Path.Combine(fixture, "source-check.db");
    server = Process.Start(start)!;
    server.OutputDataReceived += (_, e) => { if (e.Data != null) lock (serverLog) serverLog.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (serverLog) serverLog.Add(e.Data); }; server.BeginOutputReadLine(); server.BeginErrorReadLine();
    var ready = false;
    for (var i = 0; i < 100; i++) { if (server.HasExited) throw new Exception("Server exited"); try { if ((await client.GetAsync("/")).IsSuccessStatusCode) { ready = true; break; } } catch (HttpRequestException) {} await Task.Delay(100); }
    Check(ready, "isolated source-check server starts");
    var emptyPage = await client.GetStringAsync("/Scripts"); Check(emptyPage.Contains("No Unity Editor connected"), "source page has disconnected empty state");
    using (var badHost = new HttpRequestMessage(HttpMethod.Get, "/Scripts")) { badHost.Headers.Host = "attacker.example"; Check((await client.SendAsync(badHost)).StatusCode == HttpStatusCode.Forbidden, "source browsing rejects non-local Host before disclosure"); }
    var editorPage = await client.GetStringAsync("/Editors"); var anti = Regex.Match(editorPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
    var generated = await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent(new Dictionary<string,string> { ["__RequestVerificationToken"] = anti }));
    var code = Regex.Match(await generated.Content.ReadAsStringAsync(), "id=\"pairing-code\">([^<]+)").Groups[1].Value;
    var paired = await client.PostAsJsonAsync("/api/editor/pair", new { code, projectId, projectName = "Source preview fixture", unityVersion = "6000.6.0f1" });
    var token = JsonDocument.Parse(await paired.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    Check((await client.GetStringAsync("/Scripts")).Contains("No Unity Editor connected"), "paired-only session exposes no source");
    socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + token); await socket.ConnectAsync(new Uri("ws://127.0.0.1:5292/api/editor/connect"), CancellationToken.None); await Receive(socket);
    var html = await ListPage(); Check(html.Contains("PlayerHealth.cs") && html.Contains("HealthTests.cs") && !html.Contains("bytes@if"), "connected source page renders approved paths without raw Razor labels");
    Check(!html.Contains("hx-trigger=\"every") && !html.Contains("contenteditable"), "source page is read-only with no idle polling");
    html = await ReadPage(); Check(html.Contains("public class PlayerHealth") && html.Contains(hash), "viewer renders source content with SHA256");
    Check(html.Contains("&lt;script&gt;") && !html.Contains("<script>alert('source')</script>"), "source content is HTML encoded");
    Check(html.Contains("2026-10-05") && html.Contains("Saved working"), "viewer labels working-copy content and modification metadata");
    html = await ListPage(url: "/Scripts?projectId=" + projectId + "&q=healthtests"); Check(html.Contains("HealthTests.cs") && !html.Contains(">Assets/Scripts/PlayerHealth.cs<"), "case-insensitive path search filters script list");
    html = await ListPage(files: Array.Empty<object>()); Check(html.Contains("No eligible user scripts"), "successful empty snapshot differs from disconnected state");
    html = await ListPage(truncated: true); Check(html.Contains("limit") || html.Contains("truncated"), "catalog truncation is visible");
    html = await ListPage(error: "SOURCE FIXTURE unavailable"); Check(html.Contains("could not list") && !html.Contains("PlayerHealth.cs") && !html.Contains("SOURCE FIXTURE"), "Editor error does not leak stale source paths or arbitrary error text");
    foreach (var invalid in new[] { "Packages/Vendor.cs", "Assets/Scripts/../Secret.cs", "Assets/Scripts/Plugins/Vendor.cs" }) {
        html = await ListPage(files: new[] { new { path = invalid, byteLength = 1L } }); Check(!html.Contains("source-paths") || !html.Contains("href=\"/Scripts?projectId="), "invalid source catalog rejected: " + invalid);
    }
    html = await ReadPage(id => ReadReply(id, sha256: new string('0', 64))); Check(!html.Contains("public class PlayerHealth"), "backend rejects mismatched source hash");
    html = await ReadPage(id => ReadReply(id, path: "Assets/Tests/HealthTests.cs")); Check(!html.Contains("public class PlayerHealth"), "backend rejects response for a different requested path");
    html = await ReadPage(id => ReadReply(id, byteLength: 1)); Check(!html.Contains("public class PlayerHealth"), "backend rejects incorrect byte length");
    html = await ReadPage(id => ReadReply(id, time: "2026-10-05T20:00:00+01:00")); Check(!html.Contains("public class PlayerHealth"), "backend rejects non-UTC source time");
    html = await ReadPage(id => ReadReply(id, error: "The source file is no longer available.")); Check(!html.Contains("public class PlayerHealth") && html.Contains("could not read"), "missing file error is shown without old content");
    foreach (var roots in new[] { new[] { "Assets/Scripts", "Assets/Scripts" }, new[] { "Assets/Scripts", "Assets/scripts" }, new[] { "Assets" } }) {
        html = await ListPage(roots: roots);
        Check(html.Contains("invalid script response") && !html.Contains("PlayerHealth.cs"), "backend rejects duplicate or broad source roots: " + string.Join(", ", roots));
    }
    html = await ListPage(files: new[] { new { path = scriptPath, byteLength = 10L }, new { path = scriptPath, byteLength = 20L } });
    Check(html.Contains("invalid script response") && !html.Contains("PlayerHealth.cs"), "backend rejects duplicate script descriptors");
    foreach (var unsafePath in new[] { "Assets/Scripts /Bad.cs", "Assets/Scripts./Bad.cs", "Assets/Scripts/Bad?.cs", "Assets/Scripts/Bad|.cs" }) {
        html = await ListPage(files: new[] { new { path = unsafePath, byteLength = 1L } });
        Check(html.Contains("invalid script response"), "backend rejects filesystem-ambiguous script path: " + unsafePath);
    }
    const string nulContent = "// NulMarker\0\npublic class BinaryFixture {}\n";
    var nulHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nulContent))).ToLowerInvariant();
    html = await ReadPage(id => ReadReply(id, content: nulContent, sha256: nulHash));
    Check(html.Contains("invalid script response") && !html.Contains("NulMarker"), "backend rejects NUL source even when byte length and hash are valid");
    html = await ReadPage(id => ReadReply(id, content: unicodeContent, sha256: unicodeHash));
    Check(html.Contains("UnicodeMarker") && html.Contains(unicodeHash) && !html.Contains("invalid script response"), "backend accepts exact UTF8 BOM and multibyte source content");
    var traversalPage = await ListPage(url: "/Scripts?projectId=" + projectId + "&path=" + Uri.EscapeDataString("Assets/Scripts/../Secret.cs"));
    Check(!traversalPage.Contains("public class PlayerHealth"), "invalid URL source selection never discloses content");
    var waitingPage = client.GetStringAsync("/Scripts?projectId=" + projectId); var waitingRequest = await Receive(socket);
    await Write(socket, ListReply(Guid.NewGuid().ToString("N"))); await Task.Delay(100);
    Check(!waitingPage.IsCompleted, "unknown request ID cannot satisfy pending source request");
    await Write(socket, ListReply(waitingRequest.GetProperty("requestId").GetString()!)); Check((await waitingPage).Contains("PlayerHealth.cs"), "valid correlated response completes pending request");
    // A second authenticated project must not answer a request owned by this Editor.
    {
        var otherProjectId = Guid.NewGuid().ToString("N");
        var otherGenerated = await client.PostAsync("/Editors?handler=PairingCode", new FormUrlEncodedContent(new Dictionary<string,string> { ["__RequestVerificationToken"] = anti }));
        var otherCode = Regex.Match(await otherGenerated.Content.ReadAsStringAsync(), "id=\"pairing-code\">([^<]+)").Groups[1].Value;
        var otherPair = await client.PostAsJsonAsync("/api/editor/pair", new { code = otherCode, projectId = otherProjectId, projectName = "Foreign source fixture", unityVersion = "6000.6.0f1" });
        var otherToken = JsonDocument.Parse(await otherPair.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        using var foreignSocket = new ClientWebSocket(); foreignSocket.Options.SetRequestHeader("Authorization", "Bearer " + otherToken);
        await foreignSocket.ConnectAsync(new Uri("ws://127.0.0.1:5292/api/editor/connect"), CancellationToken.None); await Receive(foreignSocket);
        var foreignAttemptPage = client.GetStringAsync("/Scripts?projectId=" + projectId);
        var ownedRequest = await Receive(socket); var ownedId = ownedRequest.GetProperty("requestId").GetString()!;
        await Write(foreignSocket, ListReply(ownedId)); await Task.Delay(150);
        Check(!foreignAttemptPage.IsCompleted, "foreign authenticated session cannot satisfy a known pending request ID");
        await Write(socket, ListReply(ownedId));
        Check((await foreignAttemptPage).Contains("PlayerHealth.cs"), "owner response still completes after foreign-session attempt");
        foreignSocket.Abort();
    }
    var parallelOwnerPage = client.GetStringAsync("/Scripts?projectId=" + projectId);
    var parallelRequest = await Receive(socket);
    var parallelRejected = await client.GetStringAsync("/Scripts?projectId=" + projectId);
    Check(parallelRejected.Contains("already active") && !parallelRejected.Contains("PlayerHealth.cs"), "broker rejects concurrent source requests for the same project");
    await Write(socket, ListReply(parallelRequest.GetProperty("requestId").GetString()!));
    Check((await parallelOwnerPage).Contains("PlayerHealth.cs"), "original project request survives a rejected parallel read");
    var wrongKindPage = client.GetStringAsync("/Scripts?projectId=" + projectId);
    var wrongKindRequest = await Receive(socket);
    await Write(socket, ReadReply(wrongKindRequest.GetProperty("requestId").GetString()!));
    html = await wrongKindPage;
    Check(html.Contains("invalid script response") && !html.Contains("PlayerHealth.cs"), "matching request ID with the wrong reply kind is rejected");
    var timeoutPage = client.GetStringAsync("/Scripts?projectId=" + projectId);
    var timeoutRequest = await Receive(socket); var expiredId = timeoutRequest.GetProperty("requestId").GetString()!;
    html = await timeoutPage;
    Check(html.Contains("within 10 seconds") && !html.Contains("PlayerHealth.cs"), "unanswered source request reaches a bounded readable timeout");
    await Write(socket, ListReply(expiredId));
    html = await ListPage();
    Check(html.Contains("PlayerHealth.cs"), "timeout removes pending ownership and ignores late replies before a fresh request");
    // Replacing a socket under the same session must cancel the old request, not move it.
    var replacedSocket = socket;
    var replacedPage = client.GetStringAsync("/Scripts?projectId=" + projectId);
    var replacedRequest = await Receive(replacedSocket);
    socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
    await socket.ConnectAsync(new Uri("ws://127.0.0.1:5292/api/editor/connect"), CancellationToken.None); await Receive(socket);
    await Write(socket, ListReply(replacedRequest.GetProperty("requestId").GetString()!));
    html = await replacedPage.WaitAsync(TimeSpan.FromSeconds(3));
    Check(!html.Contains("PlayerHealth.cs") && (html.Contains("disconnected") || html.Contains("changed")), "socket replacement promptly cancels old source request without accepting a reply on the new socket");
    replacedSocket.Abort(); replacedSocket.Dispose();
    html = await ListPage();
    Check(html.Contains("PlayerHealth.cs"), "replacement socket can complete a new separately correlated request");
    var stalePage = client.GetStringAsync("/Scripts?projectId=" + projectId); await Receive(socket); socket.Abort(); socket.Dispose();
    html = await stalePage; Check(!html.Contains("PlayerHealth.cs"), "disconnect during source request clears response");
    Check(!(await client.GetStringAsync("/Scripts?projectId=" + projectId)).Contains("PlayerHealth.cs"), "disconnected project never serves cached source");
    socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + token); await socket.ConnectAsync(new Uri("ws://127.0.0.1:5292/api/editor/connect"), CancellationToken.None); await Receive(socket);
    Console.WriteLine($"SOURCE VERIFICATION COMPLETE: {checks} checks passed.");
    if (preview) {
        Console.WriteLine("PREVIEW READY: " + address + "/Scripts?projectId=" + projectId);
        while (true) { var request = await Receive(socket, keepOpen: true); var requestId = request.GetProperty("requestId").GetString()!; var type = request.GetProperty("type").GetString();
            await Write(socket, type == "source.list" ? ListReply(requestId) : ReadReply(requestId, request.GetProperty("path").GetString()!)); }
    }
} catch { lock (serverLog) foreach (var line in serverLog.TakeLast(12)) Console.Error.WriteLine(line); throw; }
finally { socket?.Abort(); socket?.Dispose(); if (server is { HasExited: false }) { server.Kill(true); server.WaitForExit(); } server?.Dispose(); }
