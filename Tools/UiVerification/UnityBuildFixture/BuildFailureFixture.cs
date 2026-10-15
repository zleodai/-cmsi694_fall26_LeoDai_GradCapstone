using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public sealed class BuildFailureFixture : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        Debug.Log("PLAYTESTOPS_BUILD_FIXTURE: preprocessing " + report.summary.platform);
        Debug.LogWarning("PLAYTESTOPS_BUILD_EXPECTED_WARNING: validation fixture");
        if (File.Exists(Path.Combine(Application.dataPath, "../SlowBuild.flag")))
        {
            for (var step = 1; step <= 4; step++)
            {
                Debug.Log("PLAYTESTOPS_BUILD_LIVE_STEP " + step + "/4");
                System.Threading.Thread.Sleep(750);
            }
        }
        if (File.Exists(Path.Combine(Application.dataPath, "../ForceBuildFailure.flag")))
            throw new BuildFailedException("PLAYTESTOPS_BUILD_EXPECTED_FAILURE: requested verification failure");
    }
}
