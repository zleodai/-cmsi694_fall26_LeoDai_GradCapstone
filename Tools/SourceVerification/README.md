# Source-browser verification

Build the web app into a temporary directory's `build` subfolder, then build and run this tool:

```powershell
dotnet build PlaytestOps.Web/PlaytestOps.Web.csproj -o <temporary-directory>/build
dotnet run --project Tools/SourceVerification/Checks.csproj -- <temporary-directory> <absolute-web-project-path>
```

Port 5292 must be free. The verifier compiles the same pure `SourceReader.cs` used by Unity, creates a separate fixture directory/database, and starts/stops only its own web server. It does not touch the working database, import scripts into Unity, or run Unity tests. Windows uses a temporary junction when symlink creation lacks privilege. Fixture directories are left as inspectable evidence.

104 checks passed on 2026-10-05. Checks cover declared roots and exclusion policy, traversal/links, UTF-8/NUL/byte limits/hash/UTC, list truncation, local-only HTML browsing, active versus paired-only sessions, escaped source, search, empty/error states, request correlation, foreign/stale sockets, wrong reply kinds, concurrency limits, timeout cleanup, duplicate metadata and BOM/Unicode content. Run the separate ConnectionVerification harness for existing 126 run/catalog/log regression checks.

Add `--preview` for a clearly labeled synthetic Editor source preview at the printed URL. This does not connect a real Unity Editor. Stop the tool when finished to release its fixture server and port. Do not rebuild the web output while the preview/server is running on Windows.
