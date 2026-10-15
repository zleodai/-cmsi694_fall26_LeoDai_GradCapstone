# Local Windows builds

LDAI-28 adds `/Builds` and `/Builds/{id}`. It requires PlaytestOps Editor package **0.5.0+** on Unity 6000.6+. The settings snapshot advertises `buildExecutionSupported`; older settings-only packages show an update notice rather than an executable build form.

## What starts a build

1. A localhost operator refreshes `/Builds` for a connected Editor. The page reads the active Windows settings revision and issues a short-lived server-owned start nonce.
2. An antiforgery-protected POST binds the nonce to that project, exact Editor session and revision. The server rejects active/queued tests or an active/unknown build for the same project.
3. SQLite records a new server-generated build GUID, request nonce, project/session identity, configuration revision and SHA-256 hash of a random per-build secret **before** sending `build.request`. The secret is sent only over the authenticated Editor socket, never HTML/status JSON. A repeated form resolves to the existing record and does not execute again.
4. Unity durably records receipt in ignored `Library/PlaytestOps/BuildExecution/<id>/request.json` before validating or scheduling execution. Existing receipts—including damaged receipts—and existing output directories are never executed or overwritten again. A new attempt needs a new ID.
5. Unity rechecks settings identity/revision, idle/import/compilation/Play Mode/test/build state, saved scenes, enabled effective scenes and platform support immediately before invoking `BuildPipeline.BuildPlayer`.

Active Build Profiles use Unity's `BuildPlayerWithProfileOptions`: profile platform flags, Player Settings overrides, scenes and additive defines remain profile-owned. The adapter does not copy stale global values over them. Classic Windows platform builds use a guarded fixed built-in Unity option resolver, not UI/custom handler delegates or a hand-maintained subset of flags; unsupported versions reject the request instead of guessing.

The workflow produces one runnable `Player.exe`. **Create Visual Studio Solution**, **Build Scripts Only**, **Install In Build Folder**, and combined/universal multi-architecture outputs are rejected with actionable guidance; they conflict with a fresh isolated player artifact. Pick one architecture and disable those modes first. These settings remain editable in `/BuildSettings`; they are not silently overridden. Build And Run/remote deployment flags and caller-selected output paths/executable names are never used. Standard project build callbacks run as they do in Unity.

## Output ownership and evidence

Output is computed from the Editor project root and validated GUID: `Builds/PlaytestOps/<id>/`. Existing output is preserved, and symlink/junction output parents are rejected. No operation deletes previous builds. The file name is always `Player.exe`.

The result records immutable output, executable, manifest, active target and profile identity once output is acquired. `build-manifest.json` contains the dashboard build ID, native Unity build GUID/result, settings revision, effective scene list, Unity/profile/target, native report duration/size/warning/error counts and an artifact inventory. The executable has SHA-256; other files have relative paths and byte lengths. The artifact inventory is bounded to 10,000 files and explicitly marks truncation. This metadata is a starting point for LDAI-32's artifact registry, not a claim that a registry/player-control service already exists.

Success requires Unity's native BuildReport result `Succeeded`, zero native report errors, an existing `Player.exe`, a successfully written manifest and nonzero output bytes. Exported-project-only output is never called a runnable-player success. Dashboard builds never launch an EXE. Running the exact produced player and checking frame/exit evidence is a separate verification step.

## Status, logs and bounds

The background socket/log path continues while Unity's main thread is occupied by synchronous BuildPipeline work. Threaded application logs are captured immediately; BuildReport step messages are also appended after Unity returns. Duplicate report/console messages can appear. Full message and stack text is retained within the explicit bounds:

- 10,000 entries and 8 MiB of message/stack characters per build.
- 16,000 characters each for one message and its stack trace. Clipped entries and total dropped entries/parts are marked.
- Network batches: at most 64 entries / 128,000 characters; strict contiguous log sequence and monotonic update sequence.
- Status pages return up to 200 entries after a supplied sequence, drain subsequent pages, and offer a download of **all** captured database rows—not merely a recent tail.

