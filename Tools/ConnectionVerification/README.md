# Connection verification

Build the web app into a temporary directory's `build` subfolder, then run:

```powershell
dotnet run --project Tools/ConnectionVerification/Checks.csproj -- <temporary-directory> <absolute-web-project-path>
```

The verifier uses port 5291 and a new disposable SQLite database, starts/stops only its own server process, and checks pairing, antiforgery, authenticated WebSockets, catalog persistence/idempotency, unavailable tests, disconnect, and restart behavior. It does not modify the working database or run Unity tests. The expected result is 64 PASS checks. Queue checks cover FIFO positions, duplicate requests, readiness tokens, stale/reconnected sockets, failure continuation, unavailable tests, execution deadlines starting at dispatch, and stopping dashboard polling when drained. Logs remain in the temporary directory.
