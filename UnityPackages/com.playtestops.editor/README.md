# PlaytestOps Editor — local preview

Editor-only UPM package for Unity 6.6+ (`6000.6`). The initial compatibility target is Unity 6000.6.0f1 and Unity Test Framework 1.8.0.

The package provides UI Toolkit controls, EditMode/PlayMode discovery, local single-case execution/results, and authenticated pairing/catalog synchronization with PlaytestOps. Selected dashboard runs and run-result synchronization are implemented. Cancellation is not implemented.

## Connect to the dashboard

Open the dashboard's `/Editors` page on localhost and generate a pairing code. In the Unity window, enter the dashboard origin (default `http://localhost:5282`) and code, then select **Connect to PlaytestOps**. Successful discovery synchronizes the full catalog. Refresh the dashboard to see the synchronized Unity tests.

Unity opens an outbound WebSocket. No Pipeline installation or inbound Editor port is required. Codes last five minutes and sessions last 12 hours; server/Editor restarts require pairing again. Script reloads resume a valid session. Project identity is stored in `ProjectSettings/PlaytestOps.json`; session tokens stay in SessionState. Use HTTPS for servers on other machines.

Endpoint input/output documentation is in `Obsiddy/Projects/PlaytestOps/Docs/`: `PlaytestOps Endpoints.md` and `Unity Package Endpoints.md`.

## Install

In Unity, open **Window > Package Management > Package Manager**, choose **Install package from disk**, and select this folder's `package.json`. Keep the package outside `Assets`; its assembly is restricted to the Editor. The package has no dependency on 1HourRoguelike scripts or Unity Pipeline.

For local development, the project's `Packages/manifest.json` may instead contain a dependency such as:

```json
"com.playtestops.editor": "file:C:/Users/Leo/Dev/PlaytestOps/UnityPackages/com.playtestops.editor"
```

This local absolute path is machine-specific. Other projects can install their own checkout from disk. A Git package URL can replace it when the package is published.

## Use

1. Wait for compilation/import and any existing Unity Test Runner run to finish.
2. Open **Tools > PlaytestOps > Open PlaytestOps**.
3. Choose **Discover / refresh tests**. The window lists individual cases from both modes. Hover a row for its full name, description, and skip reason.
4. Select one case and choose **Run selected test**. Discovery is rechecked before execution so a stale/ambiguous selection cannot turn into a suite run.
5. Read the lifecycle, raw Unity outcome, duration, message, stack trace, and output. **Open saved reports** opens `Library/PlaytestOps/Runs` in this project.

Empty discovery usually means no discoverable test assemblies exist. Use Package Manager's Samples section to import **Bridge Smoke Tests**: three passing EditMode cases (including two parameters), one explicit intentional failure, and two PlayMode cases. These are clearly labeled transport/runner placeholders, not gameplay coverage. The explicit failure must be deliberately selected. Save any modified scenes before running tests; PlaytestOps rejects the run rather than saving or discarding them.

## Lifetime and resource usage

- When disconnected and idle there is no polling, Update subscription, reconnect loop, socket, or automatic discovery. Settings restoration and queue readiness use temporary update hooks only while cleanup or a server probe is pending. Connected sockets use a 30-second keepalive; connection loss triggers bounded retries. Discovery runs on connection and explicit refresh, not on a timer.
- Closing the window does not disconnect or stop execution. Use Disconnect to stop networking. Session state and callback registration survive domain reloads, including entering/exiting Play Mode.
- Runs are started only by an explicit action. This package never resumes or retries execution automatically.
- JSON journal entries are replaced atomically. Final NUnit XML is saved alongside them. Reports can contain project/test output. Dashboard-owned runs upload bounded result text; JSON/XML files stay local.
- Session state is cleared by Unity when the Editor exits; saved files remain in `Library`. They are temporary project data and are removed if `Library` is deleted.

## Run log capture (0.2.0)

Dashboard-owned attempts include structured Debug/Warning/Error/Assert/Exception entries, UTC timestamps, capture order and stack traces in `run.update`. **View full logs** on the dashboard opens a dedicated attempt page with severity filters. Final Unity Test Framework output remains available separately. Expected errors do not force a passing test to fail.

Capture exists only during a PlaytestOps-owned run, from execution intent to the final callback; it includes runner/fixture messages during that interval. Thread-safe logging callbacks are drained on the Editor thread at most every 0.5 seconds and at run/reload boundaries. Run JSON, SessionState and the acknowledged snapshot outbox retain log data across normal domain reloads. Local runs retain logs in JSON but are not uploaded.

Per-attempt safety limits are 1000 entries, 192000 total message/stack characters, and 16000 per field. The dashboard clearly marks clipped/incomplete capture and shows omitted-entry counts. Abrupt Editor crashes can lose messages not yet persisted/uploaded; messages emitted while the managed domain is unloaded are outside the callback's coverage. Logs can contain secrets: do not expose the unauthenticated dashboard to the public internet.

