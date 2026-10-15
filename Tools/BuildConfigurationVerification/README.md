# Windows build-configuration verification

Build the web application into a disposable output directory's `build` subfolder, then run this verifier:

```powershell
dotnet build PlaytestOps.Web/PlaytestOps.Web.csproj -o <temporary-directory>/build
dotnet run --project Tools/BuildConfigurationVerification/Checks.csproj -- <temporary-directory> <absolute-web-project-path>
```

The verifier starts only its own dashboard child process on port 5295 with a disposable SQLite database and drives a synthetic connected Unity Editor over the authenticated WebSocket bridge. It checks local-host and antiforgery rejection, one-project serialization and the four-project global broker limit, correlation/session ownership, stale-schema invalidation, typed value validation, bounded replies, output encoding, busy/failed/incomplete states, and the settings page's no-store response. Completion requires at least 40 assertions; an empty or truncated run cannot be reported as a pass. The verifier does not launch Unity or mutate the user's project. Fixture reports are synthetic and do not establish live Editor API compatibility, active-profile override behavior, a successful player build, or executable launch proof.
