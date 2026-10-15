# LDAI-32 Solution 08 — Hybrid and Managed-cloud Escalation

Date and source review: 2026-10-15  
Status: Optional implementation proposal; no account creation, upload, deployment, purchase or remote gameplay test performed.  
Parent: [Local multiplayer overview and shared contract](LDAI-32-00-Local-Multiplayer-Overview.md).  
Prerequisites: [Native process baseline](LDAI-32-01-Native-Multiprocess-KCP.md), [headless/server qualification](LDAI-32-02-Headless-Server-and-Bots.md), and an explicitly approved remote-control/security/cost envelope.

Build foundation: the sprint's **LDAI-27 Windows build-settings** work and **LDAI-28 dashboard build-execution** work are upstream inputs. Reuse their configured build creation and resulting artifact provenance where available; do not duplicate those features inside a cloud adapter. Their implementation does not by itself establish a multiplayer process supervisor, immutable artifact registry, Linux server build support, remote worker, or managed-cloud deployment. Those remain separate proposed capabilities with the gates below. Synthetic build-bridge checks also remain distinct from actual Unity build and runtime evidence.

## Decision

Keep most regression runs local. Escalate only for a specific gap: insufficient local server capacity, actual hosted-server behavior, independent remote peer/Steam environment, or a defined network/region matrix. Cloud hosting does not require replacing Mirror with Unity Netcode.

Choose the smallest topology that closes that gap:

| Topology | Good reason to use it | What it proves | What it does not prove |
|---|---|---|---|
| Managed dedicated server + two local clients | Offload server CPU; validate deploy/allocation/public endpoint | The selected server artifact runs remotely and accepts real client connections | Two local clients are still on one host; no automatic Steam user/lobby/SDR proof |
| Local host + owned second physical device | Real second player environment, preferably on another network | Independent process/device/account when actually configured | Broad geographic/NAT coverage or relay use without evidence |
| Local host/client + rented rendered GPU VM peer | No suitable second device; specific remote render/Steam experiment | The tested guest, driver, account and route combination | Universal game/anti-cheat/Steam compatibility or consumer-PC performance |
| Remote server + remote headless client worker | Remove local bottlenecks or run a bounded matrix | Declared functional/transport scenarios on remote workers | Human UI, overlay or rendered-client correctness |

For a listen-host game, a managed dedicated server is a **different topology**, not a drop-in substitute for the shipping host. Use an owned second device first for real Steam peer-host qualification when practical. A single remote machine can be enough to investigate a failure; a multi-region fleet is not the starting point.

## Provider shortlist and integration boundary

The suitability judgments below are recommendations inferred from primary documentation, not measured performance or a vendor commitment. Select one provider for a small spike; do not build all three integrations simultaneously.

### A. Edgegap — first managed-server spike for Mirror

