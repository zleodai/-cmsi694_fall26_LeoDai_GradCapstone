using System.Text.Json;
using PlaytestOps.Sandboxie;

var checks = 0;
void Check(bool condition, string description) {
    if (!condition) throw new Exception(description);
    checks++;
}
async Task Reject(Func<Task> action, string description) {
    try { await action(); }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException or OperationCanceledException) {
        checks++;
        return;
    }
    throw new Exception(description);
}

using (var fixture = new Fixture()) {
    var options = fixture.Options;
    var client = new FakeClient(options.Boxes);
    var probe = new FakeProbe(client.Events);
    var launcher = new Launcher(client, probe);
    launcher.Validate(options);
    Check(client.Launches.Count == 0 && !File.Exists(options.ReceiptPath), "validation is read-only");
    await Reject(() => launcher.LaunchAsync(options with { Boxes = [] }, default), "explicit box selection required");
    await Reject(() => launcher.LaunchAsync(options with { Boxes = ["BoxA", "boxa"] }, default), "case-insensitive duplicate boxes rejected");
    await Reject(() => launcher.LaunchAsync(options with { Boxes = ["BoxA", "BoxB", "C", "D", "E"] }, default), "five sessions rejected");
    await Reject(() => launcher.LaunchAsync(options with { Boxes = ["BoxA", "MissingBox"] }, default), "every box is checked before startup");
    await Reject(() => launcher.LaunchAsync(options with { Boxes = ["BoxA", "box:/terminate_all"] }, default), "box command injection rejected");
    await Reject(() => launcher.LaunchAsync(options with { ReceiptPath = Path.Combine(Path.GetDirectoryName(options.GamePath)!, "receipt.json") }, default), "game directory cannot receive receipts");
    await Reject(() => launcher.LaunchAsync(options with { ReceiptPath = Path.Combine(Path.GetDirectoryName(options.SteamPath)!, "receipt.json") }, default), "Steam directory cannot receive receipts");
    await Reject(() => launcher.LaunchAsync(options with { GamePath = Path.Combine(fixture.Root, "missing.exe") }, default), "missing game rejected");
    await Reject(() => launcher.LaunchAsync(options with { SteamApiPath = fixture.OutsideApi }, default), "native API must belong to selected build");
    Check(client.Launches.Count == 0, "all invalid configurations fail before any Steam or game start");
    File.WriteAllText(fixture.AppId, "bad id");
    await Reject(() => launcher.LaunchAsync(options, default), "invalid existing App ID rejected");
    Check(File.ReadAllText(fixture.AppId) == "bad id", "App ID is never repaired or overwritten");
    File.WriteAllText(fixture.AppId, "480");
    File.Delete(fixture.AppId);
    await Reject(() => launcher.LaunchAsync(options, default), "missing App ID rejected");
    Check(!File.Exists(fixture.AppId), "missing App ID is never created");
}

using (var fixture = new Fixture()) {
    var options = fixture.Options;
    var client = new FakeClient(options.Boxes);
    client.Add("BoxA", options.SteamPath);
    var existingSteam = client.GetProcesses("BoxA").Single();
    var probe = new FakeProbe(client.Events);
    var launcher = new Launcher(client, probe);
    var receipt = await launcher.LaunchAsync(options, default);
    Check(receipt.Games.Count == 2, "two selected game instances recorded");
    Check(client.Launches.Count(launch => launch.Executable == options.SteamPath) == 1, "existing Steam reused; missing Steam started once");
    Check(client.Events.IndexOf("probe:BoxB") < client.Events.IndexOf("game:BoxA"), "all Steam authentication completes before first game launch");
    Check(receipt.Games.Select(game => game.SteamId).Distinct().Count() == 2, "separate identities retained");
    Check(client.Launches.All(launch => launch.Arguments.Count == 0), "game launch receives no Arena/KCP/shell arguments");
    Check(client.Launches.Where(launch => launch.Executable == options.GamePath).All(launch => launch.WorkingDirectory == Path.GetDirectoryName(options.GamePath)), "game working directory finds its existing App ID");
    Check(File.ReadAllText(fixture.AppId) == "480", "successful launch preserves build App ID");
    var saved = await Launcher.ReadReceiptAsync(options.ReceiptPath, default);
    Check(saved.Games.SequenceEqual(receipt.Games), "receipt round-trips exact process identities");
    await Reject(() => launcher.LaunchAsync(options, default), "existing receipt is never reused");
    launcher.Stop(saved);
    Check(client.Stopped.SequenceEqual(receipt.Games.Select(game => game.Process)), "stop targets only receipt game identities");
    Check(client.GetProcess(existingSteam.Pid) == existingSteam, "preexisting Steam survives game stop");
    Check(client.GetProcesses("BoxB").Any(process => process.ImageName == "steam.exe"), "new Steam remains signed in for reuse");
}

