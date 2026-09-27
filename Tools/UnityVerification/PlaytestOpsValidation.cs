using System;
using System.IO;
using System.Linq;
using PlaytestOps.Editor;
using UnityEditor;
using UnityEngine;
using UnityEditor.TestTools.TestRunner.Api;

// Verification-only fixture. Never shipped or installed into the user's gameplay project.
[InitializeOnLoad]
public static class PlaytestOpsValidation
{
    private const string Prefix = "PlaytestOps.Validation.";
    private static double after;
    private static readonly string[] Names = {
        "PlaytestOps.Smoke.EditModeSmokeTests.PassingTest",
        "PlaytestOps.Smoke.EditModeSmokeTests.ParameterizedTest(2)",
        "PlaytestOps.Smoke.EditModeSmokeTests.IntentionalFailure",
        "PlaytestOps.Smoke.PlayModeSmokeTests.PassingTest"
    };

    static PlaytestOpsValidation()
    {
        SessionState.SetInt(Prefix + "reloads", SessionState.GetInt(Prefix + "reloads", 0) + 1);
        if (SessionState.GetBool(Prefix + "active", false))
        {
            EditorApplication.update += Tick;
            TestRunnerApi.RegisterTestCallback(new Trace());
        }
    }

    public static void Start()
    {
        SessionState.SetBool(Prefix + "active", true);
        SessionState.SetInt(Prefix + "step", -1);
        SessionState.SetString(Prefix + "deadline", (EditorApplication.timeSinceStartup + 180).ToString(System.Globalization.CultureInfo.InvariantCulture));
        EditorApplication.update -= Tick;
        EditorApplication.update += Tick;
        TestRunnerApi.RegisterTestCallback(new Trace());
        after = EditorApplication.timeSinceStartup + 2;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Tick()
    {
        try
        {
            var deadline = double.Parse(SessionState.GetString(Prefix + "deadline", "0"), System.Globalization.CultureInfo.InvariantCulture);
            Check(EditorApplication.timeSinceStartup < deadline, "Verification timed out. Last run: " + JsonUtility.ToJson(PlaytestSession.LastRun));
            if (EditorApplication.timeSinceStartup < after || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var step = SessionState.GetInt(Prefix + "step", -1);
            if (step == -1)
            {
                PlaytestOpsWindow.Open();
                Check(EditorWindow.GetWindow<PlaytestOpsWindow>().rootVisualElement.childCount > 0, "UI Toolkit window was empty.");
                EditorWindow.GetWindow<PlaytestOpsWindow>().Close();
                SessionState.SetInt(Prefix + "step", 0);
                PlaytestSession.Discover();
                return;
            }
            if (PlaytestSession.Busy) return;
            Check(string.IsNullOrEmpty(PlaytestSession.Error), PlaytestSession.Error);
            if (SessionState.GetBool(Prefix + "waiting", false))
            {
                var run = PlaytestSession.LastRun;
                Check(run != null && run.lifecycle == "Completed", "Run did not complete: " + JsonUtility.ToJson(run));
                Check(run.test.fullName == Names[step], "Wrong test ran.");
                Check(step == 2 ? run.outcome.StartsWith("Failed") : run.outcome == "Passed", "Unexpected outcome: " + run.outcome);
                if (step == 2) Check(run.message.Contains("sample failure") && !string.IsNullOrEmpty(run.stackTrace), "Failure details missing.");
                Check(File.Exists(Path.Combine(PlaytestSession.ResultsDirectory, run.id + ".xml")), "XML report missing.");
                var xml = new System.Xml.XmlDocument();
                xml.Load(Path.Combine(PlaytestSession.ResultsDirectory, run.id + ".xml"));
                var cases = xml.SelectNodes("//test-case");
                Check(cases.Count == 1 && cases[0].Attributes["fullname"].Value == Names[step], "A run executed more than the selected case.");
                Debug.Log("PLAYTESTOPS_VERIFIED " + JsonUtility.ToJson(run));
                File.WriteAllText("verified-" + step + ".json", JsonUtility.ToJson(run, true));
                SessionState.SetBool(Prefix + "waiting", false);
                SessionState.SetInt(Prefix + "step", ++step);
                after = EditorApplication.timeSinceStartup + 2;
                return;
            }
            if (step == Names.Length)
            {
                var reloads = SessionState.GetInt(Prefix + "reloads", 0);
                Check(reloads > 1, "PlayMode did not exercise domain reload.");
                File.WriteAllText("verification-passed.json", "{\"runs\":4,\"reloads\":" + reloads + "}");
                SessionState.SetBool(Prefix + "active", false);
                EditorApplication.Exit(0);
                return;
            }
            if (PlaytestSession.Catalog.Count == 0)
            {
                PlaytestSession.Discover();
                return;
            }
            if (step == 0)
            {
                Check(PlaytestSession.Catalog.Count == 6, "Expected exactly six sample cases, found " + PlaytestSession.Catalog.Count);
                var item = PlaytestSession.Catalog.First();
                CheckThrows(() => TestCatalog.Resolve(PlaytestSession.Catalog, "missing"));
                CheckThrows(() => TestCatalog.Resolve(new[] { item, item }, item.Key));
                var alias = new TestCase { uniqueName = "alias", mode = item.mode, assembly = item.assembly, fullName = item.fullName, runState = "Runnable" };
                CheckThrows(() => TestCatalog.Resolve(new[] { item, alias }, item.Key));
                Debug.Log("PLAYTESTOPS_VERIFIED selector validation and both discovery modes");
            }
            var selected = PlaytestSession.Catalog.Single(x => x.fullName == Names[step]);
            SessionState.SetBool(Prefix + "waiting", true);
            PlaytestSession.Run(selected.Key);
        }
        catch (Exception ex)
        {
            File.WriteAllText("verification-failed.txt", ex.ToString());
            Debug.LogError(ex);
            SessionState.SetBool(Prefix + "active", false);
            EditorApplication.Exit(1);
        }
    }

    private static void CheckThrows(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Unsafe selector was accepted.");
    }

    private sealed class Trace : ICallbacks
    {
        public void RunStarted(ITestAdaptor test) => Debug.Log("VALIDATION_RUN_START " + test.Id + " " + test.UniqueName);
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
        public void RunFinished(ITestResultAdaptor result) => Debug.Log("VALIDATION_RUN_FINISH " + result.Test.Id + " " + result.Test.UniqueName + " owned=" + PlaytestSession.LastRun?.rootId);
    }
}