Mirror publishes an Edgegap hosting-plugin guide, and Edgegap documents building a Linux dedicated server from a Mirror sample and packaging it as a container. This gives the shortest documented Mirror-specific starting point among these options; version compatibility and the actual game bootstrap still need validation. [Mirror hosting guide](https://mirror-networking.gitbook.io/docs/hosting/edgegap-hosting-plugin-guide), [Edgegap Mirror sample](https://docs.edgegap.com/docs/sample-projects/unity-netcodes/mirror-on-edgegap).

Proposed integration:

1. Produce and locally qualify one Linux server artifact and immutable container digest. Ensure its bootstrap starts Mirror in server mode without cameras or a user Steam client.
2. Register the digest/version and exact game UDP port. Keep deployment authorization on a trusted backend, never inside downloadable clients.
3. Request one deployment, persist its provider request ID before awaiting completion, and read the actual externally assigned endpoint. Internal and external ports can differ; do not give local clients the container's internal address.
4. Wait for both provider readiness and the game's authenticated run/scene/fixture readiness. Execute the same scenario as local KCP with the selected Internet-safe transport configuration.
5. Stop the exact deployment, collect evidence, and confirm termination. Edgegap documents deployment state/port mapping and a deployment-specific stop API that triggers graceful shutdown. [Deployment lifecycle](https://docs.edgegap.com/learn/orchestration/deployments), [Dedicated-server API](https://docs.edgegap.com/docs/api/dedicated-servers).

Pros: direct Mirror examples, container portability and short-lived deployment model. Cons: provider-specific allocation/auth/endpoint semantics, image registry operations and limited equivalence to a Steam listen host. Edgegap relay products are separate from dedicated-server compute and are **not Steam Datagram Relay**. Do not adopt extra matchmaking/server-browser services for an orchestrated test that already knows its participant list.

### B. Amazon GameLift Servers — when AWS hosting lifecycle matters

AWS provides a Unity server SDK and documents local-workstation testing through GameLift Servers Anywhere. Managed EC2 can host Windows/Linux server builds; managed container fleets are Linux-based. These services manage dedicated-server lifecycle, not Mirror gameplay replication. [Unity integration](https://docs.aws.amazon.com/gameliftservers/latest/developerguide/integration-engines-unity-using.html), [GameLift Servers overview](https://docs.aws.amazon.com/gameliftservers/latest/developerguide/gamelift-intro.html), [hosting/pricing models](https://docs.aws.amazon.com/gameliftservers/latest/developerguide/gamelift-intro-pricing.html).

Proposed adapter responsibilities: initialize the server SDK, report process readiness with the correct endpoint, handle session start, activate only after the game fixture is ready, implement health/termination handling, and end the session/process. Map any provider player-session admission to the actual Mirror connection instead of trusting an arbitrary client ID. Keep allocation/service credentials on the backend. The SDK's lifecycle and client/server interaction documentation are the integration contract. [Server SDK integration](https://docs.aws.amazon.com/gameliftservers/latest/developerguide/gamelift-sdk-server-api.html), [client/server interactions](https://docs.aws.amazon.com/gameliftservers/latest/developerguide/gamelift-sdk-interactions.html).

First qualify the adapter locally, then one managed fleet/session with approved capacity limits. Anywhere is useful for integration testing on owned hardware, but still uses a managed service and may have session/connection-minute charges; it is not an offline emulator or automatically free. Pros: explicit lifecycle and fleet controls, useful if AWS is already part of the target product. Cons: IAM/account/fleet/placement integration, more setup than a simple local test, and possible idle instance cost. Do not use GameLift Realtime or replace Mirror merely to fit a tutorial, and do not confuse GameLift **Servers** with a rendered application-streaming product.

### C. PlayFab Multiplayer Servers — when PlayFab/Azure is the target

Microsoft explicitly lists a UnityMirror sample for Multiplayer Servers. Its Game Server SDK (GSDK) coordinates initialization, allocation and process lifecycle; LocalMultiplayerAgent can exercise GSDK integration locally with mock responses and no cloud connection. This is a useful low-cost feasibility stage, not cloud-runtime proof. [Official samples](https://learn.microsoft.com/en-us/xbox/playfab/multiplayer/servers/server-samples-resources), [GSDK integration](https://learn.microsoft.com/en-us/xbox/playfab/multiplayer/servers/integrating-game-servers-with-gsdk), [LocalMultiplayerAgent](https://learn.microsoft.com/en-us/xbox/playfab/multiplayer/servers/localmultiplayeragent/local-multiplayer-agent-overview).

Proposed integration: adapt the pinned Unity sample to the game's Mirror server bootstrap; configure ports/build regions and small capacity; allocate through the backend; pass the returned endpoint and run admission to clients; run the shared scenario; export logs; terminate the allocated server. Avoid blocking Unity's main thread with a blocking generic SDK readiness call; use the appropriate Unity integration pattern and verify it.

A finished active game server does not return to standby; process exit allows a replacement. Crucially, replacement/standby capacity can continue after the test session ends. Teardown must reconcile the build's configured capacity as well as the allocated session. [Server lifecycle](https://learn.microsoft.com/en-us/xbox/playfab/multiplayer/servers/multiplayer-game-server-lifecycle).

Pros: an official Mirror example, local integration simulator and managed allocation. Cons: title/build/GSDK/API coupling, standby cost and Azure capacity/quota decisions. Adopt it if PlayFab is a desired target, not solely to avoid writing a local process launcher. PlayFab platform identity is not automatically the authenticated Steam identity required by the game's policy.

## Artifact and networking architecture

Reuse the overview's run/instance/scenario schema. Add provider, account/project alias, region, allocation ID, environment/image version, container digest, external endpoints, requested resources, lease expiry and cost-estimate version. Preserve the same desktop client artifact locally. A Linux or Dedicated Server binary is a separate qualified variant with its own hash and source/build provenance. Build once per variant, upload once, and reuse the exact digest for repeat runs; do not rebuild for every allocation or trust a mutable `latest` image tag.

A small proposed provider interface should expose typed operations such as `Allocate`, `GetStatus`, `ResolveEndpoint`, `CollectEvidence` and `StopOwnedAllocation`. These are design names, not existing APIs. Provider SDKs and JSON shapes stay inside adapters. Keep scenario actions, assertions and logical participant IDs provider-neutral. If a provider requires different server compilation, record a different artifact rather than pretending all adapters use identical bytes.

Separate three security planes:

- **Provider management:** least-privilege project-scoped credentials, preferably short-lived where supported; no administrative account keys in games, public repositories or browser storage. AWS documents IAM-based API authorization; apply equivalent reviewed boundaries for each selected provider. [GameLift IAM integration](https://docs.aws.amazon.com/gameliftservers/latest/developerguide/security_iam_service-with-iam.html).
- **Playtest control/evidence:** a new authenticated TLS endpoint/broker with per-run leases, role authorization, replay protection and bounded payloads. Remote workers connect outbound where practical. The current localhost-only PlaytestOps dashboard/Editor bridge must not simply be exposed to the Internet. Authentication and authorization are a prerequisite, not a future hardening task.
- **Gameplay:** exact UDP port mappings and reviewed admission. TLS on a provider REST API does not encrypt KCP packets. Require an approved encrypted game-transport layer or an authenticated private tunnel for confidential test traffic; record any tunnel because it changes the measured route. A raw-IP path is not the Steam P2P path. Use only scoped firewall rules; do not expose RDP/SSH or debug consoles broadly.

Before accepting a remote instance, verify its allocation/run identity, artifact/protocol version, scene/fixture and expected role. A provider state of `Ready` only means provider readiness. Cross-region clocks are not assumed perfectly synchronized: use local monotonic durations and action/event IDs for causality, with UTC for approximate correlation.

## Remote rendered peer and cloud GPU VM lane

Use the same game/worker contract on an owned second device whenever available and authorized. Install only the required runtime, copy and verify the client artifact, pair the worker explicitly, and run the approved scenario. If the other person must log in, accept an invite or operate the UI, that coordination requires their involvement; it is not implied by this plan.

A GPU VM is an alternative, not a managed dedicated-server feature. AWS describes G5 for graphics/gaming workloads; Azure documents NVadsA10 v5 GPU-backed sizes. These are candidates for a compatibility spike, not guaranteed suitable SKUs or quoted costs. Verify region availability, quota, OS image, driver, DirectX/Vulkan support, usable desktop/session, capture method, VRAM and frame pacing on the actual game. [EC2 G5](https://aws.amazon.com/ec2/instance-types/g5/), [Azure NVadsA10 v5](https://learn.microsoft.com/en-us/azure/virtual-machines/sizes/gpu-accelerated/nvadsa10v5-series).

Real Steam testing still requires the approved app, separate entitled accounts and correct initialization/launch context. Cloud location or a GPU does not provide another identity. Protect login/2FA/session caches; never bake logged-in Steam credentials into a reusable image or share account secrets in manifests. Verify provider/game/Steam terms and OS/GPU/remote-desktop licensing for the intended development use before provisioning. This is not a license determination or permission to operate a third-party cloud gaming service; Valve's separate Cloud Play arrangements are not a blanket entitlement for every rented VM. [Steam testing access](https://partner.steamgames.com/doc/store/testing), [Steam Cloud Play](https://partner.steamgames.com/doc/features/cloudgaming).

Stop if the actual Steam/DRM/anti-cheat integration rejects the environment. No concealment, identity spoofing, license bypass or disabling safeguards is part of this plan. Maintain an authorized physical-device fallback. A Steam game server plus one user is not two distinct Steam users; two users joining a cloud server are not proof of the listen-host lobby/relay path. Apply [Solution 06's evidence matrix](LDAI-32-06-Steam-Sockets-Qualification.md) without weakening it.

## Phased implementation and definitions of done

| Phase | Work | Definition of done / acceptance |
|---|---|---|
| 0 — Escalation case | Name the local limitation and desired proof label | Measured bottleneck or missing remote scenario documented; topology selected; local baseline remains authoritative |
| 1 — Approval/security gate | Select one provider/device, account owner, region, maximum capacity/runtime/spend and allowed data | Explicit approval recorded; authenticated remote control designed; no deployment while any prerequisite is absent |
| 2 — Artifact/local provider test | Build/pin server variant, image and adapter; run local container/Anywhere/LMA as appropriate | Same gameplay scenario passes locally; no secret in artifact; wrong digest/role/config rejected; adapter shutdown tested |
| 3 — One remote allocation | Provision only approved minimal capacity and resolve actual endpoint | Provider and game readiness distinguished; two intended clients connect; result carries allocation/region/digest metadata |
| 4 — Shared scenario | Run admission, authority, replicated action, scene transition and leave | Server and independent client observations satisfy unchanged assertions; intentional failure is detected |
| 5 — Failure and cost cleanup | Cancel during placement/start/play, crash server, lose control connection, expire lease, duplicate callback and quota failure | No unknown allocation treated as absent; owned allocations reconciled; agreed billable-capacity baseline restored and reported |
| 6 — Rendered/Steam extension, only if needed | One remote peer/device or approved GPU VM | Actual rendering/account/route evidence captured; full Steam gate passes or limitation is recorded without fallback relabeling |
| 7 — Bounded adoption | Compare coverage, setup time, run cost and maintenance against local tests | A small approved schedule/manual trigger chosen; no automatic cloud escalation; exit/migration procedure verified |

Provisional first experiment: one remote server, two local clients, one short deterministic scenario and one forced failure, then complete teardown. Start with one region and no autoscaling expansion beyond the approved maximum. Later add a deliberately different region/network only when it answers a named question. These are proposed limits, not a reservation or permission to spend.

## Deadlines, cleanup and evidence retention

Use separate deployment and gameplay deadlines; a proposed five-minute allocation budget does not extend the scenario's wall-time limit. Persist allocation intent before creating resources, use provider idempotency where available, and reconcile by owned ID/tags after an uncertain response. Never retry a timed-out create blindly and create multiple chargeable servers. A stale completion callback must not start clients for a cancelled run.

Lease expiry must be enforceable independently of the initiating laptop. Plan a provider-supported TTL or separately authorized scoped reaper/worker, with a run-specific allowlist and a tested failure path. A local timer is insufficient if the PC sleeps or loses Internet. The reaper is itself an operational resource whose permissions, lifetime and cost belong in the plan.

Cleanup order: stop new admissions → request game shutdown → collect bounded evidence → stop/terminate the exact owned allocation → verify provider terminal state → reconcile fleet/build standby and GPU-VM state → release owned temporary endpoints/secrets → report residual resources. Session end and zero ongoing billable compute are different checks. Do not delete shared fleets, images, storage, keys or unrelated resources. For general VMs, distinguish guest OS shutdown, stopped/deallocated state, and deletion; retained disks/IPs/snapshots can still have costs.

Export a small signed/hashed manifest and per-instance summaries first, then bounded raw logs/captures. Keep the overview's local retention policy, and propose an explicit cloud retention policy such as seven days for successful raw logs and thirty for failed-run evidence, subject to approval and storage budget. Provider-default log availability is not guaranteed long-term retention. Redact credentials, account identifiers and private content before sharing; private object storage, limited access and expiring download links are required. Forced termination may prevent final log upload, so missing evidence remains an incomplete result.

## Cost model and approval checklist

**Planning model dated 2026-10-15; all workload quantities are estimates and account-specific rates require confirmation. No provider quote, final SKU, budget or purchase is selected.** Free trials/credits may reduce a bill but are not assumed available or sufficient.

Use an adjustable worksheet/model:

`C_total = C_active_compute + C_startup_and_standby + C_service_API + C_GPU_peer + C_egress + C_storage_and_logs + C_network_access + C_OS_GPU_licenses + C_support + contingency`.

For instance-priced services, `C_compute = Σ(rounded_billable_instance_hours × current_region_OS_SKU_rate)`. For vCPU-minute services, use `Σ(allocated_vCPU × rounded_billable_minutes × rate)`. Add failed deployments, image-pull/boot time, health recovery, idle capacity and cleanup delay. Do not charge only successful scenario minutes. An illustrative workload of 20 runs × 15 minutes × one instance is five active instance-hours **before** those overheads, not a monetary quote. A GPU peer adds its own allocation and storage/streaming costs.

| Provider/lane | Cost inputs to confirm before approval |
|---|---|
| Edgegap | Current vCPU-minute rate, billing granularity, memory/size constraints, egress, minimum charges, registry/log costs and any optional service subscription; avoid reserved private capacity for the first spike |
| GameLift Servers | Region/OS/instance, On-Demand versus interruptible Spot, fleet idle hours, container packing, API/Anywhere charges where applicable, logs/storage, and bandwidth eligibility; the current rate card has bandwidth inclusion for eligible instance generations, so do not apply an old universal egress assumption |
| PlayFab MPS | VM-hours, number of processes per VM, standby allocation, region/quota, egress and title billing mode; free evaluation eligibility is account-specific |
| GPU VM / owned peer | GPU VM boot/login/idle/runtime, disk/snapshot retention, public IP/network gateway, remote desktop/streaming bandwidth, OS/driver rights, or owned-device electricity and operator time |

Current primary pricing references: [Edgegap pricing](https://edgegap.com/resources/pricing), [GameLift Servers instance pricing](https://aws.amazon.com/gamelift/servers/pricing/instance-pricing/), [GameLift cost planning](https://docs.aws.amazon.com/gameliftservers/latest/developerguide/gamelift-intro-pricing.html), [PlayFab MPS billing](https://learn.microsoft.com/en-us/gaming/playfab/multiplayer/servers/billing-for-thunderhead), [PlayFab current price sheet](https://developer.microsoft.com/en-us/games/products/playfab/pricing/). Confirm the actual account/region quote immediately before provisioning; dated documentation examples are not current purchase quotes.

Approval must state maximum concurrent servers/VMs, allowed regions/SKUs, maximum lease duration, number of runs/retries, total spend ceiling, retained storage and cleanup owner. Add billing alerts, but do not call them a hard spend cap: metering/notifications can lag. Enforce capacity/runtime caps in the runner and provider configuration where supported, and reconcile the final cost estimate with provider usage after the run. Do not enable unattended expansion or accept a subscription/reservation automatically.

## Provider lock-in and exit criteria

Lock-in lives in server lifecycle SDKs, admission tokens, image/build registration, allocation metadata, logs and capacity policies. Keep those in small reviewed adapters; do not leak provider session IDs into persistent game identity or scenario definitions. Preserve portable source manifests, client/server artifact hashes, OCI image digests where applicable, JSON evidence and the local runner as a fallback.

Before adoption, demonstrate the same scenario locally and on the chosen provider, identify provider-only assertions separately, and document how to export evidence and stop paid resources. A provider migration may require rebuilding a server adapter variant; portability means a bounded change with fresh qualification, not a promise of binary interchangeability. Stop the cloud spike if its maintenance/cost exceeds the coverage benefit, remote authentication is incomplete, or the intended Steam/rendered environment is unsupported.

Proof boundary: current primary documentation establishes available hosting/integration mechanisms. No cloud account or license was inspected, no rates were accepted, no image was uploaded, no provider API mutation was made, and no remote/Steam gameplay or cleanup behavior was runtime-validated by this research.
