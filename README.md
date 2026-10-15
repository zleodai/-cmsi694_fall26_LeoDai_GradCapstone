PlaytestOps Repo

Author: Leo Dai
Class: CMSI 694
Semester: Fall 2026

## Connected Unity dashboard

Run `PlaytestOps.Web` and pair a Unity Editor from the dashboard's **Editors** page. The Unity package is in `UnityPackages/com.playtestops.editor` (Unity 6000.6+).

**Build settings** (`/BuildSettings`) reads and modifies the connected Editor's active Windows build configuration. It includes build/profile options, global and profile scene lists, profile defines, Windows/shared Player Settings, rendering/backend/stripping/compiler options, icons, splash assets and stack traces. Values and capability notes come from the installed Editor version; there is no fixed dashboard preset that silently substitutes for Unity settings.

Settings writes are localhost-only, antiforgery-protected, exact-Editor-session-bound and optimistic-revision-checked. Save one setting at a time. Compile/import/Play Mode/test/build activity blocks edits; script reloads may temporarily disconnect the Editor. An interrupted response is **not** proof that the edit failed: refresh before retrying. The page does not build, switch platform or activate another profile.

See [Windows build settings coverage, formats and safety](docs/windows-build-settings.md) for scope, adapters, exclusions and verification boundaries.

**Builds** (`/Builds`, Editor package 0.5.0+) starts local Windows player builds with a fresh server-issued ID and a new `Builds/PlaytestOps/<id>/Player.exe` output. Build history and all captured debug/warning/error entries are persisted in SQLite and remain readable after disconnect/restart. A worker streams logs during Unity's synchronous build call. Each result retains an artifact manifest and executable SHA-256 on the Editor host; it does not launch the player or deploy remotely.

See [Windows build execution and recovery](docs/windows-build-execution.md) for profile fidelity, output ownership, bounds, unknown-result recovery and proof boundaries. Apply EF migrations before production startup; Development startup migrates automatically.
