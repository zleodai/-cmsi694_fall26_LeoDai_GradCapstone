PlaytestOps Repo

Author: Leo Dai
Class: CMSI 694
Semester: Fall 2026

## Connected Unity dashboard

Run `PlaytestOps.Web` and pair a Unity Editor from the dashboard's **Editors** page. The Unity package is in `UnityPackages/com.playtestops.editor` (Unity 6000.6+).

**Build settings** (`/BuildSettings`) reads and modifies the connected Editor's active Windows build configuration. It includes build/profile options, global and profile scene lists, profile defines, Windows/shared Player Settings, rendering/backend/stripping/compiler options, icons, splash assets and stack traces. Values and capability notes come from the installed Editor version; there is no fixed dashboard preset that silently substitutes for Unity settings.

Settings writes are localhost-only, antiforgery-protected, exact-Editor-session-bound and optimistic-revision-checked. Save one setting at a time. Compile/import/Play Mode/test/build activity blocks edits; script reloads may temporarily disconnect the Editor. An interrupted response is **not** proof that the edit failed: refresh before retrying. The page does not build, switch platform or activate another profile.

See [Windows build settings coverage, formats and safety](docs/windows-build-settings.md) for scope, adapters, exclusions and verification boundaries.
