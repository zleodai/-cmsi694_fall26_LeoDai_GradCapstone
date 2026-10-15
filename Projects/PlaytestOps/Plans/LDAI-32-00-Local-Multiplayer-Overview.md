# LDAI-32 — Local Multiplayer Playtest Implementation Research

Date: 2026-10-15  
Status: Research and implementation proposal; no multiplayer runner, sandbox, VM, or Steam session was implemented or executed for this document.  
Target: Unity games using **Mirror Networking**, primarily on one Windows machine. Unity Netcode is not the proposed networking stack.  
Jira: LDAI-32 remains unchanged. This plan defines scope and decomposable implementation work; it does not claim delivery of that work.

## Decision in one minute

Build the first useful runner as **one immutable desktop artifact, launched as several independent native processes over Mirror KCP**. Start with a rendered host and one rendered client, then a server-only process and two clients. Add headless bots after the game proves that its logic does not depend on rendering. Keep an Editor-assisted lane for debugging.

Treat **Steam Sockets/P2P qualification as a separate lane**. It needs genuine, entitled Steam identities, actual Steam initialization, the intended transport path, and captured connection evidence. Two local KCP clients do not establish any of these. Sandboxes and VMs are conditional ways to attempt separate environments on one physical computer, not substitutes for separate accounts or a promise that the current Steam transport supports that topology.

The most useful sequence is **01 → shared orchestration/cleanup → 02 → 06**, with 03 available for debugging. Investigate 04 or 05 only when one-machine Steam qualification is valuable enough to justify the operational burden. Use 07 only if local coverage of the Valve sockets API itself is important enough to maintain an additional Mirror transport adapter. Add [08 — Hybrid and managed-cloud escalation](LDAI-32-08-Hybrid-and-Managed-Cloud.md) only after a measured local-capacity, remote-peer or hosted-server qualification need; it is optional and outside the local MVP.

## Solutions and tradeoffs

| Plan | Best use | Main advantages | Main drawbacks / evidence limit | Priority |
|---|---|---|---|---|
| [01 — Native multi-process KCP](LDAI-32-01-Native-Multiprocess-KCP.md) | Everyday local gameplay regression | Few moving parts; real process boundaries; same executable hash for all roles | Shared host resources and OS profile; no Steam identity/lobby/SDR proof | Default MVP |
| [02 — Headless server and bot clients](LDAI-32-02-Headless-Server-and-Bots.md) | Repeatable functional sweeps and modest scale | Low rendering cost; fast restart; automated actions | Headless compatibility work; cannot validate visual/input/overlay experience | After 01 |
| [03 — Editor-assisted Mirror testing](LDAI-32-03-Editor-Assisted-Mirror.md) | Debugging state, callbacks, and scene wiring | Inspector/debugger visibility; short iteration cycle | Editor behavior and source state differ from immutable player; clone identity/reload hazards | Developer adjunct |
| [04 — Sandboxed process isolation](LDAI-32-04-Sandboxed-Processes.md) | Save/registry collision diagnosis; conditional two-account experiment | Lighter than full VMs; isolated writable state | Shared kernel/network/GPU; compatibility and account-session uncertainty | Time-boxed spike |
| [05 — VM-isolated local clients](LDAI-32-05-VM-Isolated-Clients.md) | Separate OS/Steam environments on one physical host | Stronger OS separation; snapshots; realistic guest firewalls | RAM/disk/GPU/OS licensing costs; shared uplink; graphics support can block it | Conditional qualification |
| [06 — Steam Sockets end-to-end qualification](LDAI-32-06-Steam-Sockets-Qualification.md) | Authentic identities, lobbies, production transport | Tests the path actually intended for shipping | Accounts/entitlements/Steam availability; second device may be necessary | Required before Steam claims |
| [07 — Local Valve sockets API lane](LDAI-32-07-Local-Valve-Sockets-API.md) | Steam-like transport API semantics without Steam accounts | Multi-process IP testing; offline transport-focused checks | New adapter/native library work; open-source library is not Steam services | Optional, not MVP |
| [08 — Hybrid and managed-cloud escalation](LDAI-32-08-Hybrid-and-Managed-Cloud.md) | Managed server plus local clients, or a genuinely remote rendered peer | Offloads server work; real remote connectivity; optional regional coverage | Provider lifecycle/auth/cost integration; GPU/account constraints; hosted-server success is not Steam P2P/relay proof | Optional escalation, not MVP |

These are composable lanes, not eight systems that must all be built. A host plus a client and a dedicated server plus clients are different topologies; sandbox/VM is an execution environment; KCP/Steam Sockets is a transport choice. Persist these as separate fields rather than one ambiguous “multiplayer mode.”

