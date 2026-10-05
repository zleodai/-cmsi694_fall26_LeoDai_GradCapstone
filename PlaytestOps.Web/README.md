# PlaytestOps baseplate

One ASP.NET Core Razor Pages app, a query service, EF Core, and a persistent SQLite database. The dashboard uses a locally bundled htmx 2.0.8 script for manual refresh. It has no background browser polling; the Unity package synchronizes discovered tests over an authenticated connection.

## Run locally

Requires the .NET 10 SDK. From this project directory:

```powershell
dotnet restore
dotnet tool restore
dotnet run --launch-profile http
```

Open http://localhost:5282. The HTTPS profile is also available (`dotnet run --launch-profile https`).

Development startup applies pending migrations. The SQLite path defaults to `App_Data/playtestops.db`, resolved relative to the project content root rather than the shell's working directory. Override it with `ConnectionStrings__DefaultConnection`, e.g. `Data Source=C:/temp/playtestops-check.db` for isolated checks. Database files are ignored by Git.

The initial SQLite schema includes the existing Identity tables. The historical seed migration inserted four demo tests. RemoveDemoPlaytests removes those original unlinked records, so new databases finish migration with no tests until Unity synchronizes. Rolling back that cleanup does not recreate deleted demos. Normal startup does not re-seed, reset edited records, or recreate deleted records. Emptying the test table produces the empty state. The stock SQL Server template migration was replaced with a SQLite baseline; this does not migrate any existing LocalDB accounts/data.

## Structure

- `Models/Playtest.cs`: ID, name, description, and explicit integer status values.
- `Data/ApplicationDbContext.cs`: Identity and playtests, with a database status constraint.
- `Data/Migrations/`: schema and one-time demo data migrations.
- `Services/PlaytestService.cs`: asynchronous, no-tracking database reads.
- `Pages/Index.cshtml.cs`: page/fragment handlers and database-error handling.
- `Pages/Shared/_TestList.cshtml`: list, empty state, and readable load failure.
- `wwwroot/lib/htmx/`: pinned local htmx asset and license.

Refresh sends `GET /?handler=Tests` and swaps only the list. It re-queries SQLite, does not change test statuses, and falls back to a full page GET without JavaScript. Database errors return a readable 503 fragment; transport errors preserve the last list and label it potentially stale. No raw exception text is sent to users.

## Schema changes

```powershell
dotnet ef migrations add YourChange --output-dir Data/Migrations
dotnet ef database update
dotnet ef migrations has-pending-model-changes
```

Use migrations, not `EnsureCreated`. Non-Development deployments must apply migrations explicitly before starting the app. Registration/login UI, Identity page routes, and authentication services are removed for now. Dashboard reads are public; execution and pairing-code creation are restricted to localhost. Existing Identity database tables and migration history are retained to avoid deleting stored data; they do not enable authentication. Remote access/authentication policy is a separate implementation step; the local development certificate is not automatically trusted by phones.

## Acceptance checks

Use a disposable database via the connection override, not your working data:

1. Start in Development; confirm an empty test list, then pair Unity and synchronize its discovered tests.
2. Stop and restart with the same database; confirm the same IDs and row count.
3. Edit a row directly in the disposable database; Refresh must show the saved edit without a full navigation. Restart must preserve the edit.
4. Delete the demo rows in the disposable database; confirm the empty state, including after restart.
5. Temporarily make the disposable test table unavailable; confirm the readable error on initial load and Refresh, then restore it and refresh successfully.
6. Check desktop and narrow mobile layouts, repeated Refresh, and no automatic requests while idle.

The original baseplate has been extended with Unity execution and result reporting; see the current run bridge below.

## Verification performed (2026-09-25)

