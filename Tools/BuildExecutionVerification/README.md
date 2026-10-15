# Dashboard build execution verification

Build the web app into a separate temporary output directory, then run this verifier:

```powershell
dotnet build PlaytestOps.Web/PlaytestOps.Web.csproj -o <temporary-directory>/build
dotnet run --project Tools/BuildExecutionVerification/Checks.csproj -- <temporary-directory> <absolute-web-project-path>
```

The verifier reserves port 5296, starts and stops only its own server process, and uses a fresh disposable SQLite database under the supplied temporary directory. It pairs synthetic Unity Editors over WebSocket; it does not invoke Unity or claim a real Windows build or executable launch. Keep build outputs separate from any running dashboard process. Logs and fixture state are retained for inspection.

The integration matrix covers local-only access and antiforgery, exact connected Editor/project dispatch, server-issued build IDs and output locations, consumed request-key replay, per-project build/test mutual exclusion, build-token/session binding, immutable artifact identity, status and log sequence rules, error/state/size validation, paged history, safe HTML rendering, and disconnect/restart recovery that reports unknown work honestly rather than inventing cancellation or success. It exercises the implementation's documented update bounds (64 entries and 128,000 message/stack characters per batch; 10,000 entries and 8 MiB total retained text) and verifies terminal/unknown states stop polling. The harness has an explicit nonzero minimum assertion floor; zero tests or missing evidence are not a pass.

Latest run: 251 checks passed against the isolated `verify28-web2` web output on 2026-10-15; both the web and verifier builds reported 0 warnings and 0 errors. See the retained `verification-web2/build-execution-server.log` for the disposable server's startup and request diagnostics. This remains synthetic protocol/dashboard evidence only.

This synthetic harness does not prove that Unity emitted a runnable player. Native acceptance remains a separate gate: verify the executable and manifest exist under the immutable per-build folder, launch the executable and observe a clean process exit/fixture signal, and inspect Unity BuildReport counters. Also cover fidelity-sensitive build options against the installed Unity version: `CreateSolution` must not be mistaken for a player executable; `BuildScriptsOnly` needs a valid existing data folder and must be rejected or safely supported with a fresh unique output folder; `InstallInBuildFolder` must not redirect writes outside the server-owned build root; and multi-architecture output must be supported or rejected before dispatch with an actionable explanation. A synthetic `Succeeded` message is protocol coverage only, never build proof.