## Current baseline and gaps

Read-only inspection of `C:\Users\Leo\Dev\PlaytestOpsCapstone` on this date found:

- `PlaytestOps.Web/Bridge/RunService.cs` queues catalog tests against a connected Editor session and validates monotonically sequenced run updates.
- `PlaytestOps.Web/Bridge/RunMonitor.cs` serializes active work per project, probes Editor readiness, and applies two-minute acknowledgement/disconnection and 30-minute dispatch deadlines. Its timeout explicitly **does not stop Unity**. Restart marks unfinished attempts Interrupted/Failed.
- `PlaytestOps.Web/Models/PlaytestRun.cs` stores one attempt/result and bounded log snapshots, not a session topology or child-process ledger.
- `UnityPackages/com.playtestops.editor/Editor/Testing/RunRecord.cs` describes an Editor test execution. There is no basis here to claim that a proposed built-player runner already exists.
- The vault's `Docs/Index.md` documents one paired Editor per project identity, localhost-only mutation endpoints, and a dashboard without general remote account authentication. Preserve these boundaries.

Therefore add a **typed built-player execution lane** without silently reusing Editor session tokens or changing existing test semantics. The initial worker can live beside the backend on the same PC; use an interface seam so it can later move to a local service or guest agent. Keep the current Editor lane working and add explicit capabilities: `editor-test`, `mirror-built-session`, `rendered`, `headless`, `steam-qualified`.

Relevant reference project: read-only inspection of Corporate Hell found Unity `6000.6.0f1`, FizzySteamworks `6.0.1`, and `Assets/Scenes/MainMenu.unity` with `UseNextGenSteamNetworking: 1`. Its installed `NextClient.cs` uses `ConnectP2P`; `NextServer.cs` selects Steam user or game-server networking APIs under `UNITY_SERVER`. This is static source/serialization evidence, not an observed connection. The existing `CH_NetworkManager.cs` also demonstrates why lobby-order identity assignment must be audited before reusing a game integration. No game project was changed.

## Full support scope

### Required local MVP

1. Register and verify one previously built, trusted desktop artifact; launch 2–4 participants without rebuilding between attempts.
2. Support host+client and server+two-client KCP topologies, deterministic fixture setup, ready barriers, role-specific scripted actions, and observable assertions.
3. Test join, authoritative spawn/ownership, replicated state, scene transition, late join when the game supports it, disconnect, invalid join, and bounded reconnect policy.
4. Attribute every observation to run, instance, logical participant, process, artifact, scenario, and transport.
5. Persist per-instance and aggregate outcomes; clean up owned processes on success, failure, cancellation, and worker death.
6. Preserve all failed attempts. A retry creates a new attempt linked to the original; it does not erase it.

### Second-stage support

Headless nonvisual clients, one rendered observer, transport-level fault simulation, resource profiling, concurrent sessions within an explicit capacity budget, Editor-assisted diagnostics, and Steam P2P qualification. A feature such as host migration, reconnection, or rollback is only tested if the game implements it; this project must not silently add it as a test-harness feature.

### Explicitly outside the MVP

Public-cloud deployment, paid infrastructure, automatic Steam account creation/login/2FA handling, purchases, DRM or anti-cheat bypass, production leaderboard/cloud-save mutation, arbitrary executable or shell launch from a browser, arbitrary source changes, Unity Netcode migration, large-scale load certification, and generic “human-quality” AI play. No physics determinism, WAN equivalence, or visual correctness is inferred from a green functional run.

## Shared implementation contract

The following is a **proposed design**, not an existing API. Every solution below inherits it.

### Artifact and execution manifests

Create immutable `BuildArtifact` metadata: artifact ID, file-tree SHA-256 manifest, source revision plus dirty-state fingerprint, Unity version, package lock hash, platform/architecture, scripting backend, client/server build variant, scenes, test-hook protocol, Mirror/transport versions, and build configuration. Sign or otherwise trust the manifest at ingestion; hashing an untrusted executable does not make it safe.

Separate `SessionSpec`: scenario/version/seed; topology; roles/count; transport; render mode; admission policy; deadlines; endpoint allocation; resource limits; and reset policy. Store the canonical spec and hash with the run. A process reports the artifact ID/protocol on handshake; the worker also verifies files. A mismatch rejects the session before assertions.

Build once means **once per required binary variant**, then reuse by hash. One desktop artifact can often run host/client/server roles through a game bootstrap. A Unity Dedicated Server build is a separate artifact and must be named as such; do not claim it is byte-identical to a graphical player. Keep immutable binaries separate from writable runtime state and diagnostics. If a library must write beside its executable, make a verified per-run copy and record that policy rather than allowing shared artifact mutation.

