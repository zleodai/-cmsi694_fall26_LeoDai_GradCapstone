# PlaytestOps baseplate

One ASP.NET Core Razor Pages app, a query service, EF Core, and a persistent SQLite database. The dashboard uses a locally bundled htmx 2.0.8 script for manual refresh. It has no polling or Unity connection.

## Run locally

Requires the .NET 10 SDK. From this project directory:

```powershell
dotnet restore
dotnet tool restore
dotnet run --launch-profile http
```

Open http://localhost:5282. The HTTPS profile is also available (`dotnet run --launch-profile https`).

Development startup applies pending migrations. The SQLite path defaults to `App_Data/playtestops.db`, resolved relative to the project content root rather than the shell's working directory. Override it with `ConnectionStrings__DefaultConnection`, e.g. `Data Source=C:/temp/playtestops-check.db` for isolated checks. Database files are ignored by Git.

The initial SQLite schema includes the existing Identity tables. A separate seed migration inserts exactly four demo tests, one per status, only when that migration first applies. Normal startup does not re-seed, reset edited records, or recreate deleted records. Emptying the test table produces the empty state. The stock SQL Server template migration was replaced with a SQLite baseline; this does not migrate any existing LocalDB accounts/data.

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

Use migrations, not `EnsureCreated`. Non-Development deployments must apply migrations explicitly before starting the app. Registration/login UI, Identity page routes, and authentication services are removed for now. The read-only demo dashboard is public. Existing Identity database tables and migration history are retained to avoid deleting stored data; they do not enable authentication. Remote access/authentication policy is a separate implementation step; the local development certificate is not automatically trusted by phones.

## Acceptance checks

Use a disposable database via the connection override, not your working data:

1. Start in Development; confirm four demo rows and all four status labels.
2. Stop and restart with the same database; confirm the same IDs and row count.
3. Edit a row directly in the disposable database; Refresh must show the saved edit without a full navigation. Restart must preserve the edit.
4. Delete the demo rows in the disposable database; confirm the empty state, including after restart.
5. Temporarily make the disposable test table unavailable; confirm the readable error on initial load and Refresh, then restore it and refresh successfully.
6. Check desktop and narrow mobile layouts, repeated Refresh, and no automatic requests while idle.

Unity execution and automatic status updates are intentionally outside this baseplate.

## Verification performed (2026-09-25)

- Build: zero warnings/errors; EF reports no pending model changes.
- Thirteen real HTTP/SQLite checks passed using a disposable database, including seed count/statuses, Identity schema, restart persistence, escaped refreshed content, edited/deleted data preservation, invalid-status rejection, readable 503 responses, recovery, and empty state.
- Edge browser: changed SQLite data appears through htmx without navigation; readable database and offline errors; recovery and empty state; no refresh requests during a three-second idle observation.
- Desktop and 390px mobile viewport visually inspected; mobile horizontal overflow checked. This is browser viewport verification, not a physical-phone or remote-network test.
- Unity execution, cloud hosting, and idle resource budgets were not tested in this baseplate.