- Build: zero warnings/errors; EF reports no pending model changes.
- Thirteen real HTTP/SQLite checks passed using a disposable database, including seed count/statuses, Identity schema, restart persistence, escaped refreshed content, edited/deleted data preservation, invalid-status rejection, readable 503 responses, recovery, and empty state.
- Edge browser: changed SQLite data appears through htmx without navigation; readable database and offline errors; recovery and empty state; no refresh requests during a three-second idle observation.
- Desktop and 390px mobile viewport visually inspected; mobile horizontal overflow checked. This is browser viewport verification, not a physical-phone or remote-network test.
- Unity execution, cloud hosting, and idle resource budgets were not tested in this baseplate.

## Unity connection (2026-09-27)

`/Editors` creates short-lived pairing codes through a local antiforgery-protected form and displays connection state. Unity authenticates through `/api/editor/pair`, opens `/api/editor/connect` as a WebSocket, and sends complete discovery snapshots. `/api/editor/session` revokes its session on DELETE. The AddUnityCatalog EF migration persists project metadata and stable test identities; repeat discovery preserves statuses/IDs and marks missing cases unavailable.

Dashboard reads refresh automatically while queued/active attempts exist; idle refresh remains manual. No login/register pages were added. Generating pairing codes requires loopback; non-local Unity connections require HTTPS. Paired credentials are in server memory and expire after 12 hours, so server restart requires a new code. Selected dashboard run commands and their results now use the same socket; local-only Unity runs remain local.

Endpoint documentation: `C:\Users\Leo\Documents\files\Obsiddy\Projects\PlaytestOps\Docs\Index.md`.
Acceptance harness: `../Tools/ConnectionVerification`. Its 21 real HTTP/WebSocket/SQLite checks passed. Live Unity pairing, six-case sync, explicit disconnect, and script-reload reconnection were also verified in 1HourRoguelike.

## Dashboard run bridge (2026-09-27)

`POST /?handler=Run` is an antiforgery-protected localhost form. RunService saves a Queued PlaytestRun; RunMonitor waits for Unity readiness, records Pending/DispatchedAt, then sends `run.request` for one exact case, and commits `run.update` callbacks. Razor displays Queued #N/Starting/Running/Passed/Failed and the latest five attempts with duration, output, and errors. All attempts persist in SQLite through AddPlaytestRuns; discovery never resets them. Active queues refresh every three seconds; idle refresh is manual.

Each project has one active request and a FIFO queue of up to 100 waiting tests. Duplicate live attempts for the same test are rejected. Ownership and increasing revisions reject foreign/out-of-order updates; completed results are immutable. Unity uses a SessionState outbox across domain reloads and replays result updates, never execution requests. RunMonitor only queries during queued/active work: two-minute acknowledgement/offline grace, 30-minute deadline from dispatch. Server restart closes unfinished runs as Interrupted/Failed. These conditions do not stop Unity; inspect the Editor before retrying.

Cancellation, authenticated remote execution, historical report export, and uploading runs started by Unity's local Run button are outside this implementation.

## Connected-Editor test view (2026-10-05)

The main tests page and its refresh/run-response fragments now show only catalog rows whose Unity project currently has an open authenticated Editor WebSocket. Pairing without an open socket is not an active connection. If several Editors are connected, their projects' tests are included; disconnected projects and unlinked placeholders are excluded at the database query, without deleting saved tests or attempts.

With no active connection, the view asks the operator to connect an Editor. A connected project with no synchronized catalog shows a distinct discovery prompt. Refresh after connecting/disconnecting; idle polling remains disabled. Queued/active work keeps the existing three-second refresh alive through brief socket gaps so the view recovers after domain reload/reconnection. Direct `/Runs/{id}` links still open saved historical logs even if their Editor is disconnected.

Verification: 126 HTTP/WebSocket/SQLite checks passed, including active vs paired-only sessions, socket gaps/reconnects, multiple active projects, unlinked exclusions, empty states and preserved catalog/history. No schema change is required for this view filter.

## Read-only project source browser (2026-10-05)

