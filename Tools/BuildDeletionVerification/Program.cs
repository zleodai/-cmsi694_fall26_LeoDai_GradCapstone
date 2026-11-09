using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using PlaytestOps.Web.Bridge;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;
using PlaytestOps.Web.Streaming;

if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--preview"))
{
    Console.Error.WriteLine("Usage: Checks <isolated-output-directory-containing-build> <absolute-web-project-path> [--preview]");
    Environment.ExitCode = 2;
    return;
}

var output = Path.GetFullPath(args[0]);
var appRoot = Path.GetFullPath(args[1]);
var previewMode = args.Length == 3;
var appDll = Path.Combine(output, "build", "PlaytestOps.Web.dll");
var scratch = Path.Combine(Path.GetTempPath(), "PlaytestOps-build-deletion-" + Guid.NewGuid().ToString("N"));
var serviceDatabase = Path.Combine(scratch, "service-check.db");
var httpDatabase = Path.Combine(scratch, "http-check.db");
var serverLogs = new List<string>();
var checks = 0;
Process? server = null;

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

ApplicationDbContext OpenDatabase(string database) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
    .UseSqlite("Data Source=" + database).Options);

async Task<(bool Build, int Logs)> Counts(string database, string id)
{
    await using var db = OpenDatabase(database);
    return (await db.UnityBuilds.AnyAsync(build => build.Id == id),
        await db.UnityBuildLogs.CountAsync(log => log.BuildId == id));
}

async Task<UnityBuild> Seed(string database, string state = "Succeeded", bool files = true, int logs = 2)
{
    var id = Guid.NewGuid().ToString("N");
    var build = new UnityBuild
    {
        Id = id, ProjectId = Guid.NewGuid().ToString("N"), ProjectName = "Disposable deletion fixture",
        RequestKey = Guid.NewGuid().ToString("N"), SessionId = Guid.NewGuid().ToString("N"),
        Revision = "disposable-revision", State = state, Phase = "Fixture", Message = "Synthetic record; never built or launched",
        ActiveTarget = "StandaloneWindows64", UnityVersion = "6000.6.0f1",
        RequestedAt = DateTime.UtcNow, LastUpdateAt = DateTime.UtcNow, FinishedAt = DateTime.UtcNow,
        LastLogSequence = logs, TotalLogCharacters = logs * 20
    };
    build.OutputPath = Path.Combine(scratch, "projects", id, "Builds", "PlaytestOps", id);
    build.ExecutablePath = Path.Combine(build.OutputPath, "Player.exe");
    build.ManifestPath = Path.Combine(build.OutputPath, "build-manifest.json");
    if (files)
    {
        Directory.CreateDirectory(Path.Combine(build.OutputPath, "Player_Data", "nested"));
        await File.WriteAllTextAsync(Path.Combine(build.OutputPath, ".playtestops-build"), "");
        await File.WriteAllBytesAsync(build.ExecutablePath, [1, 2, 3, 4]); // Data only; never a native executable.
        await File.WriteAllTextAsync(Path.Combine(build.OutputPath, "Player_Data", "nested", "fixture.data"), "owned fixture data");
        await WriteManifest(build);
    }
    await using var db = OpenDatabase(database);
    db.UnityBuilds.Add(build);
    for (var sequence = 1; sequence <= logs; sequence++) db.UnityBuildLogs.Add(new UnityBuildLog
    {
        BuildId = id, Sequence = sequence, TimestampUtc = "2026-10-16T12:00:00.000Z",
        Level = sequence == 1 ? "Debug" : "Warning", Message = "Disposable captured log " + sequence,
        StackTrace = "Fixture.Stack()"
    });
    await db.SaveChangesAsync();
    return build;
}

Task WriteManifest(UnityBuild build, int protocolVersion = 1, string? id = null, string? revision = null, string? target = null) =>
    File.WriteAllTextAsync(build.ManifestPath, JsonSerializer.Serialize(new
    {
        protocolVersion, buildId = id ?? build.Id, revision = revision ?? build.Revision,
        activeTarget = target ?? build.ActiveTarget
    }));

async Task UpdateRecord(string database, UnityBuild build)
{
    await using var db = OpenDatabase(database);
    db.UnityBuilds.Update(build);
    await db.SaveChangesAsync();
}