Import **Connection Log Smoke Tests** for exactly two passing placeholders. Both emit debug, expected warning and expected error messages; the EditMode test also logs from a worker thread, while PlayMode spans frames and domain reload. These only verify connection/runner/log transport.

## Read-only dashboard source browsing

The dashboard's **Scripts** page requests saved working-copy C# source through the paired Editor socket, only when the local operator browses or refreshes. No source files are uploaded on connection or on an idle timer. Reads never modify files or save unsaved IDE buffers.

The default exposed author folders are `Assets/Scripts` and `Assets/Tests`. In the Unity panel, expand **Read-only source folders**, enter one explicit Assets subfolder per line, and choose **Save source folders**. Only this action writes `ProjectSettings/PlaytestOpsSource.json`; **Use default folders (unsaved)** just fills the form. An empty saved list disables browsing. Do not declare a folder containing imported vendor code as your author folder: ownership cannot be inferred automatically.

Packages/Library/engine code and folders named Plugins, Samples/Samples~, TutorialInfo, Template(s), Example(s), Standard Assets, ThirdParty/Third-Party, Vendor, External and generated/settings/VCS folders are always excluded. The entire Assets root is not allowed. Symlinks/junctions and unsafe paths are refused. UTF-8 source only; binary/NUL input and files over 128 KiB are not read. Metadata lists are capped at 1,000 paths/20,000 scanned entries, with explicit truncation. Settings are capped at 64 KiB; at most 16 folders and 512 characters per path are allowed.

`source.list` and `source.read` use correlated GUID request IDs and `source.list.result`/`source.read.result` replies. Responses are bounded to 1 MiB and requests to 10 seconds. Filesystem work runs off the Editor thread; Unity API/settings access remains on the Editor thread. Compilation/import/run activity returns a retry error. Source never enters SessionState/outbox or SQLite and is not replayed across reconnects. SHA-256 hashes exact UTF-8 file bytes; source responses include UTC last-modified time. This is not a repository revision browser or UVCS control integration.

## Other current limits

- Unity exposes global test callbacks but no public active-job enumeration in Test Framework 1.8.0. Runs observed after package initialization block concurrent PlaytestOps actions; a run already underway when this package is first loaded cannot be reliably identified. Wait for it to finish before using the preview.
- A lost final callback or abrupt Editor crash needs later recovery work. Do not infer success from a stale `Starting`/`Running` journal. Restart the Editor before starting another run if the preview remains busy after Unity has stopped. No automatic rerun occurs.
- XML may contain suite/fixture setup failures in addition to the selected leaf. Interrupted execution is recorded separately from an assertion failure. Skipped/inconclusive/raw outcomes are preserved, not converted into a pass.
- Other Unity versions, GPU tests, and real 1HourRoguelike gameplay behavior need their own validation. A successful package smoke test is not gameplay verification.

Dashboard **Queue test** requests join a FIFO queue and eventually execute exactly one case. Running and final results (outcome, duration, message, stack trace, output) return over the existing authenticated socket and persist in SQLite. The dashboard refreshes while queued/active work exists. Domain reloads preserve ownership and unacknowledged results; local-only runs are not uploaded. Dashboard timeouts mark incomplete runs Failed with an Interrupted/TimedOut explanation but never cancel or retry execution. Run requests currently require the server machine through localhost, pending remote access control.

## PlayMode domain reload

For PlaytestOps-owned PlayMode runs, the package temporarily enables domain reload by clearing only DisableDomainReload. It preserves the scene-reload preference and original options-enabled flag. Already-enabled domain reload and EditMode runs require no settings change. Both local and dashboard runs use this behavior.

The original settings are saved before mutation in `Library/PlaytestOps/play-mode-settings.json`. Restoration waits for the run to leave Play Mode and then restores the exact original options. Completion, errors, returning to Edit Mode after an abort, and normal Editor exit all request cleanup. A remaining record is recovered after Editor restart, provided Library and the plugin are still present. No idle update hook remains after cleanup. Restoration errors are displayed and block further runs until recovered.

The run must still report a result for the selected test; an empty Unity run is not accepted as success. This avoids the Test Framework 1.8.0 assembly-cache issue observed when domain reload stays disabled across repeated PlayMode runs.

Restoration updates both Unity memory and the two corresponding fields in `ProjectSettings/EditorSettings.asset` before deleting the recovery record. The targeted atomic file update preserves other settings and avoids saving unrelated assets. The expected Unity YAML fields are validated; an unsupported format or write failure retains the recovery record and reports an error.

Queue dispatch uses `run.probe` / `run.ready` before `run.request`. The readiness reply waits through PlayMode exit, restored settings, discovery, and acknowledgement of previous results. Reconnection invalidates the probe. The package does not maintain a second execution queue or poll the server when idle.
