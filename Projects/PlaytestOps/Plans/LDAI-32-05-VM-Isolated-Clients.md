# LDAI-32 Solution 05 — VM-isolated Local Clients

Date: 2026-10-15  
Status: Conditional single-physical-machine qualification plan; not provisioned or validated.  
Parent: [Overview and shared contract](LDAI-32-00-Local-Multiplayer-Overview.md).

## Decision

Use a host plus one full VM first when separate OS user profiles and Steam client installations matter more than startup speed. Give the guest its own approved account and game entitlement. Add more guests only after graphics and resource feasibility are measured. VMs are an execution environment for KCP or Steam tests, not a networking stack replacement.

This can approximate two computers at the OS/process level while sharing one physical CPU/GPU/uplink. It is not independent-hardware performance evidence or proof of geographically separate routing. The current FizzySteamworks guidance names two machines/accounts; a guest arrangement remains a compatibility experiment until the actual game/Steam environment passes. [FizzySteamworks guidance](https://github.com/Chykary/FizzySteamworks).

## Feasibility comes before provisioning

Record host Windows edition/build, virtualization support, available RAM/SSD, CPU topology, GPU/driver, guest OS requirements and legitimate licensing. Check the hypervisor's supported graphics path against the actual hardware. Do not presume that consumer Hyper-V on Windows provides a supported gaming GPU passthrough setup. Microsoft's documented GPU-P deployment path is scoped to Windows Server 2025 and supported hardware; that is not a promise for this desktop. [Hyper-V requirements](https://learn.microsoft.com/en-us/virtualization/hyper-v-on-windows/reference/hyper-v-requirements), [GPU partitioning prerequisites](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/partition-assign-vm-gpu).

An alternative hypervisor with a supported virtual 3D adapter can be evaluated, but it must pass the same actual scene/rendering test. No particular product, paid edition, license entitlement or hardware purchase is selected by this plan. Prefer an already available second device if GPU virtualization becomes the dominant engineering problem.

## Proposed topology and network contract

Start with one native host process and one guest client. For KCP, the guest cannot connect to the host game's `127.0.0.1`; that address means the guest itself. Choose an explicit host/guest reachable address on a private/internal virtual network and allow only the game UDP endpoint and authenticated worker control endpoint needed by the test. Do not expose the unauthenticated dashboard generally to the virtual LAN.

For Steam qualification, the guest also needs approved outbound Steam connectivity and its own active Steam session. NAT versus bridged networking changes reachability and test conditions; record the virtual switch mode, guest IP, relevant firewall rules and observed connection route. An internal network without Internet is a valid KCP fixture but cannot establish live Steam services behavior.

Put a small guest worker in each VM. It receives a typed, authenticated run lease, verifies the artifact, launches the game locally, and streams bounded results to the host supervisor. Prefer guest-initiated outbound authenticated TLS so no general remote-shell service is exposed. Credential enrollment is explicit. The host must not execute arbitrary guest commands obtained from a dashboard form.

Reuse one desktop artifact by copying it once per guest/cache and verifying its tree hash. A shared read-only folder can reduce copies if the chosen hypervisor's path/loading performance is acceptable, but separate local copies are often simpler to reason about. Writes go to a guest-local run directory; collect evidence back to the host with the matching manifest. Only binaries of the same platform/architecture are byte-identical; Linux and Windows variants are distinct artifacts.

## Implementation phases and DoD

| Phase | Work | Definition of done |
|---|---|---|
| A — Hardware/OS gate | Inventory and select supported hypervisor/guest/graphics configuration | No unsupported passthrough assumptions; licensing and admin/reboot needs recorded; user approval obtained for provisioning |
| B — One clean guest | Install only necessary runtime/worker prerequisites | Boot/shutdown repeatable; account state protected; no production credentials in base image |
| C — Render feasibility | Launch actual game scene and measure GPU/CPU/RAM | Required graphics API/features work; acceptable frame pacing documented; otherwise headless-only or no-go |
| D — KCP connectivity | Host/guest endpoint and firewall, control pairing, artifact cache | Game joins and remote observations pass; guest disconnect and network block yield bounded failures |
| E — Steam environment | Approved account login/entitlement and intended app | Correct distinct Steam identity observed; actual Sockets/lobby qualification performed separately |
| F — Supervision | Guest lease, process ownership, heartbeat, cancel and crash recovery | Guest agent loss, host worker restart and VM pause cannot leave an untracked accepted run |
| G — Repeatability | Reset owned game state and compare fresh/warm guest profiles | Results carry image/version metadata; clean reruns have no save/account contamination; capacity cap measured |

## Accounts, images and snapshots

Keep a clean guest image before account login where practical. Steam credentials, 2FA material and session caches must not be baked into shared images or repository artifacts. A snapshot taken after login contains sensitive state and requires appropriate local access control and retention. Do not distribute it as a convenient test fixture.

Do not clone the same logged-in Steam identity and call the clones separate players. Each participant needs its actual intended account; verify identities through the runtime integration. Avoid reverting a running online session to an old snapshot. End the game/network session first, preserve evidence, then reset only the approved guest/test state. Restoring a snapshot does not undo remote Steam Cloud, achievement or backend effects.

## Resources and cost model

Budget host OS/Editor/dashboard plus each guest OS, Steam client, game process, graphics backing memory and file cache. Example planning equation:

`guest_budget = OS_and_background_peak + Steam_peak + game_peak + graphics_backing + headroom`.

No numeric supported capacity is asserted. Measure one guest before choosing allocations. Disk usage includes base image, writable differencing disks, caches, copied artifacts and retained captures; snapshots can grow rapidly. Review guest Windows rights and hypervisor/GPU feature licensing through the actual license channel before deployment; this plan is not a licensing determination or purchase authorization.

Resource oversubscription distorts network timing and physics. VM pauses, host sleep, updates and GPU scheduling should be explicit disruption tests, not unexplained random delays. Initial policies: one guest, one session, no host sleep during an authorized run, bounded log/capture output, and no background build/import competing for resources.

## Cleanup and acceptance

Guest workers own game jobs; the host owns only the VMs/leases it explicitly created for testing. First request game shutdown, then guest worker cleanup. If the worker is unreachable, stop only the owned test VM after the configured grace period and record abrupt termination. Do not power off a user-owned shared VM or kill the host Steam client.

Accept the environment after native-equivalent assertions, distinct state/identity evidence, repeated restarts, agent loss, guest crash, network denial, cancellation and hash mismatch tests. Record shutdown completion and evidence export status separately. A VM reset that loses logs does not turn an unknown result into success.

Pros: clearer OS/account separation and controllable reset state. Cons: highest local RAM/disk/graphics burden, patch/login overhead and version-sensitive support. Stop the feasibility spike when required rendering or separate Steam initialization cannot work through supported configuration; use headless KCP or an authorized second physical device. Optional remote/cloud guests require additional authenticated remote-worker design and a separate cost approval.

Proof boundary: no VM, GPU partition, network switch, firewall rule, guest license, Steam account or cloud resource was created or changed during this research.
