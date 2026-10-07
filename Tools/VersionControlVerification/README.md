# Read-only UVCS verification

Build the web app into a disposable output directory's `build` subfolder, then run:

```powershell
dotnet run --project Tools/VersionControlVerification/Checks.csproj -- <output-directory> <absolute-web-project-path>
```

The verifier links the same pure reader/DTOs/process runner shipped in the Unity package. Fake command outputs test its read-only behavior without executing `cm` or changing a real workspace. HTTP/WebSocket checks start only an owned child server on port 5294 with a new disposable SQLite database; they never touch the user's live database. The server is stopped on completion/failure, and `vcs-check-server.log` is retained in the output directory.

`--pure-only` skips HTTP startup. `--http-only` skips pure-reader checks for focused integration debugging; neither flag changes the default full verification run. `--preview` runs the selected checks and then leaves a fake connected Editor serving deterministic snapshots for browser inspection; interrupt it to stop its owned child server. The preview also stops normally after ten minutes without a request. Preview content is synthetic and does not represent a live UVCS workspace. A failed rendering assertion saves only synthetic fixture HTML for diagnosis.

Coverage includes workspace identity, pending/moved/meta/Unicode paths, incoming/history scope, zero loaded revision, unavailable versus successful empty states, encoded display data, pagination, request bounds, response validation, correlation/ownership, one pending snapshot per project, timeouts, cancellation, socket replacement, disconnect/no-cache, and no idle polling or mutation forms. The global four-project broker limit was not separately stress-tested by this harness. A fake-CLI pass does not establish real `cm` compatibility or live server ancestry behavior.

## Verified evidence — 2026-10-07

- The final staged build passed **55 pure-reader checks** using `--pure-only` and **110 isolated HTTP/WebSocket checks** using `--http-only`. These were separate runs totaling 165 component checks; a combined default-mode run was not claimed after the final diagnostic changes. Web and verifier builds had zero warnings/errors.
- Existing source-browser **104 checks** and connection/run/log **126 checks** passed independently. They are not part of the 165 UVCS component count.
- Browser verification passed 15 synthetic checks for positive incoming changes, pagination, escaped metadata, both themes, and a narrow viewport. The fixture is synthetic, including its changeset numbers and comments; it is not evidence of a live workspace being behind its branch.
- Live 1HourRoguelike verification passed 12 browser checks across dark/light and mobile views. Package 0.3.0 returned 1,334 pending entries, eight history changesets, and loaded/head changeset 7 with no incoming changesets. A domain reload during a snapshot cleared the pending payload and displayed zero rows; a fresh branch-scoped snapshot recovered the same counts. Loaded revision 7 remained unchanged.

No check-in, update, pull, merge, switch, undo, or other live VCS mutation was performed. The feature-branch service used port 5283 and its own database; the existing service on 5282 was left untouched. The fake-CLI tests cover injected output and command selection, not execution of an alternate native client or every UVCS server/client version.