### Separate control and gameplay planes

Proposed control flow: dashboard authorizes a typed scenario → local worker owns lifecycle → each game process connects outbound to a loopback WebSocket control endpoint → Mirror carries gameplay traffic. The control endpoint is new and distinct from the existing Editor bridge. It transports setup/actions/events/results, not game frame replication. No per-frame REST loop is needed.

Authenticate each instance with a single-use run-scoped secret delivered through a protected pipe or ACL-restricted configuration file, not a URL, ordinary log, or command-line argument. Handshake binds `runId`, `instanceId`, role, artifact hash, and protocol version. Reject stale, duplicate, cross-run, and oversized messages; sequence events and cap queues. A loopback address is not authentication against other local processes. Guest/remote workers require separate authenticated TLS transport, explicit pairing and authorization before access is enabled.

### Identity and endpoints

- `runId`: unique attempt; `instanceId`: unique execution instance; `participantId`: scenario-level logical player; `connectionId`: transient Mirror connection; `netId`: transient spawned object. Never conflate them.
- In KCP tests, a server-side test authenticator binds admitted logical participants to actual connections using run-scoped credentials. Treat it as fixture identity, not Steam authentication.
- In Steam tests, bind the authenticated remote Steam identity to the connection, then to the logical participant. Never infer it from lobby list order, join order, nickname, or a client-supplied SteamID alone.
- Allocate a distinct UDP listen port per native KCP session, for example from configurable `30000–30999`. All clients of that session use the same server port; client-side ephemeral ports are not extra server listen ports. Reserve/retry safely and use an actual bind/readiness handshake because “port looked free” is racy.
- Keep the dashboard's current port, new control endpoint, discovery/metrics ports, and gameplay port separate. Disable network discovery for orchestrated loopback tests. If the transport binds all interfaces, apply explicit scoped firewall/bind policy; connecting to `127.0.0.1` alone does not prove the server is private.
- Steam virtual ports/SteamIDs are a different namespace from KCP UDP ports. A transport hardcoding virtual port zero limits concurrent sessions for that identity; reject unsupported concurrency instead of inventing a `--port` setting it ignores.

### Lifecycle, clocks, cancellation and cleanup

Proposed session states: `Queued → Preparing → Starting → AwaitingParticipants → Running → Stopping → Final`. Keep outcome (`Passed`, `AssertionFailed`, `InfrastructureFailed`, `TimedOut`, `Cancelled`, `Interrupted`) separate from lifecycle and preserve the primary failure if cleanup also fails.

Initial tunable budgets: 30 seconds for local process startup, 30 seconds for local admission, 120 seconds for a short scenario, 10 seconds graceful shutdown, then forced cleanup of owned processes only. Steam/VM setup can use a separately declared 120–180-second budget. Use monotonic deadlines; UTC is for correlation. Heartbeats do not extend the overall deadline. A ready barrier requires the expected authenticated roles, loaded scene, and fixture version, not a fixed sleep.

On Windows, plan a Job Object with kill-on-job-close and assign created native processes before allowing them to spawn uncontrolled children. Maintain an ownership ledger with PID, process creation time, executable hash/path, run ID, and job/guest identity; never terminate by executable name alone. Job behavior with Steam relaunches, sandbox services and guest processes needs dedicated handling. Microsoft's [Job Objects documentation](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) provides process-group lifetime and limit primitives; it does not make an arbitrary external launcher a child of the job automatically.

Cancellation means request game shutdown, stop admissions, collect bounded final output, enforce the grace deadline, terminate remaining owned instances, verify they exited, release endpoints, then finalize. Worker restart must reconcile stale owned runs without killing a newly reused PID or the user's Editor/Steam client. Failure to prove cleanup makes the worker unavailable for another session until reconciled.

### Writable state, evidence and security

Use `runs/<runId>/<instanceId>/` for log, result, dump and game-save roots. A game-level storage abstraction must route test saves/settings there; changing the working directory or `-logFile` does not redirect Unity PlayerPrefs, Steam Cloud, or every plugin's caches. Never use production save slots or achievements for automated fixtures. Reset only exact owned paths, validate final resolved paths, and retain failed-run evidence before cleanup.