The dashboard polls once per second only while Pending/Running and stops at terminal/Unknown status (after draining existing log pages). History/configuration refresh is manual. Sender and server tracking sleep when no build work exists. BuildReport warning/error totals replace provisional capture counters at completion; logged errors from unrelated/project logging are not substituted for the native summary result.

A heartbeat proves the reporting transport is alive, **not** that Unity's native pipeline is making progress. There is no fake percentage, automatic native cancellation, force-kill or arbitrary command endpoint. If the build hangs, inspect/cancel it in Unity. The dashboard never interprets lost reporting as a successful cancellation.

## Lifecycle and recovery

`Pending -> Running -> Succeeded / Failed / Rejected` is the normal flow. Delivery uncertainty, two minutes without reporting, or a dashboard restart changes active records to **Unknown**, not cancelled/failed-by-assumption. Pending/Running/Unknown reserves the project; tests and other builds cannot dispatch into it.

Unity's ignored Library ledger retains the secret, receipt, cumulative metadata, append-only JSONL logs and acknowledged sequence. Reconnect replays result/log batches, never an execution command. Authentication requires the current paired project and the matching per-build secret; this permits reconciliation after a server restart/new pairing session without accepting another project's results. A package/domain reload with an unfinished ledger reports Unknown and never reruns the ID. Only the most recent 50 local ledgers are considered for automatic reconciliation; older history/output remains on disk and in SQLite.

An explicit server rejection of a result payload is quarantined until another connection; it is not retried continuously. The local ledger records the delivery error, native execution is not cancelled, and the server eventually shows Unknown if no valid report arrives. Reconnect can retry reporting after the underlying protocol/configuration issue is resolved, never rebuild the ID.

For an Unknown result, first inspect Unity and output. The explicit **Acknowledge unknown result and release reservation** action marks the dashboard record Interrupted. It sends **no cancellation**, deletes nothing, and cannot overwrite a known terminal result. A genuinely still-busy Editor will reject another build even after the dashboard reservation is released. This action is an operator acknowledgement, not proof that native execution ended.

## HTTP and wire surface

- `GET /Builds?projectId=...`: connected selection, configuration/revision and latest 50 saved builds.
- `POST /Builds?handler=Start`: `projectId`, `editorSessionId`, `revision`, server-issued `requestKey`, antiforgery token. No ID/token/path/command comes from the browser.
- `GET /Builds/{id}` and `?handler=Status&after=N`: saved metadata plus paged logs. `?handler=LogFile` downloads all captured logs.
- `POST /Builds/{id}?handler=ResolveUnknown`: antiforgery-protected, Unknown-only reservation acknowledgement.
- `build.request {buildId,buildToken,revision}` -> `build.update` batches -> `build.accepted {buildId,sequence,logSequence,error,state}`. Foreign project/token/socket, invalid state/size/paths, log gaps, inconsistent success and mutable artifact identity are rejected. Duplicates cannot overwrite terminal data or append the same row twice.

All build/history/status/log/action routes require loopback remote IP **and** loopback Host. Responses are no-store; POSTs use Razor antiforgery. Build secrets are hashed in SQLite, retained only in ignored Library on the Editor, and excluded from HTML/status/logs/artifact manifests. Log rendering uses escaped text/textContent. There is no player-launch, remote-deploy, custom executable/command, artifact deletion or native-cancel endpoint.

## Verification boundary

`Tools/BuildExecutionVerification` covers simulated Editor HTTP/WebSocket/SQLite behavior and limits; it cannot prove native Unity builds. Live acceptance must demonstrate two dashboard-started successful builds in distinct preserved folders, status/log visibility **during** a deliberately slow synchronous callback, a real failed BuildReport, history after restart, and a smoke run of the exact produced executable. A successful web compile or fixture-only protocol test is not that evidence.

See [the dated verification record](windows-build-verification.md) for the fresh isolated checks, real build IDs/results, streaming proof, exact-player smoke evidence and the retained failed scheduling attempt.
