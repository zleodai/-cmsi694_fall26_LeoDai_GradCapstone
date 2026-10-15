# LDAI-32 Solution 02 — Headless Server and Bot Clients

Date: 2026-10-15  
Status: Proposed optimization after the native KCP baseline; not implemented or benchmarked.  
Parent: [Overview and shared contract](LDAI-32-00-Local-Multiplayer-Overview.md).  
Prerequisite: [Native multi-process runner](LDAI-32-01-Native-Multiprocess-KCP.md).

## Decision

Use a server process plus independent client processes whose actions come from a scripted game API. Render no client, or keep one rendered observer when visual evidence matters. This preserves networked client behavior while avoiding the GPU cost of rendering every view. It is suitable for repeatable join/action/replication/disconnect scenarios and a measured increase in participant count.

Do not confuse these three profiles:

| Profile | Artifact | Useful evidence | Limit |
|---|---|---|---|
| Desktop player in headless mode | Same desktop artifact as rendered tests, if compatible | Shared desktop game code without graphics initialization | Not the specialized server build |
| Unity Dedicated Server build | Separate server artifact, reused by hash | Server-target behavior and optimization | Different compile/build configuration; cannot act as a normal graphical client |
| Rendered observer + headless clients | Desktop client artifact plus chosen server artifact | Functional network behavior plus one visible perspective | One perspective does not prove every client's rendering/input |

Unity distinguishes desktop `-batchmode -nographics` execution from the optimized Dedicated Server target. Server builds remove or change graphics/audio-related work and can strip data, so qualify the target separately. [Desktop headless mode](https://docs.unity.com/en-us/engine/6000.3/manual/platform-specific/dedicated-server/desktop-headless-mode), [Dedicated Server optimizations](https://docs.unity3d.com/6000.0/Documentation/Manual/dedicated-server-optimizations.html).

## Architecture and game changes

Keep the overview's control protocol and lifecycle. Each bot is a real client process connected to the server over Mirror, not a server-side NPC counted as a network player. A server-side NPC is still useful for gameplay load, but label it separately because it does not exercise client serialization, authentication, interpolation or network admission.

Add a game-owned `PlaytestActionDriver` concept that submits move/look/interact/ready actions through the same command path as a human-controlled client. Use deterministic scripts first. ML policies can be an optional action producer later; they are not required to establish multiplayer orchestration. Observations should include authority, health/inventory/object state, scene readiness, and relevant events. Never use unrestricted reflection or arbitrary code execution as the control API.

Audit camera dependencies, `Camera.main`, render textures, animation/skinning-driven gameplay, audio callbacks, UI-only initialization, frame-end coroutines, cursor/input focus, physics timing and GPU observations. A component compiling under `UNITY_SERVER` is not evidence of correct headless behavior. Explicitly define whether a bot follows production simulation time or accelerated test time; do not mix time scales within a session without a scenario designed for it.

For eventual Steam tests, do not assume a headless desktop process removes the need for a Steam client/account. A Steam game-server API process has a different initialization and identity model; see [Steam qualification](LDAI-32-06-Steam-Sockets-Qualification.md).

## Implementation phases and DoD

| Phase | Work | Definition of done |
|---|---|---|
| A — Compatibility audit | List rendering-dependent systems and classify assertions as visual/nonvisual | Every scenario states supported execution profiles and why; unsupported GPU/overlay requirements fail preflight |
| B — Bot driver | Implement a narrow action/observation contract | A scripted client uses normal authority checks; forbidden action is rejected; action/result sequence is traceable |
| C — Desktop headless | Run the same artifact in rendered and headless profiles | One representative scenario has matching gameplay invariants; no unexplained missing initialization or object state |
| D — Server target | Produce a separately versioned Dedicated Server artifact, only if beneficial | Correct scenes/assets included; client-only systems excluded safely; actual client/server protocol match verified |
| E — Capacity | Benchmark 1 server + 1, 2, then 4 clients and one observer variant | Peak RAM/CPU/VRAM and tick/frame timing captured; admission cap selected from measurements, not assumed player count |
| F — Reliability | Long enough soak for the target scenario, repeated resets, crash/cancel tests | No orphan processes, growth beyond declared budget or stale logical players; failures retained and classified |

## Resource policy

Use measured per-role cost. Starting at one server and two headless clients is a planning default, not a supported-capacity claim. Sample process working set, CPU usage, handles, log volume, server tick duration and client update delay. Add per-instance and aggregate thresholds; a limit breach should stop or invalidate the performance profile before the PC becomes unresponsive.

Headless does not mean free: physics, AI, animation logic, serialization, asset memory and KCP work remain. Headless clients can consume enough CPU to slow the server and all other clients simultaneously. Avoid assigning rigid one-core affinity by default; measure whether it harms scheduling. Keep network tick and gameplay fidelity unchanged in the baseline. The total RAM formula and safety reserve live in the overview.

Separate throughput outcomes from gameplay outcomes. A scenario may observe correct state while the machine misses its tick budget; record “functional pass, performance budget failed” instead of averaging away the overload. Use fewer bots or a lower-cost scenario before concluding the network algorithm is defective.

## Acceptance scenarios

1. A rendered client and a headless client execute the same join/interact/disconnect script against the same server artifact; server and remote observations match within declared tolerances.
2. Client-local ownership is enforced; a bot cannot control another participant's actor merely because it has a test hook.
3. A server-only process starts without client cameras/UI and still performs scene transition and late-join state transfer when supported.
4. A bot crashes mid-action; parent run preserves the first failure, remaining processes stop within deadlines, and the next attempt has a clean roster.
5. A deliberately render-dependent assertion is rejected as unsupported in a no-render profile, not silently skipped and reported Passed.
6. A long script's result requires completed assertion/event counts; an exit code of zero alone cannot make the session pass.

## Tradeoffs and stop criteria

Pros: cheaper functional coverage, less focus/input automation, repeatable roles and rapid restart. Cons: game instrumentation is required; headless and server builds can diverge; bots miss subjective UX and visual issues; shared-host scale is limited.

Stop optimizing when maintaining headless compatibility costs more than the measured resource saving, or when the scenarios fundamentally require rendering/GPU observations. Keep [Solution 01](LDAI-32-01-Native-Multiprocess-KCP.md) as the reference. Add cloud/headless workers only after local profiling proves a need and remote authorization is designed; no cloud purchase is part of this plan.

Proof boundary: this plan and its sources establish an implementation path. No headless build was produced, launched, profiled or compared with a rendered player during this research.