Open **Scripts** in the dashboard navigation (`/Scripts`). It lists saved C# files from one currently connected Unity Editor and reads a selected file on demand. Search filters filenames/paths. The viewer labels saved working-copy content, shows byte count, UTC modification/read times and SHA-256, and HTML-encodes code. It is not an editor and does not fetch committed repository revisions.

Default author folders are `Assets/Scripts` and `Assets/Tests`. Configure explicit Assets subfolders in **Tools > PlaytestOps > Open PlaytestOps > Read-only source folders**, then select **Save source folders**. This explicit action writes only `ProjectSettings/PlaytestOpsSource.json`; browsing never writes project files. An empty folder list disables access. Package/Library/engine, vendor/plugin, sample/tutorial/template/example and generated folders are excluded. Folder declarations establish the intended ownership boundary; automatic filters cannot prove authorship of code copied into an allowed folder.

Source browsing requires a loopback caller and localhost/loopback Host header. Responses are no-store; source is neither persisted to SQLite nor logged. There is no idle scan or browser polling. The existing Editor socket carries typed list/read requests; `IProjectSourceProvider` isolates that working-copy transport from future VCS providers. Unity Version Control history, update/pull/check-in, source editing, unsaved IDE buffers and remote source access are not implemented.

Bounds: 16 author folders, 512-character canonical paths, 1,000 listed files, 20,000 scanned entries, 128 KiB per UTF-8 source file and 1 MiB source response. Oversized files are labeled but not read; clipped catalogs are explicit. Symlinks/junctions, traversal, binary/NUL text and invalid UTF-8 are refused. One request per project/four globally, with a 10-second deadline. Import/compilation/test activity returns a retry prompt. Disconnects/replaced sockets cancel pending reads; source is not replayed after reload.

Verification: see `../Tools/SourceVerification` and Obsiddy `Docs/Source Browser Verification.md`. The original connection/run/log harness remains at 126 passing checks. At the initial source-browser checkpoint, PlaytestOps Test had no eligible scripts under its default roots; imported package smoke tests and tutorial scripts were intentionally not shown. No schema migration is needed.

## Full playtest logs (2026-10-04)

Dashboard-owned runs now capture Unity debug messages, warnings, errors, assertions and exceptions with UTC timestamps, capture sequence and stack traces. The authenticated `run.update` snapshot carries these logs; SQLite saves them with the run result through `AddRunLogs`. Older clients and older attempts retain their existing framework output. Severity is independent of pass/fail: an expected `Debug.LogError` can belong to a passing test.

Select **View full logs** beside a latest or previous attempt to open `/Runs/{id}`. The page shows all retained entries, severity filters, expandable stack traces, result details and Unity Test Framework output. It refreshes every three seconds only while that attempt is queued/active. Idle runs remain manual; main-dashboard queries omit the structured log payload.

Capture is scoped from PlaytestOps execution intent through its final run callback, including fixture/runner messages during that interval. It is not a dump of the entire Editor console and does not upload runs started outside the dashboard. Threaded callbacks enqueue managed data; main-thread snapshots flush at most every 0.5 seconds and at lifecycle/domain-reload boundaries. Existing SessionState outbox replay preserves snapshots during reload/reconnect.

Safety limits per attempt: 1000 entries, 192000 combined message/stack characters, 16000 characters per field. Clipped fields and omitted entries set an explicit incomplete-capture warning; dropped-entry count is shown. A hard Editor/process crash can lose unsent messages. Logs may contain sensitive project output: read pages still have no account authentication, so keep the service local until access control is implemented.

The package's **Connection Log Smoke Tests** sample contains exactly two passing transport placeholders, one EditMode and one PlayMode. They intentionally emit warning/error messages with `LogAssert.Expect`; these are not gameplay or ML-agent qualification tests.

