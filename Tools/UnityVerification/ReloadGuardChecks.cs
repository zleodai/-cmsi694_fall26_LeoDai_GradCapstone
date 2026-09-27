// Run through Unity CLI eval_file while the Editor is idle. No test/gameplay assets are modified.
var guard = typeof(PlaytestOps.Editor.PlaytestSession).Assembly.GetType("PlaytestOps.Editor.PlayModeReloadGuard");
var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
var originalOptions = UnityEditor.EditorSettings.enterPlayModeOptions;
var originalEnabled = UnityEditor.EditorSettings.enterPlayModeOptionsEnabled;
var journal = System.IO.Path.GetFullPath("Library/PlaytestOps/play-mode-settings.json");
var checks = new System.Collections.Generic.List<string>();
if (PlaytestOps.Editor.PlaytestSession.Busy || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) throw new System.Exception("Editor must be idle.");
System.Action<string> check = name => checks.Add(name);
System.Action<string, object[]> call = (name, arguments) => guard.GetMethod(name, flags).Invoke(null, arguments);
System.Action<bool,string> assert = (condition,message) => { if (!condition) throw new System.Exception(message); check(message); };
var id = System.Guid.NewGuid().ToString("N");
try
{
    UnityEditor.EditorSettings.enterPlayModeOptionsEnabled = true;
    UnityEditor.EditorSettings.enterPlayModeOptions = UnityEditor.EnterPlayModeOptions.DisableDomainReload | UnityEditor.EnterPlayModeOptions.DisableSceneReload;
    call("Begin", new object[]{id});
    assert(UnityEditor.EditorSettings.enterPlayModeOptions == UnityEditor.EnterPlayModeOptions.DisableSceneReload, "Override removes only DisableDomainReload");
    assert(System.IO.File.Exists(journal), "Original settings journal exists before execution");
    call("Complete", new object[]{System.Guid.NewGuid().ToString("N")});
    assert(!System.IO.File.ReadAllText(journal).Contains("\"restoreRequested\": true"), "Unrelated run cannot restore owned settings");
    call("Complete", new object[]{id});
    call("RestoreWhenIdle", new object[0]);
    assert(UnityEditor.EditorSettings.enterPlayModeOptions == (UnityEditor.EnterPlayModeOptions.DisableDomainReload | UnityEditor.EnterPlayModeOptions.DisableSceneReload) && UnityEditor.EditorSettings.enterPlayModeOptionsEnabled, "Completion/start-failure restores exact original options");
    assert(!System.IO.File.Exists(journal), "Successful restoration removes recovery journal");
    assert(System.IO.File.ReadAllText("ProjectSettings/EditorSettings.asset").Contains("m_EnterPlayModeOptions: 3"), "Restoration persists both original flags to disk");

    call("Begin", new object[]{id});
    call("Recover", new object[]{id});
    assert(UnityEditor.EditorSettings.enterPlayModeOptions == UnityEditor.EnterPlayModeOptions.DisableSceneReload, "Active run recovery preserves override through domain reload");
    call("Recover", new object[]{null});
    call("RestoreWhenIdle", new object[0]);
    assert(UnityEditor.EditorSettings.enterPlayModeOptions == (UnityEditor.EnterPlayModeOptions.DisableDomainReload | UnityEditor.EnterPlayModeOptions.DisableSceneReload), "Orphaned journal recovery restores saved settings");

    call("Begin", new object[]{id});
    call("RestoreOnQuit", new object[0]);
    assert(!System.IO.File.Exists(journal) && UnityEditor.EditorSettings.enterPlayModeOptions == (UnityEditor.EnterPlayModeOptions.DisableDomainReload | UnityEditor.EnterPlayModeOptions.DisableSceneReload), "Quit restoration preserves settings and clears journal");

    UnityEditor.EditorSettings.enterPlayModeOptionsEnabled = false;
    call("Begin", new object[]{id});
    assert(!System.IO.File.Exists(journal) && !UnityEditor.EditorSettings.enterPlayModeOptionsEnabled, "Disabled custom options require no override");
    UnityEditor.EditorSettings.enterPlayModeOptionsEnabled = true;
    UnityEditor.EditorSettings.enterPlayModeOptions = UnityEditor.EnterPlayModeOptions.DisableSceneReload;
    call("Begin", new object[]{id});
    assert(!System.IO.File.Exists(journal) && UnityEditor.EditorSettings.enterPlayModeOptions == UnityEditor.EnterPlayModeOptions.DisableSceneReload, "Already-enabled domain reload leaves settings unchanged");
}
finally
{
    call("RestoreOnQuit", new object[0]);
    UnityEditor.EditorSettings.enterPlayModeOptions = originalOptions;
    UnityEditor.EditorSettings.enterPlayModeOptionsEnabled = originalEnabled;
    call("PersistRestoredOptions", new object[0]);
}
return checks.ToArray();
