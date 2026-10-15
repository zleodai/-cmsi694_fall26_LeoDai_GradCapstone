# Windows build execution verification — 2026-10-15

Scope: an isolated Windows Unity **6000.6.0f1** fixture project with PlaytestOps Editor **0.5.0**, an active x64 Windows Build Profile, a profile-specific scene list and a distinct global scene list. This is native Editor/build/player evidence, separate from simulated protocol acceptance. The dirty 1HourRoguelike project was not used or modified.

## Isolated protocol and regression checks

- Build execution: **251** passing HTTP/WebSocket/SQLite checks, including origin/loopback/antiforgery, server-owned IDs/nonces, replay, project/session/token ownership, mutexes, restart/Unknown reconciliation, log sequencing/paging/download, bounds and immutable artifacts.
- Existing Build Configuration: **176** passing checks.
- Existing Connection: **126** passing checks.
- Existing Source: **104** passing checks.
- Existing UVCS: **55** pure-reader and **110** HTTP/bridge checks (also exercised together as **165**; the combined count is not an additional independent 165 tests).
- The current web application builds with **zero warnings/errors**.

These are fresh verifier process outcomes. Generated build directories can contain a copied historical `acceptance-results.json` with old seed/demo checks; that file is **not** evidence for the counts above. The verifier console outcomes and exit codes are authoritative for this run.

## Native builds and preserved output

All three calls were started through the dashboard, not by an independent script calling BuildPipeline.

| Dashboard build ID | Native result | Native bytes | Warnings / errors | Native duration |
| --- | --- | ---: | ---: | ---: |
| `6ae231fe3c2d4a3ba383977d88eb950d` | Succeeded | 96,579,604 | 2 / 0 | 24.75 s |
| `91948021717440c7b38d9da3b01ceee9` | Succeeded | 96,579,604 | 2 / 0 | 4.87 s |
| `1bfece69e14542c7b2177d35d3f4f702` | Failed (intentional build callback exception) | 0 | 2 / 2 | 3.49 s |

The successful builds used separate `Builds/PlaytestOps/<id>/` folders, and each manifest records `Assets/Scenes/ProfileProbe.unity`, the active profile `Assets/Settings/Build Profiles/PlaytestOps Windows Probe.asset`, and the actual Unity build GUID. The failure preserved its own folder/manifest with native result Failed rather than claiming a runnable player.

The second build did not overwrite the first. The first executable's SHA-256 remained unchanged, and both manifests' executable digests match the actual files:

`a4c71b1bfc3e42d02f2f129fe04adef3ebf2343a662884e4b99598e9dfc90e7d`

This is an executable digest, not a claim that every build file was hashed or that builds are generally deterministic. The manifests have distinct native build GUIDs and dashboard IDs.

## Live logs while the main thread is blocked

The fixture's `IPreprocessBuild` callback logged four `PLAYTESTOPS_BUILD_LIVE_STEP1..4` markers, with a 750 ms synchronous wait between each. The dashboard received those debug markers and a warning while the native build was still Running, before terminal state. This proves the socket/log path was progressing during the synchronous BuildPipeline call, not merely uploading logs after completion.

The first success exposed 16 captured entries and its complete log download; the forced failure exposed 19 entries spanning Debug, Warning, Error and Exception, including the real `BuildFailedException`. All three detail pages stopped status polling after terminal state. Captured messages/stack traces were downloadable, with no truncation reported for these small fixtures.

## Exact produced-player smoke proof

Each successful build's exact produced `Player.exe` was launched separately with the fixture's smoke flag. Both exited **0**, logged `PLAYTESTOPS_PLAYER_READY unity=6000.6.0f1 version=2.0.27`, and logged `PLAYTESTOPS_PLAYER_SMOKE_PASSED` after the fixture's two-second/frame gate:

- First build: `frames=171241`.
- Second build: `frames=171636`.

These checks demonstrate startup and a short live frame interval for this fixture. They do not prove arbitrary gameplay, multiplayer, or long-duration stability, and the dashboard itself did not launch the player.

## Retained scheduling failure and recovery

An earlier request, `0db9bbee428b43ac836145fc2fdd4321`, remained Preparing without entering BuildPipeline or creating output. The first implementation used `EditorApplication.delayCall`, which did not run reliably in the background Editor even after repaint/loop/focus requests. It was replaced with a consumed-once `EditorApplication.update` callback plus `QueuePlayerLoopUpdate`.

The old request was **not** manually replayed. The package reload reported Unknown, the operator verified there was no native build/output and explicitly acknowledged it as Interrupted, and the successful calls above used new IDs. Preserve this failure alongside the successful scheduling proof; replacing an unconfirmed old attempt with a later success would hide the original behavior.

## Preflight, layout and restart proof

The final live preflight suite passed **18 checks**. Dashboard requests with incompatible settings were rejected with actionable messages before creating output:

- Create Visual Studio Solution: `9b464c84cd9148a08f82e036d60b47ad` — Rejected.
- Combined X64ARM64 architecture: `c6cdd30046844b509209641b4a983437` — Rejected.

The fixture settings were restored after these checks. Across the live acceptance sequence, the global `ProjectSettings/ProjectSettings.asset` SHA-256 stayed unchanged:

`0d268d76ecf5b1ec1215d6016b78c079ca62537f6a7fe3e681ef1a6ba29a2b39`

The final layout suite passed **14 checks** covering build history and failed-build detail pages in dark/light themes at 390 px and 1440 px widths, with no horizontal overflow.

After a real dashboard-service restart, all **six** records retained their states and captured-log counts: two Succeeded, one Failed, two Rejected and the explicitly acknowledged Interrupted request. The same fixture was re-paired afterward. The restarted service used the identical final web DLL, SHA-256:

`2f710b6f91170d43134953b88c9d2027e5588aa4a711f0edfe8582286decf97e`

This restart check proves persistence of those completed/recovered records. Active-build restart/Unknown reconciliation is separately covered by the isolated protocol verifier; this sequence did not restart the service during a native BuildPipeline call.

## Remaining proof boundaries

The native evidence above covers this single Windows x64 fixture/profile and local loopback host. It is not ARM64/IL2CPP toolchain acceptance, a cloud/remote deployment test, a native-hang cancellation test, or LDAI-32 artifact-registry/player-control implementation. Recovery can truthfully report Unknown without proving native execution has ended. See the [execution/recovery contract](windows-build-execution.md) for the supported behavior and explicit limits.
