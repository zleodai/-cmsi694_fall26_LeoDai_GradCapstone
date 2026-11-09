using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

// Explicit verification fixture. Not included in the runtime package or ordinary games.
public sealed class StreamingFixtureProbe : MonoBehaviour
{
    [Serializable] private sealed class Snapshot
    {
        public int frame, width, height, spaceDown, spaceUp, keyDown, keyUp, mouseDown, mouseUp, rightDown, rightUp, middleDown, middleUp;
        public float x, y, mouseX, mouseY;
        public bool spaceHeld;
        public string utc;
    }

    private InputAction move, space, click, rightClick, middleClick;
    private InputAction[] keys;
    private readonly Snapshot state = new Snapshot();
    private string output;
    private float nextWrite;
    private bool capturing;
    private bool logsEmitted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindAnyObjectByType<StreamingFixtureProbe>() != null) return;
        var root = new GameObject("Streaming verification fixture");
        DontDestroyOnLoad(root);
        root.AddComponent<StreamingFixtureProbe>();
    }

    private void Awake()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        output = Environment.GetEnvironmentVariable("PLAYTESTOPS_FIXTURE_OUTPUT");
        move = new InputAction("Move", InputActionType.Value);
        move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
        space = new InputAction("Fire", InputActionType.Button, "<Keyboard>/space");
        space.performed += _ => state.spaceDown++;
        space.canceled += _ => state.spaceUp++;
        click = new InputAction("Click", InputActionType.Button, "<Mouse>/leftButton");
        click.performed += _ => state.mouseDown++;
        click.canceled += _ => state.mouseUp++;
        rightClick = new InputAction("RightClick", InputActionType.Button, "<Mouse>/rightButton");
        rightClick.performed += _ => state.rightDown++;
        rightClick.canceled += _ => state.rightUp++;
        middleClick = new InputAction("MiddleClick", InputActionType.Button, "<Mouse>/middleButton");
        middleClick.performed += _ => state.middleDown++;
        middleClick.canceled += _ => state.middleUp++;
        keys = new InputAction[4];
        var names = new[] { "w", "a", "s", "d" };
        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = new InputAction(names[index], InputActionType.Button, "<Keyboard>/" + names[index]);
            keys[index].performed += _ => state.keyDown++;
            keys[index].canceled += _ => state.keyUp++;
            keys[index].Enable();
        }
        move.Enable(); space.Enable(); click.Enable(); rightClick.Enable(); middleClick.Enable();
        Debug.Log("PLAYTESTOPS_STREAM_FIXTURE_READY");
    }

    private void Update()
    {
        if (!logsEmitted && Time.realtimeSinceStartup > 10f)
        {
            logsEmitted = true;
            Debug.Log("PLAYTESTOPS_STREAM_EXPECTED_DEBUG: rendered fixture is alive");
            Debug.LogWarning("PLAYTESTOPS_STREAM_EXPECTED_WARNING: severity relay fixture");
            Debug.LogError("PLAYTESTOPS_STREAM_EXPECTED_ERROR: intentional severity relay fixture, not a gameplay failure");
        }
        var direction = move.ReadValue<Vector2>();
        state.x += direction.x * Time.unscaledDeltaTime;
        state.y += direction.y * Time.unscaledDeltaTime;
        state.spaceHeld = space.IsPressed();
        state.frame = Time.frameCount;
        state.width = Screen.width; state.height = Screen.height;
        if (Mouse.current != null)
        {
            var position = Mouse.current.position.ReadValue();
            state.mouseX = position.x; state.mouseY = position.y;
        }
        if (string.IsNullOrEmpty(output)) return;
        if (Time.realtimeSinceStartup >= nextWrite)
        {
            state.utc = DateTime.UtcNow.ToString("O");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, JsonUtility.ToJson(state, true));
            nextWrite = Time.realtimeSinceStartup + 0.1f;
        }
        var request = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "capture.request");
        if (!capturing && File.Exists(request))
        {
            File.Delete(request);
            StartCoroutine(Capture());
        }
    }

    private IEnumerator Capture()
    {
        capturing = true;
        yield return new WaitForEndOfFrame();
        var image = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        image.Apply();
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)), "native-frame.png"), image.EncodeToPNG());
        Destroy(image);
        capturing = false;
    }

    private void OnGUI()
    {
        var previous = GUI.color;
        GUI.color = new Color(0.04f, 0.06f, 0.12f);
        GUI.DrawTexture(new Rect(100, 80, 660, 150), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(120, 90, 620, 32), "PlaytestOps native framebuffer verification — " + Screen.width + " x " + Screen.height);
        GUI.Label(new Rect(120, 120, 620, 28), "WASD position: " + state.x.ToString("F3") + ", " + state.y.ToString("F3") + "   frame " + Time.frameCount);
        GUI.Label(new Rect(120, 150, 620, 28), "Space down/up: " + state.spaceDown + "/" + state.spaceUp + "   WASD: " + state.keyDown + "/" + state.keyUp + "   Clicks: " + state.mouseDown + "/" + state.mouseUp);
        GUI.Label(new Rect(120, 180, 620, 28), "Green rectangle = Space held; red = released. Cyan dot follows WASD.");
        GUI.color = state.spaceHeld ? new Color(0, 1, 0) : new Color(1, 0, 0);
        GUI.DrawTexture(new Rect(Screen.width - 220, Screen.height - 120, 160, 64), Texture2D.whiteTexture);
        GUI.color = Color.cyan;
        GUI.DrawTexture(new Rect(Screen.width / 2f + Mathf.Clamp(state.x * 40f, -200, 200), Screen.height / 2f - Mathf.Clamp(state.y * 40f, -120, 120), 24, 24), Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private void OnDestroy()
    {
        move?.Dispose(); space?.Dispose(); click?.Dispose(); rightClick?.Dispose(); middleClick?.Dispose();
        if (keys != null) foreach (var key in keys) key?.Dispose();
    }
}
