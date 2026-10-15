# LDAI-32 Solution 03 — Editor-assisted Mirror Testing

Date: 2026-10-15  
Status: Proposed developer/debugging lane; not an immutable-build acceptance replacement.  
Parent: [Overview and shared contract](LDAI-32-00-Local-Multiplayer-Overview.md).

## Decision

Use one Unity Editor plus built players when inspecting state and breakpoints is valuable. Consider Unity Multiplayer Play Mode (MPPM) or ParrelSync as accelerators only after a compatibility spike with the actual Mirror project. Keep the authoritative acceptance run on built processes.

Do not confuse **Multiplayer Play Mode** with **Netcode for GameObjects**. Older Unity MPPM compatibility documentation explicitly allows any netcode solution. That does not prove the current Mirror project's integration. Current MPPM 3.0 documentation requires Unity 6.3 LTS or later on Windows/Mac, describes up to four Editor players and four locally built instances, and notes that much functionality moved into engine modules starting with 2.0. Pin the actual Editor/package pair and validate its custom role setup. [Earlier compatibility statement](https://docs-multiplayer.unity3d.com/mppm/current/sys-req/), [MPPM 3.0 requirements and limits](https://docs.unity3d.com/Packages/com.unity.multiplayer.playmode@3.0/manual/index.html).

## Choose one sub-option

| Sub-option | Strength | Limitation | Recommendation |
|---|---|---|---|
| One Editor + immutable player clients | Minimal new tooling; direct Inspector/debugger | Editor source can differ from client artifact | First debug lane |
| MPPM with explicit Mirror bootstrap | Coordinated local players in Unity tooling | Version-dependent engine APIs; extra instances are limited Editors; Mirror role/identity setup still needed | Time-boxed spike |
| ParrelSync cloned Editors | Full clone Editor debugging; no player rebuild for iteration | Shared authored files/settings; independent imports cost disk/RAM; not build evidence | Fallback for established workflows |

ParrelSync links `Assets`, `Packages` and `ProjectSettings` back to the original project while retaining independent generated folders such as `Library`. Thus a clone is not a safe place for uncontrolled project settings edits. [ParrelSync maintainer README](https://github.com/VeriorPies/ParrelSync).

## Integration with current PlaytestOps

Current PlaytestOps pairing supports one live Editor per project ID. A second Editor pairing with the same project ID can replace the first. In the inspected design, `ProjectSettings/PlaytestOps.json` stores that ID. Do not fix clone pairing by blindly writing a new ID into a shared `ProjectSettings` folder: it could change the original's identity too.

Proposed approach: one main Editor remains the PlaytestOps discovery/coordination connection; other Editor players are child session participants through a new runtime control endpoint, not separately paired catalog owners. Add a stable source-project identity and a separate ephemeral execution-instance identity. If separate full Editor project copies truly need separate catalogs, require independently owned settings and document that choice.

Editor transport selection must still happen before Mirror starts. Use KCP for local debug by default, unique role/instance configuration and the overview's run-scoped admission. An Editor is not a separate Steam identity. Neither ParrelSync nor MPPM creates accounts or makes same-account Steam P2P valid.

## Implementation phases and DoD

| Phase | Work | Definition of done |
|---|---|---|
| A — Baseline | One manually started Editor host plus one immutable player client | Mirror scene/authority behavior observed; Editor source fingerprint and player artifact hash both recorded |
| B — Lifecycle | Runtime participant bridge, play-mode/reload awareness | Enter/exit Play Mode, compilation, domain reload and application quit each produce a bounded terminal state |
| C — Identity | Separate source project, Editor instance and child role | Two instances cannot replace the main pairing or mix logs; shared settings unchanged |
| D — Tool spike | Test MPPM first if supported; otherwise evaluate ParrelSync | Actual project joins, transitions scene, exits and reruns; API support documented; no assumption based only on package installation |
| E — Debug handoff | Capture fixture/action script from failing built run | Developer can reproduce it in Editor with an explicit source/build mismatch warning |
| F — Acceptance separation | UI labels and result model | Editor-assisted success cannot fulfill a built-artifact or Steam-qualified gate |

## Operational details

An attached user-owned Editor is not a child process the supervisor may kill. Cancellation should request exit from the scenario/Play Mode and await confirmation; if the Editor is paused at a breakpoint, classify the run as paused/debugging or time out according to a separate interactive profile. Never terminate every `Unity.exe` to recover.

Keep a distinct unattended profile with deadlines that do not tolerate debugger pauses. Record Editor compilation/import state, domain/scene reload options and test fixture revision. The existing package temporarily enables domain reload for its PlayMode tests; new multiplayer orchestration must coordinate with that lifecycle instead of starting a second overlapping run.

The main Editor and child players need separate logs, save roots and assigned KCP endpoint. Keep only the relevant windows rendered; do not automate clicks into unfocused clones when a typed action API is available. Inspect serialized scenes and prefabs when behavior differs; do not assume a class default is the scene value.

## Validation and resource budget

Start with one Editor and one player. Measure the Editor/import cost separately from player runtime. Before allowing extra Editors, account for independent Library caches, shader/import work, CPU and memory. Avoid tests while assets are importing unless the scenario explicitly verifies that disruption.

Acceptance must include repeated Play Mode entry, one script reload, scene transition, main-Editor disconnect, stale child event rejection and cleanup after a failed admission. Verify that original assets/settings are unchanged after clone management. Test normal desktop player behavior separately with the same intended scenario.

Pros: excellent observability and quick iteration. Cons: mutable source, Editor-only branches, domain reloads, tooling version sensitivity and a less production-like lifecycle. Stop the tool spike if a clean Mirror project cannot run repeatably within one focused engineering day; retain the simpler Editor+player route. This is a proposed time box, not an implementation estimate.

Proof boundary: no MPPM installation, clone, Editor run or debugger test was performed for this document. Compatibility statements above describe documentation, not a verified local integration.
