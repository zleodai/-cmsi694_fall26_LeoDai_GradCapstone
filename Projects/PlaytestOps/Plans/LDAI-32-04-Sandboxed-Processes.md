# LDAI-32 Solution 04 — Sandboxed Process Isolation

Date: 2026-10-15  
Status: Conditional feasibility plan; not a supported Steam P2P topology claim.  
Parent: [Overview and shared contract](LDAI-32-00-Local-Multiplayer-Overview.md).

## Decision

Evaluate Sandboxie-Plus when filesystem/registry state collisions or lightweight process separation are real blockers. It may be useful for running a known game artifact in separate writable environments on one Windows installation. Do not introduce it merely to run two KCP players; ordinary processes with good per-instance state routing are simpler.

An attempted separate-Steam-client setup is a **separate, gated experiment**, not the default recommendation. Sandboxing a game that still uses the host's one Steam client/account does not give it a new authenticated Steam identity. The FizzySteamworks maintainer's documented supported test arrangement uses separate machines and accounts; this plan does not override that guidance. [FizzySteamworks](https://github.com/Chykary/FizzySteamworks).

## What isolation does and does not mean

Sandboxie exposes per-sandbox execution and resource access rules. Its documentation also lists applications and services/drivers that may fail in a sandbox. Compatibility must be checked against the pinned Sandboxie version, Windows build, Steam client and game. [Resource access settings](https://sandboxie-plus.com/sandboxie/resourceaccesssettings/), [Known conflicts](https://sandboxie-plus.github.io/sandboxie-docs/Content/KnownConflicts/).

Proposed contract: a sandbox is a distinct **writable-state and process ownership scope**, not a VM, independent GPU, guaranteed network namespace or entitlement boundary. Expect shared host CPU/GPU and explicitly test networking/IPC behavior. Do not assume two boxes may bind the same UDP port; keep per-session port allocation unless the tested configuration proves isolated networking.

## Two useful lanes

### Lane A — State isolation with KCP

Run host/client instances of the same verified artifact with separate named test boxes. Keep fixture actions and observations identical to Solution 01. Prove that settings/save writes do not cross boxes and that no production save is touched. Prefer application data-root hooks even in a sandbox: they make results portable and reduce dependence on a virtualization layer's path behavior.

### Lane B — Optional Steam client/account experiment

Proceed only with user-approved test accounts, valid game entitlement and a developer-controlled build. Establish whether two independently logged-in Steam clients can remain active in the specific configuration and whether each game process observes its intended distinct account. Never copy login tokens between boxes or automate credentials/2FA through logs/scripts. Then apply the full [Steam qualification](LDAI-32-06-Steam-Sockets-Qualification.md) gate.

If Steam/DRM/anti-cheat or the transport rejects the environment, stop and record unsupported. Do not hide the sandbox, patch checks, weaken security software, spoof identities or bypass entitlement. A compatibility issue is not a reason to risk the user's normal Steam account. A second authorized device is the fallback.

## Implementation phases and DoD

| Phase | Work | Definition of done |
|---|---|---|
| A — Feasibility inventory | Record installed versions, sandbox features, account ownership and game requirements | Explicit go/no-go; no installation or security change occurs without separate authorization |
| B — Disposable KCP box | Start one known artifact with restricted inputs and owned output directory | File/registry probes demonstrate intended isolation; game joins native host; evidence exported |
| C — Two-box lifecycle | Distinct instance names/configuration and per-box cleanup | Crash/cancel/start failure closes only owned box processes; native Steam and unrelated boxes stay running |
| D — Repeatability | Reuse immutable artifact, reset owned state, rerun | Hashes unchanged; clean initial state; startup/log/export overhead measured |
| E — Steam feasibility, optional | Separate approved account sessions and transport handshake | Distinct observed identities, proper entitlements and actual transport events; otherwise no-go with reason |
| F — Graduation decision | Compare cost/benefit against native/VM alternatives | Adopt only for a documented need; store pinned configuration and a small regression suite |

## Supervisor and cleanup design

Use unique sandbox names derived from an owned run/instance record; never select a generic default box. The Sandboxie command interface supports selecting a box, waiting and box-scoped termination. Prefer the narrow operation and never terminate all sandboxes. The service may own launched processes, so native parent/child tracking alone is insufficient. [Sandboxie command interface](https://sandboxie-plus.github.io/sandboxie-docs/Content/StartCommandLine.html).

Require two proofs: the game reports the expected run identity, and the sandbox manager reports it belongs to the expected owned box. On cancellation, request normal game shutdown, allow the overview's grace period, then terminate the exact owned box if necessary. Export bounded evidence before resetting the box; mark unavailable if export/cleanup cannot be confirmed. Never delete a user's pre-existing sandbox or Steam installation.

Use a read-only immutable artifact input where supported and a narrow output export area. Restrict mappings/access to exact test paths; do not grant broad access to the home directory, browser profiles, source checkout or credentials. A sandbox with permissive resource exceptions must not be advertised as a security boundary for untrusted builds. Run trusted project artifacts only.

## Windows Sandbox alternative

Windows Sandbox is a different, disposable VM-backed product. Microsoft's current documentation says it does not support simultaneous multiple sandbox instances. Thus it could be evaluated as **one host plus one guest**, not an arbitrary N-client farm. Networking and mapped folders must be explicitly configured; read-only artifact mapping and a narrow result export are preferable. vGPU support/fallback does not guarantee game compatibility. [Windows Sandbox overview](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/), [Configuration](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file).

Use full VMs when persistent independent OS environments are required. Do not build a repeating Steam-login workflow around an ephemeral environment without first considering security, manual login burden and account state loss.

## Costs, acceptance and stop criteria

Expected benefits are lower OS overhead than full guests and less shared-save interference. Expected costs are compatibility maintenance, startup/export/reset handling, shared GPU pressure and potentially feature-specific licensing. No specific feature price or machine capacity is established here; verify the chosen edition before adoption and do not purchase anything in the spike.

Time-box the first feasibility attempt to one focused engineering day. Acceptance requires the baseline gameplay assertions, ten proposed clean reset cycles, no cross-instance state leakage, no unrelated process termination, and a documented exact configuration. Steam success must satisfy its separate gate; KCP success does not upgrade the evidence label. Stop immediately on required security/DRM/anti-cheat circumvention, unsupported client initialization or unbounded cleanup.

Proof boundary: no Sandboxie/Windows Sandbox software was installed, configured or run for this research. This document is a conditional plan, not a claim that either product currently works with the user's Steam game.