Collect structured events with role, tick, local monotonic time, UTC, scene, authority, action sequence and assertion. Keep raw Player logs separately from a bounded searchable summary; the existing 1,000-entry/192,000-character snapshot limits are not a suitable unlimited multi-process log channel. Proposed initial retention: 20 MiB per instance plus 5 MiB structured events, explicit truncation counters, latest ten successful attempts and all failures for a configurable period. These are design defaults, not measured requirements. Redact tickets, pairing secrets, credentials, private paths and unnecessary Steam identifiers before sharing.

Rendered runs need background execution and explicit low-cost window/quality settings. Actions should enter the game's normal command/input abstraction, not teleport objects to simulate success. Record every fixture/authority override. For physics, use tolerances and event invariants; the same seed does not guarantee identical cross-process simulation.

## Resource admission and cost model

Measure one host/server, one rendered client and one headless client before choosing capacity. Reserve host OS, dashboard, active Editor and 20–25% safety headroom as an initial policy. Admit only if measured peak working sets, CPU time, GPU frame time/VRAM and disk/log limits fit. Start at one session; increase participants and then session concurrency separately.

`required_RAM = baseline_host_RAM + server_peak + Σ(client_peak) + VM_OS_overhead + safety_margin`.

Use the analogous measured CPU/GPU constraints; logical core count is not a promise of one performant client per core. If the host saturates, classify the run as resource-contended and retain gameplay observations, but do not report network timing as production-quality evidence. VM snapshots, Steam installs, rendered captures and build variants often dominate disk usage. No hardware or cloud purchase is recommended until an actual profile identifies the bottleneck. Existing hardware avoids a new hosting invoice, not electricity, licensing, disk or maintenance costs.

## Proposed work packages and definitions of done

| Order | Work package | Definition of done / acceptance |
|---|---|---|
| 0 | Game capability inventory | Actual scenes/prefabs, Mirror/transport versions, authority policy, reset hooks and supported topologies documented; Steam-coupled code identified; unsupported features listed |
| 1 | Artifact registry | Tree hash and provenance verified; tampering/missing files/wrong platform reject; immutable files unchanged after repeated runs |
| 2 | Local process supervisor | Two participants start with unique logs/state; startup/crash/cancel/restart tests prove bounded cleanup without harming a sentinel unrelated process |
| 3 | Game bootstrap and control protocol | Role/transport chosen before networking begins; forged/cross-run credentials rejected; readiness reflects actual scene/admission; production path unaffected |
| 4 | Scenario/observation layer | One join–action–replication–disconnect test passes across independent processes; deliberately broken replication and a missing participant both fail |
| 5 | Persistence/dashboard | Parent and child outcomes, hashes, roles, evidence and cleanup status visible; old Editor tests/history regressions pass; no remote control exposure |
| 6 | Fault and resource qualification | Crash, hang, bad port, stale result, disconnect, delayed join, cancellation, log overflow and saturation cases covered; profile limits recorded |
| 7 | Headless optimization | Same scenario compared against rendered reference; unsupported visual features excluded; memory/CPU benefit measured |
| 8 | Steam qualification | Actual accounts/entitlements and transport path recorded; lobby and gameplay connection tested separately; relay status reported only when evidenced |

Suggested provisional MVP acceptance: ten clean sequential attempts for each selected topology, zero orphan owned processes/port leases/state collisions, and successful detection of injected faults. Ten passes are a regression gate, not statistical reliability certification. Always report attempted/passed/failed/interrupted counts; zero executed tests cannot pass.

## Proof labels

| Label | What is established | What is not established |
|---|---|---|
| Static research | Sources and architecture inspected | Compilation, runtime success or compatibility |
| Harness fixture | Protocol/lifecycle behavior against fake peers | Unity gameplay |
| Native local KCP | Independent built processes and Mirror gameplay over loopback | Steam, WAN, independent hardware |
| Headless functional | Declared nonvisual behaviors | Rendering, overlay, controller/focus UX |
| Editor-assisted | Inspected Editor scenario | Exact shipping artifact behavior |
| Steam authenticated | Real admitted identities and recorded Steam path | Relay or WAN coverage unless separately captured |
| Relay/WAN qualified | Recorded route/environment and tested conditions | All NATs, regions, networks or production scale |

## Source policy and related notes

Primary documentation was checked on 2026-10-15. Exact source links and method-specific constraints are included in each solution. Vendor documentation establishes available mechanisms; plan architecture, defaults and acceptance criteria are recommendations, not vendor guarantees. Re-pin package versions and recheck compatibility before implementation.

Related local notes: [[PlaytestOpsDashboardExpansionSuggestions]], [[PlaytestOps Endpoints]], [[Unity Package Endpoints]], [[Run Bridge Verification]]. Preserve those notes and their historical proof boundaries. This research did not update Jira or operate any live gameplay environment.