foreach (var identity in new ulong[] { 0, 11 }) {
    using var fixture = new Fixture();
    var client = new FakeClient(fixture.Options.Boxes);
    var probe = new FakeProbe(client.Events) { SecondIdentity = identity };
    await Reject(() => new Launcher(client, probe).LaunchAsync(fixture.Options, default), "zero or duplicate Steam identity blocks launch");
    Check(client.Launches.All(launch => launch.Executable != fixture.Options.GamePath), "identity failure launches no games");
    Check(client.Stopped.Count == 0, "identity failure never kills Steam or unrelated apps");
    var receipt = await Launcher.ReadReceiptAsync(fixture.Options.ReceiptPath, default);
    Check(receipt.Games.Count == 0, "identity failure receipt contains no false game ownership");
}

using (var fixture = new Fixture()) {
    var client = new FakeClient(fixture.Options.Boxes);
    var probe = new FakeProbe(client.Events) { FailBox = "BoxB" };
    await Reject(() => new Launcher(client, probe).LaunchAsync(fixture.Options, default), "sign-in/readiness failure is surfaced");
    Check(client.Launches.All(launch => launch.Executable != fixture.Options.GamePath), "readiness failure happens before games");
}

foreach (var failAfterSpawn in new[] { false, true }) {
    using var fixture = new Fixture();
    var client = new FakeClient(fixture.Options.Boxes) { FailGameBox = "BoxB", FailAfterSpawn = failAfterSpawn };
    var launcher = new Launcher(client, new FakeProbe(client.Events));
    await Reject(() => launcher.LaunchAsync(fixture.Options, default), "partial game startup failure is surfaced");
    Check(client.Stopped.Count == (failAfterSpawn ? 2 : 1), "partial startup rolls back only observed new games, including post-spawn launcher failure");
    Check(client.GetProcesses("BoxA").All(process => process.ImageName != "Game With Spaces.exe")
        && client.GetProcesses("BoxB").All(process => process.ImageName != "Game With Spaces.exe"), "no owned game remains after rollback");
    Check(client.Stopped.All(process => process.ImageName == "Game With Spaces.exe"), "rollback preserves Steam sessions");
    var receipt = await Launcher.ReadReceiptAsync(fixture.Options.ReceiptPath, default);
    Check(receipt.Games.Count == client.Stopped.Count, "rollback retains exact game records for recovery");
}

using (var fixture = new Fixture()) {
    var client = new FakeClient(["DefaultBox"]);
    var options = fixture.Options with { Boxes = ["DefaultBox"] };
    var launcher = new Launcher(client, new FakeProbe(client.Events));
    var receipt = await launcher.LaunchAsync(options, default);
    Check(receipt.Games.Single().BoxName == "DefaultBox", "explicit DefaultBox can supply an isolated client beside an external host");
}

using (var fixture = new Fixture()) {
    var options = fixture.Options with { Boxes = ["DefaultBox"], IncludeHost = true };
    var client = new FakeClient(["DefaultBox", HostSessionClient.Host]);
    var launcher = new Launcher(client, new FakeProbe(client.Events));
    var receipt = await launcher.LaunchAsync(options, default);
    Check(receipt.Games.Select(game => game.BoxName).ToHashSet().SetEquals(["DefaultBox", HostSessionClient.Host]), "explicit host opt-in launches host and selected sandbox");
    Check(receipt.Games.Select(game => game.SteamId).Distinct().Count() == 2, "host identity participates in duplicate-account checks");
    Check(client.Events.FindAll(entry => entry.StartsWith("probe:")).Count == 2
        && client.Events.IndexOf("probe:DefaultBox") < client.Events.FindIndex(entry => entry.StartsWith("game:")), "host and sandbox authentication precedes both games");
    launcher.Stop(receipt);
    Check(client.Stopped.Count == 2 && client.Stopped.Any(process => process.BoxName == HostSessionClient.Host), "host and sandbox stop use their exact receipt records");
}

