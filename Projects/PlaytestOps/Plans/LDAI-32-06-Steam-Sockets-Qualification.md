# LDAI-32 Solution 06 — Steam Sockets End-to-end Qualification

Date: 2026-10-15  
Status: Required qualification lane before claiming Steam multiplayer support; proposed, no live session executed.  
Parent: [Overview and shared contract](LDAI-32-00-Local-Multiplayer-Overview.md).

## Decision and non-negotiable distinction

Use the shipping-intended Mirror Steam transport with real Steam clients, entitled accounts and a controlled test app/build. This lane verifies platform initialization, identity, lobby/discovery, transport connection and gameplay together. It cannot be replaced by passing KCP or a mocked Steam API.

The FizzySteamworks maintainer explicitly documents two machines, two Steam accounts and game ownership for P2P tests. Make that the supported qualification baseline. [Sandboxed clients](LDAI-32-04-Sandboxed-Processes.md) and [VMs](LDAI-32-05-VM-Isolated-Clients.md) are optional one-physical-machine feasibility routes; if unsupported, fall back to a second authorized device. Do not promise that two game instances under one Steam login can represent two authenticated users. [FizzySteamworks README](https://github.com/Chykary/FizzySteamworks).

## Current project-specific finding

Corporate Hell's inspected Unity project already contains FizzySteamworks 6.0.1, and `Assets/Scenes/MainMenu.unity` serializes `UseNextGenSteamNetworking: 1`. The installed `NextClient` uses `SteamNetworkingSockets.ConnectP2P` and `NextServer` uses `CreateListenSocketP2P`; server compilation selects the game-server API variant. Therefore do not propose “switch from old Steam networking to Sockets” as if that migration were still unconditionally required. Audit the actual integration and initialization instead.

This finding is source/scene evidence only. The same project's manager references lobby-member indexing when assigning Steam identity; use it as an explicit audit target, not a reusable authentication pattern. Bind the remote transport/auth identity to the actual Mirror connection. Do not revive the project's shelved transport-decoupling plan as already implemented.

Valve marks legacy `ISteamNetworking` deprecated; new work should use the intended modern Sockets/Messages API through a compatible Mirror transport. [Legacy API notice](https://partner.steamgames.com/doc/api/ISteamNetworking).

## Keep the layers separate

| Layer | Proposed evidence | What does not count |
|---|---|---|
| Artifact | Verified installed executable tree hash and build/branch metadata | Same filename or menu version string |
| Platform initialization | Correct AppID, successful initialization, expected local account alias | Overlay appearing alone |
| Identity/admission | Expected distinct remote identities bound to actual connections; configured auth policy succeeds | Nickname, supplied SteamID, lobby enumeration order |
| Lobby | Create/join result, intended lobby ID/metadata/owner, ready state | A visible row with an unverified join ID |
| Transport | Sockets branch, connection state, errors, route diagnostics | Mirror host's local client existing |
| Gameplay | Same authoritative and remote-observation assertions as KCP | Lobby membership alone |
| Relay/WAN | Captured transport route and environment showing the specific exercised path | “Allow relay” enabled, or local KCP success |

Steam lobbies are discovery/grouping objects; gameplay connection to the selected host/server is a distinct step. Design failures and logs for both. [Steam matchmaking and lobbies](https://partner.steamgames.com/doc/features/multiplayer/matchmaking).

## Initialization, identity and entitlement

Use the exact intended launch context: direct development executable, debugger, official Steam library build, or other explicitly documented mode. A Non-Steam Game shortcut is not equivalent to a properly installed app entitlement. Steam initialization requires the correct context; for direct development launches Valve documents `steam_appid.txt`, and explicitly says not to ship it. Keep development launcher configuration separate from a release depot and record it as runtime metadata. [Steamworks API overview](https://partner.steamgames.com/doc/sdk/api).

Use the project's own authorized test AppID/access. Do not adopt public Spacewar/AppID 480 as production qualification. Test accounts need access through legitimate developer/test distribution arrangements. Valve documents internal test access and Dev Comp packages; access/depot configuration still needs an authorized owner. [Testing on Steam](https://partner.steamgames.com/doc/store/testing).

Choose and document the actual admission policy. Transport-authenticated Steam identity and explicit session-ticket/ownership policy are related but not identical requirements. If the game uses session tickets, validate asynchronous responses and invalid/revoked outcomes, and end/cancel the sessions/tickets during cleanup. Do not treat a claimed ID or a preliminary synchronous result as completed ticket authentication. Avoid adding redundant authentication flows without a product requirement. [User authentication and ownership](https://partner.steamgames.com/doc/features/auth).

Store redacted account aliases in shared results, with an access-controlled local mapping if needed. Never log auth tickets, passwords, 2FA codes, API keys or raw credential-bearing process command lines. Manual login/2FA is an explicit prerequisite, not an automation failure to bypass.

## Transport and route specifics

`CreateListenSocketP2P`/`ConnectP2P` use Steam identity and virtual-port addressing; raw-IP calls are a different mode. The inspected Fizzy path uses virtual port zero. Do not assume the ordinary KCP port allocator can configure it, or that one identity can host several simultaneous test sessions through that unchanged adapter. Begin with one session per account/environment.

Capture supported connection information and diagnostics from the pinned API/transport. `GetDetailedConnectionStatus` is diagnostic text whose format can change; keep raw sanitized output and use stable structured fields where available. A forced/observed relay profile needs explicit evidence; a successful connection alone should not be relabeled “all NAT traversal tested.” [ISteamNetworkingSockets](https://partner.steamgames.com/doc/api/ISteamNetworkingSockets), [Steam Datagram Relay](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay).

Do not invent firewall exceptions or scrape undocumented route internals to force a result. If a path cannot be determined reliably, mark it `unknown`; if an explicit supported test configuration can require the target route, record that configuration and its observed outcome. Two guests on one physical network still do not cover independent home routers or geography.

## Implementation phases and DoD

| Phase | Work | Definition of done |
|---|---|---|
| A — Inventory | Pin Mirror, Steam wrapper, transport, Unity and native library versions; inspect serialized transport and bootstrap | Actual selected API path, role ownership, identity mapping and initialization owner documented |
| B — Platform preflight | Approved accounts, AppID, entitlement, launch mode and artifact hashes | Missing Steam, wrong account/app/entitlement or different artifact produce actionable preflight failure, not a gameplay pass |
| C — Two-peer transport | Direct connection by known host identity before lobby UI | Distinct identities and remote Mirror connection proved; reliable/unreliable messages and disconnect exercised |
| D — Lobby flow | Create/find/invite/join/ready/scene start/leave | Lobby ID and selected host propagate correctly; late/failed callbacks cannot join an obsolete run |
| E — Admission and lifecycle | Auth policy, room capacity, rejected participant, host exit and stale lobby | Invalid admission fails; cleanup ends sessions and leaves lobby; no duplicate or zombie player |
| F — Route/failure qualification | Supported relay diagnostics, Steam disruption, reconnect policy | Route label evidence-backed; outage/timeouts distinguish platform from gameplay failure; no silent KCP fallback |
| G — Environment comparison | Repeat on selected VM/sandbox only if needed, then second device/network when available | Environment limits recorded; same scenario and artifact compared; local-only result never presented as broad WAN proof |

## Listen host versus dedicated server

Start with the game's actual peer-host topology. A listen host uses a Steam user context; a Steam dedicated server uses separate game-server initialization/login/API requirements. The presence of `UNITY_SERVER` code in a transport does not prove all native calls, callbacks, authentication and teardown are correct in that mode. Qualify dedicated-server support as a separate capability after the host path works.

Headless KCP server acceptance does not automatically qualify a Steam game server. Use a distinct server artifact if necessary and record its hash/version. Keep secrets such as game-server login tokens out of repository/config manifests and logs; only introduce them through a separately authorized protected mechanism when the chosen server setup actually requires them.

## Orchestration, budgets and cleanup

Use a separate Steam preflight/setup deadline, initially proposed at 120 seconds, then bounded lobby/admission deadlines and the scenario's fixed wall-time budget. Automatic retries must not repeatedly create lobbies or churn accounts; one safe reattempt may be configured with a new run ID and preserved failure evidence. Never replace Steam with KCP after a platform failure and keep the same outcome label.

Steam may relaunch an installed application outside the worker's ordinary process tree. The child must still authenticate to the run, report its actual artifact and establish owned-process identity. Do not kill the user's Steam client to clean up a test. Stop the owned game, leave the test lobby, close transport connections, release any session tickets and verify termination. A lost guest can be stopped only if it is an explicitly owned test VM.

Accounts, OS environments, game entitlements and human login time are real operational costs even when no new cloud server is used. No account, entitlement, purchase, public lobby, invite or Steamworks configuration change is authorized by this research document.

## Acceptance matrix and proof boundary

Required positive cases: two entitled distinct users connect; correct remote ownership; expected lobby/ready flow; scene transition; gameplay replication; clean leave; supported late join/reconnect policy. Required negative cases: unavailable Steam, wrong build/protocol, rejected/expired admission where applicable, full lobby/server, host crash, stale lobby metadata, connection timeout and cancellation during join.

Each case records account aliases, AppID, artifact hashes, environment, transport branch, route evidence, participant/result counts and cleanup. Keep invite/overlay/manual UI checks separate from API-driven lobby checks. For private builds, external coordination or sending invites requires explicit authorization.

This research inspected source and official documentation only. It did not log into accounts, create lobbies, test relay routing, modify Steamworks settings, launch a game or validate multiplayer success.