void RequireOwnedFixturePath(string candidate)
{
    var root = Path.GetFullPath(scratch).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(candidate).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Fixture filesystem operation escaped its private directory.");
}

void CreateDirectoryLink(string link, string target)
{
    RequireOwnedFixturePath(link);
    RequireOwnedFixturePath(target);
    Directory.CreateDirectory(Path.GetDirectoryName(link)!);
    try { Directory.CreateSymbolicLink(link, target); }
    catch (Exception error) when (OperatingSystem.IsWindows() && error is UnauthorizedAccessException or IOException)
    {
        // Junctions exercise the ReparsePoint guard without requiring symlink privilege.
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") +
            "' -Target '" + target.Replace("'", "''") + "' | Out-Null");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create a disposable junction.");
        if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new InvalidOperationException("Disposable junction creation failed.");
    }
    Check((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "fixture link is an actual reparse point");
}

async Task ServiceChecks()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite("Data Source=" + serviceDatabase));
    services.AddSingleton<BridgeRegistry>();
    services.AddSingleton<IWebHostEnvironment>(new FixtureEnvironment(scratch));
    services.AddSingleton<StreamingArtifactValidator>();
    services.AddSingleton<StreamingSupervisor>();
    services.AddSingleton<BuildDeletionService>();
    await using var provider = services.BuildServiceProvider();
    await using (var scope = provider.CreateAsyncScope())
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureCreatedAsync();
    var deletion = provider.GetRequiredService<BuildDeletionService>();

    var localContext = new DefaultHttpContext();
    localContext.Connection.RemoteIpAddress = IPAddress.Loopback;
    localContext.Request.Scheme = "http";
    localContext.Request.Host = new HostString("127.0.0.1", 54321);
    Check(BuildDeletionService.IsLocalDeleteRequest(localContext), "ordinary local form may omit Origin");
    localContext.Request.Headers.Origin = "http://127.0.0.1:54321";
    Check(BuildDeletionService.IsLocalDeleteRequest(localContext), "exact same-origin local deletion is accepted");
    foreach (var origin in new[] { "http://127.0.0.1:54322", "https://127.0.0.1:54321", "http://127.0.0.1:54321/path", "null" })
    {
        localContext.Request.Headers.Origin = origin;
        Check(!BuildDeletionService.IsLocalDeleteRequest(localContext), "wrong scheme, authority, path or opaque Origin is rejected");
    }
    localContext.Request.Headers.Origin = new[] { "http://127.0.0.1:54321", "http://127.0.0.1:54321" };
    Check(!BuildDeletionService.IsLocalDeleteRequest(localContext), "multiple Origin headers are rejected");
    localContext.Request.Headers.Remove("Origin");
    localContext.Request.Headers["Sec-Fetch-Site"] = "cross-site";
    Check(!BuildDeletionService.IsLocalDeleteRequest(localContext), "cross-site Fetch Metadata is rejected");
    localContext.Request.Headers.Remove("Sec-Fetch-Site");
    localContext.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.123");
    Check(!BuildDeletionService.IsLocalDeleteRequest(localContext), "non-loopback client cannot delete even with a localhost Host");
    localContext.Connection.RemoteIpAddress = IPAddress.Loopback;
    localContext.Request.Host = new HostString("attacker.example");
    Check(!BuildDeletionService.IsLocalDeleteRequest(localContext), "loopback connection cannot use an unapproved Host");

    foreach (var state in new[] { "Succeeded", "Failed", "Rejected", "Interrupted" })
        Check(BuildDeletionService.IsTerminal(state), state + " is terminal for metadata deletion");
    foreach (var state in new[] { "Pending", "Running", "Unknown", "", "Invented" })
        Check(!BuildDeletionService.IsTerminal(state), state + " is not a terminal deletion state");
    Check(await deletion.PreviewAsync(Guid.NewGuid().ToString("N"), CancellationToken.None) is null,
        "preview of an unknown build is absent");
    Check(!(await deletion.DeleteAsync(Guid.NewGuid().ToString("N"), false, CancellationToken.None)).Deleted,
        "unknown build cannot delete any history");

    var retained = await Seed(serviceDatabase);
    var metadata = await Seed(serviceDatabase);
    var preview = await deletion.PreviewAsync(metadata.Id, CancellationToken.None);
    Check(preview is { BlockedReason: null, CanDeleteFiles: true } && preview.LogCount == 2 && preview.Build.Id == metadata.Id,
        "preview reports terminal record, exact log count and validated optional files");
    Check((await Counts(serviceDatabase, metadata.Id)) == (true, 2) && File.Exists(metadata.ExecutablePath),
        "preview does not delete or change the record, logs or files");
    var metadataResult = await deletion.DeleteAsync(metadata.Id, false, CancellationToken.None);
    Check(metadataResult.Deleted, "metadata-only deletion succeeds");
    Check((await Counts(serviceDatabase, metadata.Id)) == (false, 0), "metadata deletion cascades captured logs");
    Check(File.Exists(metadata.ExecutablePath) && Directory.Exists(metadata.OutputPath), "metadata-only deletion preserves all output files");
    Check((await Counts(serviceDatabase, retained.Id)) == (true, 2) && File.Exists(retained.ExecutablePath),
        "another build's record, logs and output remain untouched");
    Check(!(await deletion.DeleteAsync(metadata.Id, false, CancellationToken.None)).Deleted,
        "replayed metadata deletion cannot act on an already removed record");
    foreach (var state in new[] { "Failed", "Rejected" })
    {
        var terminal = await Seed(serviceDatabase, state);
        Check((await deletion.DeleteAsync(terminal.Id, false, CancellationToken.None)).Deleted &&
            (await Counts(serviceDatabase, terminal.Id)) == (false, 0) && File.Exists(terminal.ExecutablePath),
            state + " metadata and logs can be removed while preserving output");
    }

    if (OperatingSystem.IsWindows())
    {
        var complete = await Seed(serviceDatabase);
        var parent = Path.GetDirectoryName(complete.OutputPath)!;
        var sibling = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(sibling, "keep.data"), "neighbor");
        await File.WriteAllTextAsync(Path.Combine(parent, "parent-keep.data"), "parent");
        Check((await deletion.DeleteAsync(complete.Id, true, CancellationToken.None)).Deleted,
            "confirmed optional file deletion removes a terminal owned build");
        Check(!Directory.Exists(complete.OutputPath) && (await Counts(serviceDatabase, complete.Id)) == (false, 0),
            "file deletion removes its exact output directory, record and captured logs");
        Check(File.Exists(Path.Combine(sibling, "keep.data")) && File.Exists(Path.Combine(parent, "parent-keep.data")),
            "parent and sibling files survive recursive output deletion");

        var gone = await Seed(serviceDatabase, files: false);
        Check((await deletion.DeleteAsync(gone.Id, true, CancellationToken.None)).Deleted,
            "already absent exact output folder allows history removal without filesystem mutation");
        var noManifest = await Seed(serviceDatabase);
        File.Delete(noManifest.ManifestPath);
        Check((await deletion.DeleteAsync(noManifest.Id, true, CancellationToken.None)).Deleted,
            "ordinary ownership marker permits incomplete output with no manifest");
    }

    foreach (var state in new[] { "Pending", "Running", "Unknown" })
    {
        var active = await Seed(serviceDatabase, state);
        var activePreview = await deletion.PreviewAsync(active.Id, CancellationToken.None);
        Check(!string.IsNullOrWhiteSpace(activePreview?.BlockedReason), state + " preview explains its deletion block");
        Check(!(await deletion.DeleteAsync(active.Id, false, CancellationToken.None)).Deleted &&
            !(await deletion.DeleteAsync(active.Id, true, CancellationToken.None)).Deleted,
            state + " blocks both metadata and optional file deletion");
        Check((await Counts(serviceDatabase, active.Id)) == (true, 2) && File.Exists(active.ExecutablePath),
            state + " rejection preserves its record, logs and files");
    }
    var interrupted = await Seed(serviceDatabase, "Interrupted");
    var interruptedPreview = await deletion.PreviewAsync(interrupted.Id, CancellationToken.None);
    Check(interruptedPreview is { BlockedReason: null, CanDeleteFiles: false },
        "Interrupted history may be removed but cannot authorize output deletion");
    Check(!(await deletion.DeleteAsync(interrupted.Id, true, CancellationToken.None)).Deleted,
        "Interrupted optional file deletion is refused");
    Check((await deletion.DeleteAsync(interrupted.Id, false, CancellationToken.None)).Deleted && File.Exists(interrupted.ExecutablePath),
        "Interrupted metadata-only removal leaves potentially unknown native output alone");

    async Task RejectFiles(UnityBuild build, string label, string? protectedFile = null, bool previewDetectsFailure = true)
    {
        var rejected = await deletion.PreviewAsync(build.Id, CancellationToken.None);
        if (previewDetectsFailure)
            Check(rejected is { CanDeleteFiles: false } && !string.IsNullOrWhiteSpace(rejected.FileError), label + " preview explains unsafe files");
        else
            Check(rejected is { BlockedReason: null, CanDeleteFiles: true }, label + " reaches the final recursive/exclusive-file preflight");
        var result = await deletion.DeleteAsync(build.Id, true, CancellationToken.None);
        Check(!result.Deleted && !string.IsNullOrWhiteSpace(result.Error), label + " rejects optional file deletion");
        Check((await Counts(serviceDatabase, build.Id)) == (true, 2), label + " rejection retains history and logs");
        if (protectedFile is not null) Check(File.Exists(protectedFile), label + " leaves the protected fixture file untouched");
    }

    var missingMarker = await Seed(serviceDatabase);
    File.Delete(Path.Combine(missingMarker.OutputPath, ".playtestops-build"));
    await RejectFiles(missingMarker, "missing ownership marker", missingMarker.ExecutablePath);
    Check((await deletion.DeleteAsync(missingMarker.Id, false, CancellationToken.None)).Deleted && File.Exists(missingMarker.ExecutablePath),
        "unsafe or legacy files do not block explicit metadata-only cleanup");

    var nonemptyMarker = await Seed(serviceDatabase);
    await File.WriteAllTextAsync(Path.Combine(nonemptyMarker.OutputPath, ".playtestops-build"), "not an empty ownership marker");
    await RejectFiles(nonemptyMarker, "nonempty ownership marker", nonemptyMarker.ExecutablePath);

    foreach (var invalidManifest in new[] { "protocol", "buildId", "revision", "activeTarget", "malformed" })
    {
        var badManifest = await Seed(serviceDatabase);
        switch (invalidManifest)
        {
            case "protocol": await WriteManifest(badManifest, protocolVersion: 2); break;
            case "buildId": await WriteManifest(badManifest, id: Guid.NewGuid().ToString("N")); break;
            case "revision": await WriteManifest(badManifest, revision: "a-different-revision"); break;
            case "activeTarget": await WriteManifest(badManifest, target: "StandaloneLinux64"); break;
            default: await File.WriteAllTextAsync(badManifest.ManifestPath, "{not-json"); break;
        }
        await RejectFiles(badManifest, "invalid manifest " + invalidManifest, badManifest.ExecutablePath);
    }

    foreach (var metadataField in new[] { "executable", "manifest" })
    {
        var badPath = await Seed(serviceDatabase);
        if (metadataField == "executable") badPath.ExecutablePath = Path.Combine(badPath.OutputPath, "Different.exe");
        else badPath.ManifestPath = Path.Combine(badPath.OutputPath, "Different.json");
        await UpdateRecord(serviceDatabase, badPath);
        await RejectFiles(badPath, "mismatched " + metadataField + " metadata", Path.Combine(badPath.OutputPath, "Player.exe"));
    }

    foreach (var malformedPath in new[] { "relative", "traversal", "wrong-id", "device", "ads" })
    {
        var badPath = await Seed(serviceDatabase);
        var protectedPlayer = badPath.ExecutablePath;
        badPath.OutputPath = malformedPath switch
        {
            "relative" => Path.Combine("Builds", "PlaytestOps", badPath.Id),
            "traversal" => Path.Combine(Path.GetDirectoryName(badPath.OutputPath)!, "..", "PlaytestOps", badPath.Id),
            "wrong-id" => Path.Combine(Path.GetDirectoryName(badPath.OutputPath)!, Guid.NewGuid().ToString("N")),
            "device" => OperatingSystem.IsWindows() ? @"\\?\" + badPath.OutputPath : badPath.OutputPath + ":device",
            _ => badPath.OutputPath + ":stream"
        };
        await UpdateRecord(serviceDatabase, badPath);
        await RejectFiles(badPath, "unsafe recorded output " + malformedPath, protectedPlayer);
    }

    if (OperatingSystem.IsWindows())
    {
        var locked = await Seed(serviceDatabase);
        using (var pin = new FileStream(locked.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await RejectFiles(locked, "locked output file", locked.ExecutablePath, previewDetectsFailure: false);
            Check(File.Exists(Path.Combine(locked.OutputPath, "Player_Data", "nested", "fixture.data")),
                "all-file preflight leaves unlocked neighbors intact when another file is locked");
        }
        Check((await deletion.DeleteAsync(locked.Id, true, CancellationToken.None)).Deleted,
            "file deletion succeeds after the disposable file pin is released");

        var readOnly = await Seed(serviceDatabase);
        File.SetAttributes(readOnly.ExecutablePath, File.GetAttributes(readOnly.ExecutablePath) | FileAttributes.ReadOnly);
        try { await RejectFiles(readOnly, "read-only output file", readOnly.ExecutablePath, previewDetectsFailure: false); }
        finally { File.SetAttributes(readOnly.ExecutablePath, FileAttributes.Normal); }

        var descendant = await Seed(serviceDatabase);
        var outside = Path.Combine(scratch, "outside-target");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "must-survive.data");
        await File.WriteAllTextAsync(outsideFile, "not part of the approved build");
        var descendantLink = Path.Combine(descendant.OutputPath, "Player_Data", "external");
        CreateDirectoryLink(descendantLink, outside);
        await RejectFiles(descendant, "linked descendant directory", outsideFile, previewDetectsFailure: false);

        var linkedRoot = await Seed(serviceDatabase, files: false);
        Directory.CreateDirectory(Path.GetDirectoryName(linkedRoot.OutputPath)!);
        CreateDirectoryLink(linkedRoot.OutputPath, descendant.OutputPath);
        await RejectFiles(linkedRoot, "linked output root", descendant.ExecutablePath);

        var linkedAncestor = await Seed(serviceDatabase, files: false);
        var projectRoot = Directory.GetParent(linkedAncestor.OutputPath)!.Parent!.Parent!.FullName;
        Directory.CreateDirectory(projectRoot);
        var ancestorTarget = Path.Combine(scratch, "ancestor-target");
        Directory.CreateDirectory(Path.Combine(ancestorTarget, "PlaytestOps", linkedAncestor.Id));
        var ancestorSentinel = Path.Combine(ancestorTarget, "keep.data");
        await File.WriteAllTextAsync(ancestorSentinel, "protected ancestor target");
        CreateDirectoryLink(Path.Combine(projectRoot, "Builds"), ancestorTarget);
        await RejectFiles(linkedAncestor, "linked output ancestor", ancestorSentinel);
    }

    // No native process is started. A synthetic private reservation exercises the
    // supervisor's actual deletion guard, including the exited-but-draining gap.
    var reserved = await Seed(serviceDatabase);
    var supervisor = provider.GetRequiredService<StreamingSupervisor>();
    var lease = await supervisor.AcquireBuildDeletionLeaseAsync(Guid.NewGuid().ToString("N"), CancellationToken.None)
        ?? throw new InvalidOperationException("The idle fixture supervisor did not grant its deletion lease.");
    Task<StreamingResult> blockedStart;
    try
    {
        blockedStart = supervisor.StartSessionAsync(new StreamingStartRequest(Guid.NewGuid().ToString("N")),
            "http://127.0.0.1:1", CancellationToken.None);
        await Task.Delay(100);
        Check(!blockedStart.IsCompleted, "deletion lifecycle lease serializes a concurrent stream start before artifact validation");
    }
    finally { lease.Dispose(); }
    Check((await blockedStart).StatusCode == 400 && supervisor.Snapshot() is null,
        "released lifecycle lease permits only the unknown-build rejection, never a player launch");
    var currentField = typeof(StreamingSupervisor).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Supervisor reservation field changed; update the explicit fixture.");
    var sessionType = currentField.FieldType;
    var reservation = Activator.CreateInstance(sessionType, nonPublic: true)
        ?? throw new InvalidOperationException("Could not construct the disposable native-session reservation.");
    void Set(string name, object? value) => (sessionType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Supervisor fixture field changed: " + name)).SetValue(reservation, value);
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var fakeChild = new Process();
    using var fakeArtifact = new StreamingArtifact(reserved.ExecutablePath, reserved.ProjectName, Array.Empty<FileStream>());
    Set("Id", Guid.NewGuid().ToString("N")); Set("InstanceId", Guid.NewGuid().ToString("N")); Set("BuildId", reserved.Id);
    Set("ProjectName", reserved.ProjectName); Set("Origin", "http://127.0.0.1:1"); Set("Token", "disposable-noncredential");
    Set("Child", fakeChild); Set("Artifact", fakeArtifact); Set("Completion", completion.Task);
    currentField.SetValue(supervisor, reservation);
    try
    {
        foreach (var phase in new[] { "Starting", "Stopping", "Failed" })
        {
            Set("State", phase); Set("Exited", false); Set("Stopping", phase == "Stopping");
            Check(!(await deletion.DeleteAsync(reserved.Id, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Deleted,
                phase + " owned-native reservation protects metadata even for a terminal build");
        }
        Set("Exited", true); Set("State", "Stopped");
        Check(!(await deletion.DeleteAsync(reserved.Id, false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Deleted,
            "exited native reservation remains protected while cleanup completion is pending");
        Check((await Counts(serviceDatabase, reserved.Id)) == (true, 2) && File.Exists(reserved.ExecutablePath),
            "native lifecycle rejections preserve history, captured logs and output");
        var faultedCleanup = Task.FromException(new IOException("Synthetic cleanup failure; no native process exists."));
        _ = faultedCleanup.Exception;
        foreach (var cleanup in new[] { faultedCleanup, Task.FromCanceled(new CancellationToken(canceled: true)) })
        {
            Set("Completion", cleanup);
            Check(!(await deletion.DeleteAsync(reserved.Id, false, CancellationToken.None)).Deleted,
                "faulted or canceled native cleanup does not authorize deletion");
            Check(!string.IsNullOrWhiteSpace((await deletion.PreviewAsync(reserved.Id, CancellationToken.None))?.BlockedReason),
                "incomplete native cleanup remains visibly blocked in its confirmation preview");
        }
        Set("Completion", completion.Task);
        completion.SetResult();
        Check((await deletion.DeleteAsync(reserved.Id, false, CancellationToken.None)).Deleted,
            "completed native cleanup releases metadata deletion without launching a player");
    }
    finally { currentField.SetValue(supervisor, null); }
}

async Task HttpChecks()
{
    if (!File.Exists(appDll)) throw new FileNotFoundException("Build the updated app into <isolated-output-directory>/build first.", appDll);
    int port;
    using (var reservation = new TcpListener(IPAddress.Loopback, 0))
    {
        reservation.Start();
        port = ((IPEndPoint)reservation.LocalEndpoint).Port;
    }
    var address = "http://127.0.0.1:" + port;
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = appRoot, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add(appDll); start.ArgumentList.Add("--contentRoot"); start.ArgumentList.Add(appRoot);
    start.ArgumentList.Add("--urls"); start.ArgumentList.Add(address);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ConnectionStrings__DefaultConnection"] = "Data Source=" + httpDatabase;
    start.Environment["LOCALAPPDATA"] = Path.Combine(scratch, "local-app-data");
    Directory.CreateDirectory(Path.Combine(start.Environment["LOCALAPPDATA"]!, "ASP.NET", "DataProtection-Keys"));
    server = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated deletion dashboard.");
    server.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (serverLogs) serverLogs.Add(e.Data); };
    server.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (serverLogs) serverLogs.Add(e.Data); };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
        { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
    var ready = false;
    for (var attempt = 0; attempt < 100; attempt++)
    {
        if (server.HasExited) throw new InvalidOperationException("Isolated deletion server exited during startup.");
        try { using var response = await client.GetAsync("/Editors"); if (response.IsSuccessStatusCode) { ready = true; break; } }
        catch (HttpRequestException) { }
        await Task.Delay(100);
    }
    Check(ready, "deletion dashboard starts on a private dynamic port with a disposable database");

    var build = await Seed(httpDatabase);
    var neighbor = await Seed(httpDatabase);
    var route = "/Builds/" + build.Id + "/Delete";
    using var preview = await client.GetAsync(route);
    var html = await preview.Content.ReadAsStringAsync();
    Check(preview.StatusCode == HttpStatusCode.OK && html.Contains(build.Id, StringComparison.Ordinal),
        "real Razor confirmation page identifies the exact build");
    Check(preview.Headers.CacheControl?.NoStore == true, "delete preview is no-store");
    var anti = WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    Check(!string.IsNullOrWhiteSpace(anti), "real Razor confirmation form supplies an antiforgery token");
    Check((await Counts(httpDatabase, build.Id)) == (true, 2) && File.Exists(build.ExecutablePath),
        "GET preview does not delete history, logs or files");

    async Task<HttpResponseMessage> Post(string target, bool token = true, bool confirmed = true,
        bool deleteFiles = false, string? origin = null, string? host = null, string? fetchSite = null, string? injectedPath = null)
    {
        var form = new Dictionary<string, string> { ["deleteFiles"] = deleteFiles.ToString().ToLowerInvariant() };
        if (token) form["__RequestVerificationToken"] = anti;
        if (confirmed) form["confirmed"] = "true";
        if (injectedPath is not null) { form["outputPath"] = injectedPath; form["executablePath"] = Path.Combine(injectedPath, "Player.exe"); }
        var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new FormUrlEncodedContent(form) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        if (fetchSite is not null) request.Headers.Add("Sec-Fetch-Site", fetchSite);
        return await client.SendAsync(request);
    }

    using (var response = await Post(route, token: false))
        Check(response.StatusCode == HttpStatusCode.BadRequest, "delete POST rejects missing antiforgery token");
    using (var response = await Post(route, confirmed: false, origin: address))
        Check(response.StatusCode != HttpStatusCode.Redirect && response.StatusCode != HttpStatusCode.SeeOther,
            "unconfirmed POST cannot submit the destructive action");
    using (var response = await Post(route, origin: address, host: "attacker.example"))
        Check(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest,
            "delete POST rejects a non-loopback Host");
    using (var response = await Post(route, origin: "https://attacker.example"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "valid antiforgery token cannot bypass a hostile Origin");
    using (var response = await Post(route, origin: address, fetchSite: "cross-site"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "cross-site Fetch Metadata cannot authorize deletion");
    Check((await Counts(httpDatabase, build.Id)) == (true, 2) && File.Exists(build.ExecutablePath),
        "all rejected HTTP attempts preserve history, logs and output");
    using (var badId = await client.GetAsync("/Builds/not-a-build-id/Delete"))
        Check(badId.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, "malformed deletion ID is rejected");
    using (var unknown = await client.GetAsync("/Builds/" + Guid.NewGuid().ToString("N") + "/Delete"))
        Check(unknown.StatusCode == HttpStatusCode.NotFound, "unknown deletion ID returns 404");

    using (var success = await Post(route, origin: address))
        Check(success.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther,
            "local confirmed antiforgery-protected metadata deletion redirects successfully");
    Check((await Counts(httpDatabase, build.Id)) == (false, 0) && File.Exists(build.ExecutablePath),
        "real HTTP metadata deletion cascades logs while preserving files");
    using (var details = await client.GetAsync("/Builds/" + build.Id))
        Check(details.StatusCode == HttpStatusCode.NotFound, "deleted build detail route returns 404");
    using (var logFile = await client.GetAsync("/Builds/" + build.Id + "?handler=LogFile"))
        Check(logFile.StatusCode == HttpStatusCode.NotFound, "deleted full-log download returns 404");
    Check(!(await client.GetStringAsync("/Builds")).Contains(build.Id, StringComparison.Ordinal),
        "deleted build disappears from saved history");
    using (var state = await client.GetAsync("/api/streaming/state"))
        Check(!(await state.Content.ReadAsStringAsync()).Contains(build.Id, StringComparison.Ordinal),
            "deleted successful build disappears from streaming selection");
    using (var replay = await Post(route, origin: address))
        Check(replay.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict,
            "replayed destructive form cannot delete a different build");
    Check((await Counts(httpDatabase, neighbor.Id)) == (true, 2) && File.Exists(neighbor.ExecutablePath),
        "HTTP deletion leaves another build, its logs and output untouched");

    var held = await Seed(httpDatabase, "Unknown");
    using (var blocked = await Post("/Builds/" + held.Id + "/Delete", origin: address, deleteFiles: true))
        Check(blocked.StatusCode == HttpStatusCode.Conflict, "real HTTP rejects a protected unknown build");
    Check((await Counts(httpDatabase, held.Id)) == (true, 2) && File.Exists(held.ExecutablePath),
        "HTTP protected-state rejection leaves all saved data intact");

    if (OperatingSystem.IsWindows())
    {
        var files = await Seed(httpDatabase);
        var protectedPath = Path.Combine(scratch, "http-form-injected-path");
        Directory.CreateDirectory(protectedPath);
        var sentinel = Path.Combine(protectedPath, "must-survive.data");
        await File.WriteAllTextAsync(sentinel, "form paths are not authority");
        using (var response = await Post("/Builds/" + files.Id + "/Delete", origin: address, deleteFiles: true, injectedPath: protectedPath))
            Check(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.SeeOther,
                "real HTTP optional file deletion uses the persisted record instead of browser paths");
        Check(!Directory.Exists(files.OutputPath) && (await Counts(httpDatabase, files.Id)) == (false, 0),
            "HTTP optional deletion removes only its validated exact build output and history");
        Check(File.Exists(sentinel) && File.Exists(neighbor.ExecutablePath),
            "injected browser paths and unrelated outputs are not deleted");
    }
    if (previewMode)
    {
        var previews = new List<object>();
        foreach (var item in new[]
        {
            (Name: "Delete preview: history only", State: "Succeeded", MissingMarker: false),
            (Name: "Delete preview: history and files", State: "Succeeded", MissingMarker: false),
            (Name: "Delete preview: protected unknown", State: "Unknown", MissingMarker: false),
            (Name: "Delete preview: missing ownership marker", State: "Failed", MissingMarker: true)
        })
        {
            var fixture = await Seed(httpDatabase, item.State);
            fixture.ProjectName = item.Name;
            await UpdateRecord(httpDatabase, fixture);
            if (item.MissingMarker) File.Delete(Path.Combine(fixture.OutputPath, ".playtestops-build"));
            previews.Add(new { item.Name, fixture.Id, url = address + "/Builds/" + fixture.Id + "/Delete", outputPath = fixture.OutputPath });
        }
        Console.WriteLine("PRIVATE DELETE PREVIEW: " + JsonSerializer.Serialize(new { historyUrl = address + "/Builds", fixtures = previews }));
    }
}

try
{
    Directory.CreateDirectory(output);
    Directory.CreateDirectory(scratch);
    await ServiceChecks();
    await HttpChecks();
    Check(checks >= 60, "verifier completed a nonempty minimum assertion matrix");
    Console.WriteLine($"BUILD DELETION VERIFICATION PASSED: {checks} checks. No Unity project or native player was used.");
    if (previewMode)
    {
        Console.WriteLine("Private preview is running. Press Enter or Ctrl+C to stop only its isolated child server.");
        using var previewStop = new CancellationTokenSource();
        ConsoleCancelEventHandler stopHandler = (_, e) => { e.Cancel = true; previewStop.Cancel(); };
        Console.CancelKeyPress += stopHandler;
        try { await Task.WhenAny(Task.Run(() => Console.ReadLine()), Task.Delay(Timeout.Infinite, previewStop.Token)); }
        finally { Console.CancelKeyPress -= stopHandler; }
    }
}
catch (Exception error)
{
    Console.Error.WriteLine("BUILD DELETION VERIFICATION FAILED: " + error);
    lock (serverLogs) foreach (var line in serverLogs.TakeLast(25)) Console.Error.WriteLine(line);
    Environment.ExitCode = 1;
}
finally
{
    if (server is { HasExited: false })
    {
        server.Kill(entireProcessTree: true);
        server.WaitForExit(10000);
    }
    server?.Dispose();
    lock (serverLogs) File.WriteAllLines(Path.Combine(output, "build-deletion-server.log"), serverLogs);
    Console.WriteLine("Disposable fixture: " + scratch);
}

sealed class FixtureEnvironment(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "PlaytestOps.BuildDeletionVerification";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = root;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
