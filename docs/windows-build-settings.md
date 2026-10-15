# Windows build settings

LDAI-27 exposes the active Windows platform/profile through `/BuildSettings`. Read and write operations go through the paired Editor WebSocket; the server never edits Unity YAML or invokes arbitrary Editor code.

## Coverage manifest

The live snapshot is the version-specific manifest. Each field has a stable ID, category, type, current value, enum choices, editability and scope/format note. The page shows total/editable counts. Its capability report identifies unavailable APIs, unsupported value types, excluded profile fields, and exact excluded non-Windows and obsolete Player property names. Missing capability is not presented as a default value.

| Surface | Discovery/binding | IDs and scope |
| --- | --- | --- |
| Player Settings | Every non-obsolete, supported public static property declared by `PlayerSettings`, excluding the named non-Windows/credential list reported in the snapshot | `Player.*`; effective active Player Settings object |
| Splash screen | Every supported non-obsolete public static property declared by `PlayerSettings.SplashScreen` | `Splash.*`; includes logo duration/sprite sequences and asset references |
| Standalone properties | Supported public `PlayerSettings.Standalone` properties, when the installed version exposes that class | `Standalone.*`; desktop-family settings |
| Target APIs | Fixed Get/Set pairs listed in `WindowsBuildConfiguration.AddPlayerMethods` | `Target.*`; Windows target / Standalone or Server named target, explicitly noted per field |
| Build options | Fixed `EditorUserBuildSettings` property names in `BuildProperties`, plus compression API when available | `Build.*`; Editor-local/shared option APIs, distinct from raw profile platform fields |
| Active profile options | Scalar fields beneath `m_PlatformBuildProfile`, restricted to `ProfileLeaves` | `ProfilePlatform.*`; development, profiler/debugging/checks, compression, architecture, solution/PDB, diagnostics/adaptive performance |
| Scenes/defines | Public scene/profile APIs; `globalScenes` explicitly preferred over effective `scenes` | `Scenes.Global`, `Profile.scenes`, `Profile.overrideGlobalScenes`, `Profile.scriptingDefines`; capability note states which scene list is effective and that profile defines are additive |
| Icons | Public default and Standalone icon APIs, with required slot counts | `Icons.Default`, `Icons.Windows`; ordered texture asset slots, empty list clears override |
| Logging | Each Unity `LogType` through stack trace Get/Set APIs | `Logging.*`; shared Player Settings |
| Active Input Handling | Fixed `activeInputHandler` property through a guarded Player Settings serialized adapter | `Player.activeInputHandler`; 0 legacy, 1 new package, 2 both |
| Advanced Windows inspector fields | Fixed `unsupportedMSAAFallback`, `keepLoadedShadersAlive`, `VertexChannelCompressionMask` and `useDeterministicCompilation` serialized paths on the effective Player Settings object | `Player.*`; typed flags, MSAA enum choice, vertex-channel bitmask and deterministic script compilation |

Target API coverage includes graphics API order/automatic selection, color gamuts, batching, lightmap/HDR encoding and streaming, Load/Store debug mode, scripting backend/API level/identifier/architecture, defines/compiler and IL2CPP arguments/options, stripping, preloaded assets, shader chunks/precision, normal-map encoding, startup log capture and virtual texturing. Internal-only pairs are explicitly named, version-guarded adapters; absent methods are reported, not guessed.

Supported values are booleans, signed/unsigned integer scalars, finite floating-point values, strings, enums, GUID metadata, vectors/colors, project asset references, scenes, splash logos and arrays of these supported values. Read-only properties remain visible but cannot be posted for mutation.

This is broad Windows Build/Player Settings coverage, **not a claim to control every setting contributed by every Unity package or future version**. Unsupported or unknown settings must appear in capability notes before extending the adapter. The page does not create/delete/activate profiles, switch platforms, install SDKs/toolchains, edit Graphics/Quality/render-pipeline asset inspectors or package custom components, configure cloud builds, reveal deployment credentials, preview the splash screen, or start builds. UWP is a separate target and is not Windows Standalone.

## Formats

- Scalar values use invariant culture; enum selects send named values (serialized profile enum selects send documented indices).
- Lists use exactly `{"items":["value","value"]}`. Missing keys, non-string items, extra members, trailing data, invalid escapes, oversized lists and malformed JSON are rejected, not converted into empty lists.
- Asset references are `Assets/path.ext` or `Packages/name/path.ext`, optionally followed by `#localFileId` for a specific sub-asset. Empty means `None`. Unity validates the expected object type. Absolute paths, traversal and network/URL paths are rejected; no files are uploaded.
- Scene lists use the list format with entries such as `+Assets/Scenes/Main.unity` (enabled) or `-Assets/Scenes/Other.unity` (disabled). Array order is build order. Duplicate/missing scenes are rejected. Saving this list does not save unsaved scene contents.
- Vectors and colors use the list format with numeric strings in x/y[/z/w] or r/g/b/a order.
- A splash logo list contains strings that themselves encode `{"items":["2","Assets/logo.png#localFileId"]}`: duration in seconds (2–10), then sprite. Empty sprite represents a delay and `@unity-logo` selects Unity's own logo.
- Icons use all required ordered size slots, or an empty list to remove the override. Individual empty slots inherit Unity defaults.

