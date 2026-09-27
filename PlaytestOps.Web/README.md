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

Dashboard reads still require manual Refresh. No login/register pages were added. Generating pairing codes requires loopback; non-local Unity connections require HTTPS. Paired credentials are in server memory and expire after 12 hours, so server restart requires a new code. Selected dashboard run commands and their results now use the same socket; local-only Unity runs remain local.

Endpoint documentation: `C:\Users\Leo\Documents\files\Obsiddy\Projects\PlaytestOps\Docs\Index.md`.
Acceptance harness: `../Tools/ConnectionVerification`. Its 21 real HTTP/WebSocket/SQLite checks passed. Live Unity pairing, six-case sync, explicit disconnect, and script-reload reconnection were also verified in 1HourRoguelike.

## Dashboard run bridge (2026-09-27)

`POST /?handler=Run` is an antiforgery-protected localhost form. RunService saves a Pending PlaytestRun, sends `run.request` for one exact case, and commits `run.update` callbacks. Razor displays Requested/Running/Passed/Failed and the latest five attempts with duration, output, and errors. All attempts persist in SQLite through AddPlaytestRuns; discovery never resets them. Refresh remains manual.

Each project has one active request. Ownership and increasing revisions reject foreign/out-of-order updates; completed results are immutable. Unity uses a SessionState outbox across domain reloads and replays result updates, never execution requests. RunMonitor only queries during active runs: two-minute acknowledgement/offline grace, 30-minute deadline. Server restart closes unfinished runs as Interrupted/Failed. These conditions do not stop Unity; inspect the Editor before retrying.

Cancellation, authenticated remote execution, historical report export, and uploading runs started by Unity's local Run button are outside this implementation.

Current verification: 43 real HTTP/WebSocket/SQLite checks pass. Four dashboard-triggered Unity smoke cases were verified against both SQLite and single-case NUnit XML, including a parameterized case, an intentional failure, and PlayMode domain reloads. Seven current Unity tests remain in the dashboard. Run history survived a server restart. Desktop and 390px mobile browser layouts were checked. See Obsiddy `Docs/Run Bridge Verification.md` for the evidence boundary.