Run-bridge verification (2026-09-27): 64 real HTTP/WebSocket/SQLite checks passed, including FIFO positions, duplicate clicks, readiness ownership, failure continuation, unavailable queued cases, reconnects, and idle polling removal. Four dashboard-triggered Unity smoke cases were verified against both SQLite and single-case NUnit XML, including a parameterized case, an intentional failure, and PlayMode domain reloads. Seven Unity tests remained in the dashboard at that checkpoint. Run history survived a server restart. Desktop and 390px mobile browser layouts were checked. See Obsiddy `Docs/Run Bridge Verification.md` for that evidence boundary. The log-capture checkpoint passed 107 checks; both Connection Log Smoke Tests were also verified in the live PlaytestOps Test Editor, and desktop/mobile full-log pages were inspected. See `Docs/Log Capture Verification.md` for that evidence and its limits. Connected-Editor filtering extends the current harness to 126 passing checks.

## Dark theme and C# highlighting (2026-10-05)

Dark is the default across Tests, Editors, Scripts, and run logs, including with JavaScript disabled. The shared **Dark / Light** toggle saves `playtestops.theme` in localStorage and applies it before styles load; Bootstrap and native controls follow the same theme. If storage is blocked, dark remains the startup default and the toggle still works for the current page. Focus indicators, statuses, log severities, fields, and source panels have matching dark/light colors.

Only `/Scripts` loads the highlighting stylesheet and enhancement. Locally pinned Prism **1.30.0** core, C-like, and explicit C# grammar run in a same-origin Worker; there is no language autodetection, CDN request, source upload, formatting, or editing. The existing source-reading protocol and SQLite schema are unchanged.

Highlighting replaces plain text only after inert parsing accepts text and token-class spans and verifies exact equality with the original DOM text. Carriage returns are encoded before parsing so existing CR/CRLF text survives enhancement. Bounds are 128 KiB input, 1 MiB expanded output, 10,000 DOM nodes, depth 64, four code panels, and a 2.5-second Worker deadline. Missing assets, unsupported/blocked Workers, timeouts, invalid markup, oversized output, or changed text leave the readable plain source intact. Prism normalizes non-breaking spaces (NBSP), so affected files intentionally stay plain. This is a rendered-DOM text guarantee, not a byte-round-trip/download guarantee; saved-file byte count and SHA-256 remain separate metadata.

Verification: web build completed with zero warnings/errors; 14 theme, 29 Worker, 25 isolated-browser, 104 source-browser, and 126 connection/run/log checks passed. Computed syntax-token contrast minima were 6.70:1 dark and 4.76:1 light. Populated-code checks used the isolated SourceVerification preview fixture, not imported scripts or live gameplay. See the [UI verification guide](../Tools/UiVerification/README.md) for reproduction.

The update was then applied to the live localhost dashboard with original-file backups, without changing the database or Unity package/project code. PlaytestOps Test was re-paired after restart and remained connected/idle. Twenty additional live browser checks passed across both themes: two active-project test rows, Editors connection, the accurate empty Scripts state, persisted historical debug/warning/error logs, local asset availability, preference persistence/navigation and 390px log-page overflow. Live log badge contrast minima were 8.05:1 dark and 6.48:1 light. No new Unity test execution, gameplay or ML-agent qualification was performed for this UI-only update.

### Responsive code wrapping (2026-10-05)

The code viewer now uses whitespace-preserving soft wrapping at the current panel width. Long identifiers/strings can break within the panel, and window resizing reflows visual lines automatically; no resize JavaScript, source formatting, or inserted line breaks are used. The existing maximum dashboard width still applies. Eighteen live browser checks passed using `Assets/Scripts/Services/Effects/BigHitEffect.cs`, now present in PlaytestOps Test: 1280/900/390px and back, no horizontal overflow, increased/decreased visual line height, exact source/token preservation, both themes, and a page-local long-token/indentation probe. CSS was updated without restarting the backend or Unity. See `../Tools/UiVerification` and the Obsiddy theme/highlighting verification note.
