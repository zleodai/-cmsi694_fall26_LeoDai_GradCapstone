using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace PlaytestOps.Editor
{
    [InitializeOnLoad]
    public static class EditorConnection
    {
        private const string StateKey = "PlaytestOps.Connection";
        private static CancellationTokenSource lifetime;
        private static ClientWebSocket socket;
        private static readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
        private static Credentials credentials;
        private static bool needsDiscovery;
        private static int generation;
        public static event Action Changed;
        public static string Status { get; private set; } = "Disconnected";
        public static string Error { get; private set; }
        public static bool Connected => socket?.State == WebSocketState.Open && Status == "Connected";
        public static bool Active => lifetime != null;
        public static int SyncedCount { get; private set; }
        public static string LastSync { get; private set; }
        public static string DefaultAddress => EditorPrefs.GetString("PlaytestOps.ServerAddress", "http://localhost:5282");

        [Serializable] private sealed class Credentials { public string address; public string token; public string expiresAt; }
        [Serializable] private sealed class ProjectIdentity { public string projectId; }
        [Serializable] private sealed class PairInput { public string code; public string projectId; public string projectName; public string unityVersion; }
        [Serializable] private sealed class PairOutput { public string token = ""; public string expiresAt = ""; }
        [Serializable] private sealed class Envelope { public string type = ""; public string requestId = ""; public string error = ""; public int count = 0; public string runId; public string mode; public string uniqueName; public int sequence; }
        [Serializable] private sealed class RunMessage {
            public string type = "run.update"; public string runId; public int sequence; public string state; public string outcome;
            public double durationSeconds; public string message; public string stackTrace; public string output;
        }
        [Serializable] private sealed class Outbox { public List<RunMessage> items = new List<RunMessage>(); }
        private static Outbox outbox = new Outbox();
        private const string OutboxKey = "PlaytestOps.RunOutbox";
        [Serializable] private sealed class Catalog { public string type = "catalog.replace"; public string requestId; public TestCase[] tests; }

        static EditorConnection()
        {
            var pending = SessionState.GetString(OutboxKey, "");
            if (!string.IsNullOrEmpty(pending)) { try { outbox = JsonUtility.FromJson<Outbox>(pending) ?? new Outbox(); } catch { } }
            PlaytestSession.RunChanged += QueueRun;
            PlaytestSession.RemoteRejected += (id, error) => Queue(new RunMessage { runId = id, sequence = 1, state = "Failed", outcome = "Rejected", message = Clip(error, 16000) });
            PlaytestSession.CatalogChanged += PublishCatalog;
            PlaytestSession.Changed += DiscoverWhenReady;
            AssemblyReloadEvents.beforeAssemblyReload += StopForReload;
            EditorApplication.quitting += StopForReload;
            var saved = SessionState.GetString(StateKey, "");
            if (string.IsNullOrEmpty(saved)) return;
            try { credentials = JsonUtility.FromJson<Credentials>(saved); }
            catch { SessionState.EraseString(StateKey); return; }
            // delayCall can wait for an Inspector repaint while Unity is in the background.
            // This one-shot hook exists only while restoring a previously paired session.
            EditorApplication.update += ResumeAfterReload;
        }

        private static void ResumeAfterReload()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            EditorApplication.update -= ResumeAfterReload;
            Resume();
        }

        public static async void Connect(string address, string code)
        {
            if (Active) return;
            var currentGeneration = ++generation;
            lifetime = new CancellationTokenSource();
            var ct = lifetime.Token;
            Status = "Pairing"; Error = null; LastSync = null; Notify();
            try
            {
                var uri = ValidateAddress(address);
                var identityFile = Path.GetFullPath("ProjectSettings/PlaytestOps.json");
                ProjectIdentity identity;
                if (File.Exists(identityFile)) identity = JsonUtility.FromJson<ProjectIdentity>(File.ReadAllText(identityFile));
                else
                {
                    identity = new ProjectIdentity { projectId = Guid.NewGuid().ToString("N") };
                    File.WriteAllText(identityFile, JsonUtility.ToJson(identity, true));
                }
                if (identity == null || !Guid.TryParseExact(identity.projectId, "N", out _))
                    throw new InvalidOperationException("ProjectSettings/PlaytestOps.json has an invalid projectId.");
                var input = new PairInput { code = code.Trim(), projectId = identity.projectId,
                    projectName = Application.productName, unityVersion = Application.unityVersion };
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                using (var body = new StringContent(JsonUtility.ToJson(input), Encoding.UTF8, "application/json"))
                using (var response = await client.PostAsync(new Uri(uri, "api/editor/pair"), body, ct))
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                            ? "Invalid or expired pairing code. Generate a new code in the dashboard."
                            : "Pairing failed (HTTP " + (int)response.StatusCode + "). Check the dashboard address and try again.");
                    var result = JsonUtility.FromJson<PairOutput>(json);
                    if (result == null || string.IsNullOrEmpty(result.token)) throw new InvalidOperationException("The server returned an invalid pairing response.");
                    ct.ThrowIfCancellationRequested();
                    outbox.items.Clear(); SaveOutbox();
                    credentials = new Credentials { address = uri.AbsoluteUri, token = result.token, expiresAt = result.expiresAt };
                    SessionState.SetString(StateKey, JsonUtility.ToJson(credentials));
                    EditorPrefs.SetString("PlaytestOps.ServerAddress", uri.AbsoluteUri.TrimEnd('/'));
                }
                await ConnectionLoop(currentGeneration, ct);
            }
            catch (OperationCanceledException)
            {
                if (currentGeneration == generation) { Error = "Pairing timed out. Check the address and try again."; Stop(false); }
            }
            catch (Exception ex) { if (currentGeneration == generation) { Error = ex.Message; Stop(false); } }
        }

        private static Uri ValidateAddress(string address)
        {
            if (!Uri.TryCreate(address.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
                uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
                throw new InvalidOperationException("Use the server origin, for example http://localhost:5282, without a path.");
            if (uri.Scheme == "http" && !uri.IsLoopback)
                throw new InvalidOperationException("Use HTTPS when the server is on another machine.");
            return uri;
        }

        private static async void Resume()
        {
            if (Active || credentials == null) return;
            var currentGeneration = ++generation;
            lifetime = new CancellationTokenSource();
            try { await ConnectionLoop(currentGeneration, lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (currentGeneration == generation) { Error = ex.Message; Stop(false); } }
        }

        private static async Task ConnectionLoop(int currentGeneration, CancellationToken ct)
        {
            var failures = 0;
            while (!ct.IsCancellationRequested && currentGeneration == generation)
            {
                if (!DateTimeOffset.TryParse(credentials.expiresAt, out var expires) || expires <= DateTimeOffset.UtcNow)
                    throw new InvalidOperationException("Pairing expired. Generate a new code to connect again.");
                Status = failures == 0 ? "Connecting" : "Reconnecting"; Notify();
                using (var connection = new ClientWebSocket())
                {
                    socket = connection;
                    connection.Options.SetRequestHeader("Authorization", "Bearer " + credentials.token);
                    connection.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    var uri = new UriBuilder(new Uri(new Uri(credentials.address), "api/editor/connect"));
                    uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
                    try
                    {
                        using (var opening = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            opening.CancelAfter(TimeSpan.FromSeconds(10));
                            await connection.ConnectAsync(uri.Uri, opening.Token);
                        }
                        var ready = JsonUtility.FromJson<Envelope>(await Receive(connection, ct));
                        if (ready?.type != "session.ready") throw new InvalidOperationException("Unexpected server handshake.");
                        failures = 0; Status = "Connected"; Error = null; needsDiscovery = true;
                        Notify();
                        foreach (var pendingRun in outbox.items.ToArray()) PublishRun(pendingRun);
                        DiscoverWhenReady();
                        while (connection.State == WebSocketState.Open && !ct.IsCancellationRequested)
                        {
                            var json = await Receive(connection, ct);
                            if (json == null) break;
                            var message = JsonUtility.FromJson<Envelope>(json);
                            if (message.type == "catalog.accepted")
                            {
                                SyncedCount = message.count; LastSync = DateTime.Now.ToString("HH:mm:ss"); Error = null;
                            }
                            else if (message.type == "run.request") PlaytestSession.RunRemote(message.runId, message.mode, message.uniqueName);
                            else if (message.type == "run.accepted")
                            {
                                outbox.items.RemoveAll(x => x.runId == message.runId && x.sequence <= message.sequence);
                                SaveOutbox();
                            }
                            else if (message.type == "error") Error = message.error;
                            Notify();
                        }
                        ct.ThrowIfCancellationRequested();
                        throw new WebSocketException("The server closed the connection.");
                    }
                    catch (Exception ex) when (ex is WebSocketException || ex is HttpRequestException || ex is OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        if (++failures >= 3) throw new InvalidOperationException("Connection lost. Check the server and pair again if it restarted.");
                        Error = "Connection interrupted; retrying shortly."; Status = "Reconnecting"; Notify();
                    }
                    finally { if (ReferenceEquals(socket, connection)) socket = null; }
                }
                await Task.Delay(TimeSpan.FromSeconds(failures * 3), ct);
            }
        }

        private static void DiscoverWhenReady()
        {
            if (!Connected || !needsDiscovery || PlaytestSession.Busy || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            needsDiscovery = false;
            PlaytestSession.Discover();
        }

        private static async void PublishCatalog()
        {
            if (!Connected || !PlaytestSession.HasDiscovered) return;
            var connection = socket;
            var ct = lifetime.Token;
            var currentGeneration = generation;
            var message = new Catalog { requestId = Guid.NewGuid().ToString("N"), tests = PlaytestSession.Catalog.ToArray() };
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message));
            if (bytes.Length > 2 * 1024 * 1024 || message.tests.Length > 5000)
            { Error = "Catalog exceeds the server limit (5000 tests / 2 MiB)."; Notify(); return; }
            try
            {
                await SendLock.WaitAsync(ct);
                try { await connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct); }
                finally { SendLock.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (currentGeneration == generation) { Error = "Catalog sync failed: " + ex.Message; Notify(); connection.Abort(); }
            }
        }

        private static string Clip(string text, int limit) => string.IsNullOrEmpty(text) ? "" : text.Length <= limit ? text : text.Substring(0, limit - 20) + "\n[truncated]";

        private static void QueueRun(RunRecord run)
        {
            if (string.IsNullOrEmpty(run.dashboardRunId) || run.lifecycle == "Starting") return;
            Queue(new RunMessage {
                runId = run.dashboardRunId, sequence = run.revision,
                state = run.IsActive ? "Running" : run.lifecycle == "Completed" && run.outcome == "Passed" ? "Passed" : "Failed",
                outcome = run.IsActive ? "" : run.lifecycle == "Interrupted" ? "Interrupted" : Clip(run.outcome, 128),
                durationSeconds = run.durationSeconds, message = Clip(run.message, 16000),
                stackTrace = Clip(run.stackTrace, 32000), output = Clip(run.output, 64000)
            });
        }

        private static void Queue(RunMessage message)
        {
            outbox.items.RemoveAll(x => x.runId == message.runId);
            outbox.items.Add(message);
            SaveOutbox();
            PublishRun(message);
        }

        private static void SaveOutbox() => SessionState.SetString(OutboxKey, JsonUtility.ToJson(outbox));

        private static async void PublishRun(RunMessage message)
        {
            if (!Connected) return;
            var connection = socket;
            var ct = lifetime.Token;
            var currentGeneration = generation;
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message));
            try
            {
                await SendLock.WaitAsync(ct);
                try { await connection.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct); }
                finally { SendLock.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (currentGeneration == generation) { Error = "Run update not delivered: " + ex.Message; Notify(); connection.Abort(); }
            }
        }

        public static async void Disconnect()
        {
            var previous = credentials;
            Stop(true);
            if (previous == null) return;
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", previous.token);
                    using (await client.DeleteAsync(new Uri(new Uri(previous.address), "api/editor/session"))) { }
                }
            }
            catch { /* Local disconnect is complete even when the server is unavailable. */ }
        }

        private static void Stop(bool clearError)
        {
            ++generation;
            EditorApplication.update -= ResumeAfterReload;
            lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null;
            socket?.Abort(); socket = null; credentials = null; needsDiscovery = false;
            SessionState.EraseString(StateKey);
            Status = "Disconnected"; if (clearError) Error = null;
            Notify();
        }
        private static void StopForReload()
        {
            ++generation;
            EditorApplication.update -= ResumeAfterReload;
            lifetime?.Cancel(); socket?.Abort();
            // Keep session credentials only in SessionState; never write them to the project.
        }
        private static void Notify() => Changed?.Invoke();
        private static async Task<string> Receive(ClientWebSocket connection, CancellationToken ct)
        {
            using (var stream = new MemoryStream())
            {
                var buffer = new byte[4096]; WebSocketReceiveResult read;
                do
                {
                    read = await connection.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (read.MessageType == WebSocketMessageType.Close) return null;
                    if (read.MessageType != WebSocketMessageType.Text || stream.Length + read.Count > 65536)
                        throw new InvalidOperationException("Invalid server message.");
                    stream.Write(buffer, 0, read.Count);
                } while (!read.EndOfMessage);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
