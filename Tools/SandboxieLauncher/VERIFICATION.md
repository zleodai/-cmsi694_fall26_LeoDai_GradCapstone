# LDAI-34 validation — 2026-11-01

The final solution build passed with zero warnings and errors. All 67 isolated launcher checks passed. The checks use fake processes and Steam identities, including four selected contexts, and do not themselves start Sandboxie, Steam or Unity.

Live Windows x64 verification used Sandboxie Classic 5.73.5, normal Steam plus the explicitly selected `DefaultBox`, and the existing Corporate Hell Windows build with App ID 480.

- Before implementation, a temporary application was launched in a selected empty sandbox. Native membership was observed, box termination was requested and verified, and existing processes in the other box remained present. The launcher itself never terminates whole boxes or deletes sandbox contents.
- Two complete game launch/stop cycles passed. In both cycles, short-lived native Steam probes initialized the build's Steam API, reported `BLoggedOn`, and returned two distinct Steam identities before either game was started.
- Each cycle produced two separate Corporate Hell processes, one unboxed and one in `DefaultBox`. Both were observed responsive; the first cycle also sampled increasing CPU time. Unity player logs reached scene/service initialization.
- A host-containing stop request without `--include-host` was rejected and left both games running. A second launch while games were already running was rejected before producing another receipt or game.
- Explicit receipt-based stop removed both owned games after each cycle. The original normal and sandboxed Steam processes remained running with the same PIDs and creation times.
- The 245 existing build files retained their names, lengths and modification timestamps. SHA-256 checks of the executable, `steam_appid.txt` and `steam_api64.dll` matched the pre-test values. No Corporate Hell source, scene, build or configuration files were edited.

The first native attempt exposed a missing legacy `SteamAPI_Init` export. Modern SDK compatibility was corrected to use exported `SteamAPI_InitFlat` with its proper integer result and 1024-byte error buffer, then the successful cycles above were run. No games were started by the failed attempt.

Private receipts and raw logs stay outside the repository. Receipts contain Steam IDs; no credentials were collected.

This qualifies local Steam identity readiness, game-process launch, conflict rejection and owned-game cleanup for two actual accounts. Four-account behavior has isolated fixture coverage only. Game-reported Steam identity, lobby joining, Steam socket traffic, gameplay replication, streaming/input integration and transport discovery were not qualified by this test.