using (var fixture = new Fixture()) {
    var options = fixture.Options with { Boxes = ["BoxA", "BoxB", "BoxC", "BoxD"], IncludeHost = true };
    var client = new FakeClient(["BoxA", "BoxB", "BoxC", "BoxD", HostSessionClient.Host]);
    await Reject(() => new Launcher(client, new FakeProbe(client.Events)).LaunchAsync(options, default), "host counts toward four-player limit");
    Check(client.Launches.Count == 0, "excess players rejected before startup");
}

foreach (var includeHost in new[] { false, true }) {
    using var fixture = new Fixture();
    string[] boxes = includeHost ? ["BoxA", "BoxB", "BoxC"] : ["BoxA", "BoxB", "BoxC", "BoxD"];
    var options = fixture.Options with { Boxes = boxes, IncludeHost = includeHost };
    var client = new FakeClient(boxes.Concat(includeHost ? [HostSessionClient.Host] : []));
    var launcher = new Launcher(client, new FakeProbe(client.Events));
    var receipt = await launcher.LaunchAsync(options, default);
    Check(receipt.Games.Count == 4 && receipt.Games.Select(game => game.SteamId).Distinct().Count() == 4, "four requested contexts retain four distinct identities");
    Check(client.Events.TakeWhile(entry => !entry.StartsWith("game:")).Count(entry => entry.StartsWith("probe:")) == 4, "four-player game launch waits for all four authenticated identities");
    launcher.Stop(receipt);
    Check(client.Stopped.Count == 4, "all four owned games stop independently");
}

using (var fixture = new Fixture()) {
    var client = new FakeClient(fixture.Options.Boxes);
    var existing = client.Add("BoxB", fixture.Options.GamePath);
    await Reject(() => new Launcher(client, new FakeProbe(client.Events)).LaunchAsync(fixture.Options, default), "preexisting matching game blocks entire launch");
    Check(client.Launches.Count == 0 && client.GetProcess(existing.Pid) == existing, "preexisting game is neither adopted nor stopped");
}

using (var fixture = new Fixture()) {
    var client = new FakeClient(fixture.Options.Boxes);
    var launcher = new Launcher(client, new FakeProbe(client.Events));
    var receipt = await launcher.LaunchAsync(fixture.Options, default);
    var old = receipt.Games[0].Process;
    var replacement = old with { CreationTime = old.CreationTime + 1, ImageName = "Unrelated.exe" };
    client.Replace(replacement);
    try { launcher.Stop(receipt); throw new Exception("reused PID was accepted"); }
    catch (AggregateException exception) { Check(exception.InnerExceptions.Count == 1, "PID reuse is reported during stop"); }
    Check(client.GetProcess(replacement.Pid) == replacement, "reused PID is never terminated");
    Check(client.GetProcess(receipt.Games[1].Process.Pid) is null, "one stop failure does not prevent cleanup of other owned games");
}

using (var fixture = new Fixture()) {
    var wrongIdentity = new BoxProcess(42, "BoxA", "Different.exe", 1, 100);
    var path = fixture.Options.ReceiptPath;
    File.WriteAllText(path, JsonSerializer.Serialize(new LaunchReceipt(1, fixture.Options.GamePath, DateTimeOffset.UtcNow,
        [new OwnedGame("BoxA", 11, wrongIdentity)])));
    await Reject(() => Launcher.ReadReceiptAsync(path, default), "mismatched receipt executable rejected");
    File.WriteAllText(path, JsonSerializer.Serialize(new LaunchReceipt(99, fixture.Options.GamePath, DateTimeOffset.UtcNow, [])));
    await Reject(() => Launcher.ReadReceiptAsync(path, default), "unknown receipt version rejected");
}

