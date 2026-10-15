# LDAI-32 Solution 01 — Native Multi-process Mirror KCP

Date: 2026-10-15  
Status: Recommended MVP; proposed, not implemented or runtime-verified.  
Parent: [Local multiplayer overview and shared contract](LDAI-32-00-Local-Multiplayer-Overview.md).

## When to choose this

Choose this for the fastest useful automated multiplayer lane on one PC. Launch the same trusted desktop build multiple times; choose `host`, `client` or `server` through a game-owned bootstrap before starting Mirror. KCP is included with Mirror and uses UDP. Mirror's Network Manager supports client, server and host roles. [Mirror transports](https://mirror-networking.gitbook.io/docs/manual/transports), [Network Manager](https://mirror-networking.gitbook.io/docs/manual/components/network-manager).

This is not one process containing several faux players. A host's local player has a special local path, so at least one **separate client process** is mandatory. Also run server+two-client topology before claiming both clients traverse real transport connections.

## Architecture

PlaytestOps's proposed local supervisor owns one session. It starts the authoritative process first, waits for a control-plane `server-ready` event after Mirror is actually listening, then launches clients with the resolved endpoint. Every process connects to the supervisor separately and reports artifact ID, role, fixture state and observations. Gameplay travels through KCP; orchestration commands travel through the authenticated control channel described in the overview.

Use one UDP server port per session, not one per client. Bind or firewall the test listener appropriately; do not assume a loopback client address prevents LAN access. Keep initial concurrency at one session. Test discovery is disabled; the manifest is the source of the endpoint and expected roster.

Proposed runtime fields are `runId`, `instanceId`, `role`, `transport=kcp`, `serverEndpoint`, `scenarioId`, `seed`, `dataRoot`, and `controlConfigPath`. They are application arguments/configuration, **not built-in Unity or Mirror command-line options**. Use typed argument construction and an allowlisted executable path; never concatenate browser input into a shell command.

Unity documents player options including `-logFile`, window dimensions and headless flags. Use a unique log path and small window per rendered participant, with background execution enabled. Verify the build does not force a single process instance. These settings do not isolate saves or input devices. [Player arguments](https://docs.unity3d.com/6000.0/Documentation/Manual/PlayerCommandLineArguments.html), [runInBackground](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-runInBackground.html), [forceSingleInstance](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/PlayerSettings-forceSingleInstance.html).

## Game integration requirements

- Separate transport selection, session lifecycle and platform identity. KCP mode must not call Steam lobbies or require a running Steam client; production Steam mode remains explicit.
- Select the active transport once before `StartHost`, `StartServer` or `StartClient`; reject changes while connected.
- Admit only the expected test participants and bind their logical IDs to the server's actual connections. Fixture credentials do not become production login credentials.
- Provide reset/load/readiness hooks and a bounded action API. All normal test actions should take the same authority/validation route as gameplay inputs.
- Expose observations from server and clients independently. A server-only assertion that a value changed is insufficient to prove remote replication.
- Route writes through per-instance storage. PlayerPrefs and plugins need an explicit audit; a distinct working directory alone is insufficient.

## Implementation phases and DoD

| Phase | Work | Definition of done |
|---|---|---|
| A — Inventory | Confirm serialized manager/transport, online/offline scenes, player prefab, room flow and Steam calls | Source and asset evidence identifies startup owner; KCP feasibility and unsupported assumptions documented |
| B — Bootstrap | Add test-only manifest loading and role/transport selection | Bad config fails before network startup; ordinary launch unchanged; two processes report different instance IDs but same artifact hash |
| C — Supervisor | Artifact verification, port lease, process job, logs, ready barrier and deadlines | Host+client launch repeatedly; failed bind/missing binary/second client never ready all produce explicit infrastructure outcomes |
| D — Scenario | Join, spawn, action, replicated observation, scene transition and disconnect | Both peers report the expected roster and state; injected replication break fails; disconnected identity is not reused as another participant |
| E — Failure cases | Process crash/hang, host exit, cancellation, log overflow, stale callbacks and worker restart | Every run reaches final outcome; no orphan owned process; unrelated sentinel process unaffected |
| F — Dashboard | Parent session and child instances attached to history | Operator sees role, endpoint, artifact, timing and first failing assertion; existing Editor run flow still works |

## Initial scenarios

1. **Host/client smoke:** client joins an already-listening host, both agree on participant count, each controls only its own actor, one client action reaches server and appears remotely, then both exit cleanly.
2. **Server/two clients:** no host-local player; both remote clients exercise authority and independent observation. Assert absence of client-only camera/UI null failures in server mode.
3. **Room/scene flow:** all required participants ready, one scene transition, clients acknowledge the correct scene and spawned roles; failure to load within deadline fails the run.
4. **Late join/disconnect:** if supported, a late client receives correct current state. Disconnect and rejoin use an explicit game policy; do not assume automatic persistence or host migration.
5. **Isolation/repeatability:** twenty proposed reset cycles with distinct data roots, same artifact, and alternating role launch order. Retain all attempts and report any state leak.

## Network-fault coverage

Mirror's `LatencySimulation` is useful for application-facing latency/jitter and unreliable-message loss/reordering. In the inspected implementation, reliable traffic is delayed, while loss/scramble apply to the unreliable channel; it is not a packet-level KCP retransmission lab. Record exact wrapper placement and values, and measure observed RTT rather than assuming a slider equals end-to-end RTT. [Mirror LatencySimulation source](https://github.com/MirrorNetworking/Mirror/blob/master/Assets/Mirror/Transports/Latency/LatencySimulation.cs).

Proposed profiles: baseline; 50 ms configured per-send delay; 100 ms plus small jitter; unreliable 2% loss; short disconnect/reconnect. The units and effect must be verified for the pinned implementation. Its default random state does not imply reproducibility from the game's scenario seed. Capture fault configuration and observed event timing; exact fault replay needs a separately designed deterministic shim. Kernel/VM packet shaping is a later option when actual KCP packet loss is the target, not an invisible substitution for this wrapper.

## Costs and risks

Pros: no Steam login burden, one artifact, debuggable logs, real process serialization, and little environment machinery. Cons: shared CPU/GPU can create apparent network stalls; shared OS state causes collisions if not explicitly redirected; host mode can conceal client/server asymmetries; low loopback latency is not WAN evidence.

Measure rendered client peak RAM/VRAM and background frame pacing first. A reasonable unvalidated starting configuration is two 960×540 windows, reduced quality, one session, a game-approved frame cap, and normal production network tick settings. Do not lower simulation/network rates simply to fit more clients without labeling the altered profile. Increase to four participants only if budget gates remain green.

## Acceptance and rollback

Accept the MVP after the overview's sequential-run and fault-injection gates pass, both host/client and server/two-client topologies are evidenced, artifacts remain unchanged, and cancellation proves cleanup. A KCP pass says nothing about Steam ownership, lobby discovery, overlay, NAT or relay.

Rollback is disabling the new capability/queue path and leaving existing Editor testing intact. Preserve manifests and logs; never reset the user's source project or delete production saves to recover a failed test. The first usable deliverable should be a single documented scenario, not a general-purpose arbitrary process launcher.
