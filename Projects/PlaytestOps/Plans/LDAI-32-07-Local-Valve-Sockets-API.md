# LDAI-32 Solution 07 — Local Valve Sockets API Coverage

Date: 2026-10-15  
Status: Optional transport-engineering lane; not required for the KCP MVP and not Steam service qualification.  
Parent: [Overview and shared contract](LDAI-32-00-Local-Multiplayer-Overview.md).

## Decision

Consider a Mirror adapter backed by Valve's open-source **GameNetworkingSockets** when the team specifically needs repeated same-machine tests of Valve-style connection/message behavior without separate Steam accounts. Use ordinary IP connections between native processes. This can narrow the gap between KCP gameplay tests and real Steam tests, but it adds a transport and native-library maintenance burden.

Valve's open-source library does not itself include Steam's authentication, signaling or SDR relay services. It is a subset sharing API concepts with the Steamworks version. A pass here must be labeled `valve-sockets-ip`, never `steam-authenticated` or `steam-relay`. [Valve GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets).

This is not simply changing FizzySteamworks' endpoint from a SteamID to `localhost`. The inspected FizzySteamworks adapter uses Steam P2P APIs. An IP-capable adapter or a carefully scoped fork is needed, with a compatibility/licensing review and pinned native binaries. Retain KCP as the simple default until this additional lane demonstrates concrete defect-detection value.

## Candidate forms

| Form | Coverage | Scope decision |
|---|---|---|
| Open-source GameNetworkingSockets + Mirror IP adapter | Native multi-process transport behavior over loopback without Steam services | Preferred form for this optional lane |
| Steamworks Sockets raw-IP mode | Some production SDK API coverage, still subject to SDK/context rules | Separate feasibility experiment; do not assume account-free operation |
| Socket-pair unit fixture | Fast in-process adapter/message tests | Unit-test supplement only; not multi-process or authenticated-user proof |

Valve exposes raw-IP and socket-pair APIs. A default socket pair can bypass real packet/network work; enabling network loopback changes what it exercises. Therefore “socket pair connected” is not a replacement for separate-process IP coverage. [Sockets API](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets).

## Proposed architecture

Reuse the artifact registry, worker, lifecycle, scenario drivers and result format from the overview. Add `transport=valve-sockets-ip` and a declared adapter version/native-library fingerprint. The server binds a dedicated local UDP endpoint; clients connect by explicit IP/port. Run-scoped test identity stays separate from any platform identity.

The adapter is responsible for translating Mirror connect/data/disconnect/error callbacks, reliable/unreliable channels, maximum message sizes, message buffer lifetime, connection-to-Mirror ID mapping and safe shutdown. Drive callbacks in the correct Unity update phases without blocking the main thread. Treat received messages as untrusted and enforce length/queue limits before allocation or deserialization.

Do not add a new game-session abstraction solely for this transport; use the existing proposed transport-independent game bootstrap and scenario API. A new binary/native library requires a new artifact hash. All instances of that variant still use the same built artifact repeatedly.

## Implementation phases and DoD

| Phase | Work | Definition of done |
|---|---|---|
| A — Value/maintenance gate | Identify defects KCP cannot cover; assess available maintained bindings/adapters | Written reason to add lane; ownership and pinned versions selected; no dependency chosen only because it compiles |
| B — Native loading | Trusted library ingestion, architecture matching and clean-machine prerequisites | Missing/wrong-architecture library fails clearly; correct binary loads in built player; licenses/notices retained |
| C — Adapter unit tests | Callback mapping, reliable/unreliable channels, buffers and disconnect | Invalid handles/oversized data/duplicate close handled; resource ownership proven with fakes and socket fixtures |
| D — Two-process IP test | Server/client handshake and messages | Actual native processes connect through loopback; port conflicts and invalid endpoints fail within deadlines |
| E — Mirror parity | Same scenario suite used for KCP | Gameplay invariants match; channel/MTU/order assumptions exposed; all logs name actual transport |
| F — Fault/cleanup | Supported latency/loss settings, connection interruption, callback races and worker death | No native handle/buffer leaks across repeated runs; missing callbacks cannot hang forever; unrelated processes unaffected |
| G — Adoption decision | Compare coverage/maintenance cost against Steam lane | Keep only if it catches useful issues; otherwise archive the spike without replacing KCP or Steam qualification |

## Test design

Begin with reliable ordered payload round-trip, unreliable message handling, connection rejection, peer close, server close, oversized-message rejection, connection-ID reuse and rapid reconnect. Use payload hashes and sequence counters rather than logging large raw data. Verify how the pinned library represents errors and loss; do not assume KCP and Valve channels have identical buffering or delivery behavior.

Then run the actual game scenario: admission, authoritative spawn, action, independent remote observation, scene transition and leave. A transport benchmark alone cannot prove Mirror serialization, ownership or gameplay behavior. Compare the exact scenario and record different transport timing rather than forcing identical event ordering across implementations.

For fault simulation, distinguish message-layer delay from actual transport packet impairment. Use only documented library settings or a declared network fixture; record the seed/configuration when supported. If a random source cannot be seeded, preserve observations and call the run repeatable in setup, not bit-for-bit deterministic.

## Security, resources and cleanup

Bind/firewall local endpoints, authenticate test participants independently, and keep control-plane credentials separate from gameplay messages. The absence of Steam identity means this lane needs its own fixture admission; it must not silently become a production authentication scheme.

Native memory/message ownership deserves explicit soak tests. Set queue/message limits and measure process working set, native allocations where observable, handles, CPU and shutdown duration. An ordinary network disconnect is not proof that all native resources were released. Clean up both ends of socket fixtures, every connection, listen socket, callback registration and library initialization lifetime according to the pinned API.

Use the same process ownership and port lease rules as native KCP. No special cloud account or VM is required. Costs are engineering time, additional binary packaging and ongoing compatibility tests; avoid this lane when those costs exceed the coverage gained.

## Pros, cons and proof boundary

Pros: useful local transport-specific failure reproduction; no need to involve live Steam accounts for every low-level iteration; comparable real process topology. Cons: adapter/native binding work, drift from the production Steam SDK branch, and a risk of false confidence if results are mislabeled.

The final acceptance gate remains [Steam Sockets qualification](LDAI-32-06-Steam-Sockets-Qualification.md) for real identity, lobby, entitlement and relay behavior. This plan does not claim that an existing installed Mirror transport already offers the IP lane, or that any library was built, installed or tested during this research.