Console.WriteLine($"Passed {checks} isolated launcher checks. Fake processes and Steam identities only; no Steam, Sandboxie or game was started by these checks.");

sealed class Fixture : IDisposable {
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "PlaytestOps-SandboxieChecks-" + Guid.NewGuid().ToString("N"));
    public LauncherOptions Options { get; }
    public string AppId { get; }
    public string OutsideApi { get; }
    public Fixture() {
        foreach (var directory in new[] { "Sandboxie", "Steam", "Game Build/Plugins/x86_64", "receipts", "other" })
            Directory.CreateDirectory(Path.Combine(Root, directory));
        var start = Path.Combine(Root, "Sandboxie", "Start.exe");
        var steam = Path.Combine(Root, "Steam", "steam.exe");
        var game = Path.Combine(Root, "Game Build", "Game With Spaces.exe");
        var api = Path.Combine(Root, "Game Build", "Plugins", "x86_64", "steam_api64.dll");
        OutsideApi = Path.Combine(Root, "other", "steam_api64.dll");
        foreach (var file in new[] { start, steam, game, api, OutsideApi }) File.WriteAllText(file, "fixture only - not executable");
        AppId = Path.Combine(Root, "Game Build", "steam_appid.txt");
        File.WriteAllText(AppId, "480");
        Options = new(start, steam, game, api, Path.Combine(Root, "receipts", "games.json"), ["BoxA", "BoxB"], 5);
    }
    public void Dispose() {
        var resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("PlaytestOps-SandboxieChecks-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture cleanup path");
        Directory.Delete(resolved, true);
    }
}

sealed record LaunchCall(string Box, string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments);
sealed class FakeClient(IEnumerable<string> boxes) : ISandboxieClient {
    private readonly List<BoxProcess> processes = [];
    private readonly string[] configured = boxes.ToArray();
    private int nextPid = 100;
    public List<string> Events { get; } = [];
    public List<LaunchCall> Launches { get; } = [];
    public List<BoxProcess> Stopped { get; } = [];
    public string? FailGameBox { get; init; }
    public bool FailAfterSpawn { get; init; }
    public IReadOnlyList<string> GetBoxes() => configured;
    public IReadOnlyList<BoxProcess> GetProcesses(string box) => processes.Where(process => process.BoxName == box).ToArray();
    public BoxProcess? GetProcess(int pid) => processes.SingleOrDefault(process => process.Pid == pid);
    public BoxProcess Add(string box, string executable) {
        var process = new BoxProcess(nextPid++, box, Path.GetFileName(executable), 1, nextPid * 1000L);
        processes.Add(process);
        return process;
    }
    public void Replace(BoxProcess replacement) {
        processes.RemoveAll(process => process.Pid == replacement.Pid);
        processes.Add(replacement);
    }
    public Task LaunchAsync(string box, string executable, string workingDirectory, IReadOnlyList<string> arguments, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        Launches.Add(new(box, executable, workingDirectory, arguments.ToArray()));
        var steam = Path.GetFileName(executable) == "steam.exe";
        Events.Add((steam ? "steam:" : "game:") + box);
        var fail = !steam && box == FailGameBox;
        if (!fail || FailAfterSpawn) Add(box, executable);
        if (fail) throw new InvalidOperationException("fixture launcher failure");
        return Task.CompletedTask;
    }
    public void StopProcess(BoxProcess expected) {
        var current = GetProcess(expected.Pid);
        if (current is null) return;
        if (current != expected) throw new InvalidOperationException("fixture PID reuse");
        Stopped.Add(expected);
        processes.Remove(current);
    }
}

sealed class FakeProbe(List<string> events) : ISteamReadinessProbe {
    public ulong SecondIdentity { get; init; } = 22;
    public string? FailBox { get; init; }
    public Task<ulong> GetSteamIdAsync(string box, LauncherOptions options, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        events.Add("probe:" + box);
        if (box == FailBox) throw new InvalidOperationException("fixture sign-in incomplete");
        return Task.FromResult(box switch { "BoxB" => SecondIdentity, "BoxC" => 44UL, "BoxD" => 55UL, HostSessionClient.Host => 33UL, _ => 11UL });
    }
}
