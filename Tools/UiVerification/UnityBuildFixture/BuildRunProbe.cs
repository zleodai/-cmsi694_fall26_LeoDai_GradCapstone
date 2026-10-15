using System;
using UnityEngine;

// An isolated verification fixture, not game or dashboard production code.
public sealed class BuildRunProbe : MonoBehaviour
{
    private float elapsed;
    private bool smoke;

    private void Start()
    {
        smoke = Array.IndexOf(Environment.GetCommandLineArgs(), "-playtestops-smoke") >= 0;
        Debug.Log("PLAYTESTOPS_PLAYER_READY unity=" + Application.unityVersion + " version=" + Application.version);
        if (smoke) Debug.LogWarning("PLAYTESTOPS_PLAYER_EXPECTED_WARNING: smoke log fixture");
    }

    private void Update()
    {
        elapsed += Time.unscaledDeltaTime;
        transform.Rotate(0f, 45f * Time.unscaledDeltaTime, 0f);
        if (smoke && elapsed >= 2f)
        {
            Debug.Log("PLAYTESTOPS_PLAYER_SMOKE_PASSED frames=" + Time.frameCount);
            Application.Quit(0);
        }
    }
}
