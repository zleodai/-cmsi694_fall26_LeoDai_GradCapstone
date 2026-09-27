"""Run the Editor adapter in an isolated Unity project, without touching a game project.

python run.py --unity ".../Editor/Unity.exe" --work-dir ".../new-empty-folder"
Requires Python 3 and an activated Unity 6000.6 Editor. No third-party Python modules.
"""
import argparse
import json
import shutil
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--unity', type=Path, required=True)
parser.add_argument('--work-dir', type=Path, required=True)
args = parser.parse_args()
tool_dir = Path(__file__).resolve().parent
package = tool_dir.parents[1] / 'UnityPackages/com.playtestops.editor'
project = args.work_dir.resolve()
if project.exists() and any(project.iterdir()):
    raise SystemExit('Use a new empty work directory; this verifier never rewrites an existing project.')
if not args.unity.is_file() or not (package / 'package.json').is_file():
    raise SystemExit('Unity executable or PlaytestOps package not found.')
(project / 'Packages').mkdir(parents=True, exist_ok=True)
(project / 'ProjectSettings').mkdir()
(project / 'Assets/Editor').mkdir(parents=True)
(project / 'Packages/manifest.json').write_text(json.dumps({'dependencies': {
    'com.playtestops.editor': 'file:' + package.as_posix(),
    'com.unity.test-framework': '1.8.0',
    'com.unity.modules.imgui': '1.0.0',
    'com.unity.modules.jsonserialize': '1.0.0',
    'com.unity.modules.uielements': '1.0.0'
}}, indent=2), encoding='utf-8')
(project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 6000.6.0f1\n', encoding='utf-8')
shutil.copytree(package / 'Samples~/BridgeSmokeTests', project / 'Assets/BridgeSmokeTests')
shutil.copyfile(tool_dir / 'PlaytestOpsValidation.cs', project / 'Assets/Editor/PlaytestOpsValidation.cs')
command = [str(args.unity), '-batchmode', '-projectPath', str(project),
           '-executeMethod', 'PlaytestOpsValidation.Start', '-logFile', str(project / 'verification.log')]
process = subprocess.Popen(command, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
try:
    code = process.wait(timeout=360)
except subprocess.TimeoutExpired:
    process.kill()  # Only this verifier's explicitly created process.
    process.wait()
    raise SystemExit('Verification timed out. Inspect ' + str(project / 'verification.log'))
success = project / 'verification-passed.json'
if code or not success.exists():
    raise SystemExit('Verification failed. Inspect ' + str(project / 'verification.log'))
print(success.read_text())
print('Reports:', project / 'Library/PlaytestOps/Runs')
