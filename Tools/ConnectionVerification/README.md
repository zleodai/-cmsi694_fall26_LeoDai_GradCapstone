# Connection verification

Build the web app into a temporary directory's `build` subfolder, then run:

```powershell
dotnet run --project Tools/ConnectionVerification/Checks.csproj -- <temporary-directory> <absolute-web-project-path>
```

The verifier uses port 5291 and a new disposable SQLite database, starts/stops only its own server process, and checks pairing, antiforgery, authenticated WebSockets, catalog persistence/idempotency, unavailable tests, disconnect, and restart behavior. It does not modify the working database or run Unity tests. The expected result is 126 PASS checks. Queue checks cover FIFO positions, duplicate requests, readiness tokens, stale/reconnected sockets, failure continuation, unavailable tests, execution deadlines starting at dispatch, and stopping dashboard polling when drained. Structured-log checks cover severity/UTC/order/size/prefix validation, persistence, ownership, replay, legacy omissions, capture-limit metadata, escaped full-log pages, filters, history links and restart recovery.

Connected-Editor visibility checks cover the full dashboard and tests fragment, active sockets versus merely paired sessions, reconnecting without rediscovery, multiple connected projects, disconnected and unlinked catalog exclusion, distinct no-connection and empty-catalog states, queue polling through a socket gap, and preservation of saved tests, attempts, and direct historical log pages. Server restart preserves storage but hides tests until their Editor reconnects. Logs remain in the temporary directory.