## Scope and mutation safeguards

Player Settings APIs can route to a profile override or global fallback. Before enabling edits, the adapter checks that Unity's active Player Settings API object is exactly the active profile's `PlayerSettings` component/fallback. A mismatch disables all writes. Profile identity, current values and editability contribute to the SHA-256 revision, so another profile with otherwise identical values is not interchangeable. The active profile's Player Settings serialization hook is invoked when needed before saving its asset.

Edits change only one fresh, locally enumerated binding. The Editor checks the revision again immediately before mutation. The server's schema is bound to the same session/socket, expires after five minutes, and is invalidated when refreshing. The server rejects stale forms, unknown/read-only IDs and malformed primitive/enum/JSON/asset values before dispatch. Unity performs stricter value/schema/type checks. Windows-only target choices, scene existence/order, supported graphics APIs, required sRGB gamut, icon slot counts, compression inherit sentinel and selected numeric ranges are validated.

All reads/writes require a loopback remote IP and loopback Host header; Razor POSTs require antiforgery tokens. Responses are no-store. Settings and paths are HTML-escaped, never inserted as executable markup. The server keeps at most four short-lived schemas and four in-flight requests, with one request per project. It does not persist settings in SQLite. Each reply must match the original Editor session, socket and request ID; foreign/late replies are consumed and discarded. Busy compilation/import, Play Mode, tests and builds reject changes.

The mutation reply is sent while assembly reload is locked, then the lock is released. There is no automatic edit replay after reconnect. Disconnect/timeouts can leave an applied-but-unacknowledged edit: the UI explicitly instructs refreshing rather than assuming failure. Setter/save failures attempt to restore the original single value; Unity side effects such as scheduled compilation cannot always be rolled back, and the UI does not promise otherwise.

## Verification boundary

The isolated verifier under `Tools/BuildConfigurationVerification` exercises HTTP/WebSocket/schema/security behavior with a simulated Editor. It cannot prove Unity's native setters, domain reload, asset serialization or version-specific backing fields. Live acceptance must independently check effective global/profile separation, representative reversible typed edits and persistence, malformed-value no-mutation, busy and stale handling, reload/reconnect behavior, and the page in a real browser. Build execution is a separate LDAI-28 workflow.

Primary references: [Windows Player Settings](https://docs.unity3d.com/6000.6/Documentation/Manual/playersettings-windows.html), [PlayerSettings API](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/PlayerSettings.html), [BuildProfile API](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Build.Profile.BuildProfile.html), and the installed Unity XML/API metadata. The adapter follows the installed version rather than assuming documentation for another minor version is identical.

## Acceptance evidence — 2026-10-15

- Dashboard build: zero warnings and zero errors. The isolated HTTP/WebSocket verifier passed **176 checks** on port 5295 using a disposable database and simulated Editors.
- A separate live Unity **6000.6.0f1** example with companion **0.4.0** exposed **155 fields, 153 editable**. It deliberately used distinct global/profile versions, global/profile scene lists, and an active Player Settings override.
- **58 live browser checks** passed against the final source: reversible scalar, integer, boolean, PDB, compression/default, MSAA, shader-retention and vertex-mask edits; profile scene ordering; invalid asset-type rejection; script-define change and empty-string restoration across compilation/reconnect; search; dark/light narrow-screen layout; and no idle polling. Desktop and 390-pixel screenshots were inspected separately.
- Eight additional direct live assertions passed for malformed/extra-member/non-string/escaped-NUL JSON, stale-edit rejection and restoration, and profile-routing mismatch/restore. A separate Play Mode check confirmed reads returned busy/no fields and edits did not mutate the value. These are not Unity Test Framework test results.
- The global `ProjectSettings.asset` SHA-256 remained `0D268D76ECF5B1EC1215D6016B78C079CA62537F6A7FE3E681EF1A6BA29A2B39` throughout profile edits; the profile YAML persisted the intended values. The user's heavily modified 1HourRoguelike checkout was not changed.
- Existing isolated regression suites passed: connection/run/log **126**, source browsing **104**, and combined UVCS **165** checks. These counts are separate from the 176 settings-protocol and 58 live-browser checks.

The mutating browser script is `Tools/UiVerification/build-settings-live-checks.js`. It is deliberately fixture-specific (port 5284, project display name PlaytestOps Build Probe, profile PlaytestOps Windows Probe, GlobalProbe/ProfileProbe scenes). Do not run it against a real game: it changes and restores representative settings and stops at the observed state on failure. The example's global scene list enables GlobalProbe and disables ProfileProbe; its profile overrides scenes with ProfileProbe, uses a separate Player Settings override/version, and has empty target defines initially.

Build execution and player-launch proof belong to LDAI-28, not this settings acceptance checkpoint. No gameplay, multiplayer, Steam or ML-agent qualification is claimed here.
