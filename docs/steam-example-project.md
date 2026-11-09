# External Unity Steam example — LDAI-35

This repository contains the PlaytestOps integration and this configuration/qualification record for [LDAI-35](https://awsomeleodai.atlassian.net/browse/LDAI-35). The playable Unity example is the separate **PlaytestOps Test** project, managed outside this Git repository in Unity Version Control. Its scenes, gameplay scripts, prefabs, third-party assets and generated players are not included here. Cloning this repository alone does not provide the playable example.

The project assets and package manifests below were inspected read-only on 2026-11-09. The live qualification summarized here was performed on 2026-11-07; this documentation audit did not rerun Unity or Steam. No Corporate Hell assets are part of this fixture.

## Project configuration

The inspected project records Unity **6000.6.0f1** in `ProjectSettings/ProjectVersion.txt`. Its `steamMultiplayer` Windows Build Profile, at `Assets/Settings/Build Profiles/steamMultiplayer.asset`, overrides the global scene list with these enabled scenes in order:

1. `Assets/SteamMultiplayer/Scenes/SteamArenaLobby.unity`
2. `Assets/SteamMultiplayer/Scenes/SteamArenaGame.unity`

The separate normal Windows/KCP profile remains available. The Steam scenes do not replace the KCP lobby or manager.

| Dependency | Inspected configuration |
| --- | --- |
| Mirror | Vendored under `Assets/ThirdParty/Mirror`; the fixture uses Mirror NetworkManager and the existing Arena player |
| FizzySteamworks | `https://github.com/Chykary/FizzySteamworks.git?path=/com.mirror.steamworks.net#2339a4a4742a8b09fbbb29fc555e5622a6e30a92` |
| Steamworks.NET | `https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#ba71581f1ed7349e8d0f17ddc6f135dd3bc8a6a3` |
| PlaytestOps Editor and runtime | Local package references to this checkout's `UnityPackages/com.playtestops.editor` and `UnityPackages/com.playtestops.runtime`; use the checkout's actual absolute paths on another machine |
| Input System | Project resolves `com.unity.inputsystem` 1.20.0 |
| WebRTC | Runtime package resolves `com.unity.webrtc` 3.0.0 |

The `PlaytestOps.SteamMultiplayer` assembly references `Mirror`, `FizzySteamworks`, `com.rlabrecque.steamworks.net`, `PlaytestOps.Arena`, `Unity.InputSystem` and `PlaytestOps.Runtime`.

## Scene and runtime responsibilities

The Steam lobby contains a `SteamArenaNetworkManager`, `SteamArenaLobbyUI`, and `Mirror.FizzySteam.FizzySteamworks` on the same manager object. Serialized manager settings use the two Steam scenes as offline/online scenes, the existing `Assets/PlaytestOpsArena/Prefabs/ArenaPlayer.prefab`, automatic player creation, background execution, persistence across scene loads, 30 Hz send rate and a maximum of four connections. Fizzy enables `UseNextGenSteamNetworking` and `AllowSteamRelay`; the component starts disabled until the manager initializes Steam successfully. Enabling relay support is not evidence that a particular live connection used a relay route.

`SteamArenaNetworkManager` owns Steam initialization, callbacks, friends-only lobby creation/joining, scene readiness and cleanup. `SteamArenaLobbyUI` provides Host, join-by-lobby-ID, invite, copy ID and Leave actions. Shared `ArenaPlayer` code owns movement, aiming, firing, damage, respawn and player cameras. Returning to the lobby does not rerun the process's one-shot `--steam-host` or `--steam-join <id>` startup request.

For dashboard launches, `SteamArenaStreamingSession` reads the private `--playtestops-steam-config-file` supplied by the backend, or its `PLAYTESTOPS_STEAM_CONFIG` fallback. It verifies role, group/session/instance IDs, development App ID, account and expected lobby identity. It publishes game readiness only after the local gameplay player exists and the host's lobby is joinable. The runtime SDK independently owns video capture, WebRTC, virtual input devices and input leases. The backend owns deletion of private launch configuration files after the player exits.

The fixture's `SteamArenaBuildAppId` postprocessor creates `steam_appid.txt` containing development App ID **480** beside a successful Windows player. It preserves an existing matching file and rejects a conflicting one. `SteamArenaStreamingBuildCapability` requires the Steam lobby as the first scene and the game scene to be included, then adds this adapter identity to the SDK marker:

```json
{
  "launchProfiles": ["arena-steam"],
  "steamAppId": 480,
  "steamAdapter": "steam-arena-v1"
}
```

These are additional fields in `playtestops-streaming.json`, not a complete replacement marker. The dashboard validates the approved build manifest and required artifact hashes before launching it.

## Use and qualification

With access to the external Unity project, select `steamMultiplayer` in Build Profiles, connect its Editor to PlaytestOps, and create a fresh Windows build from `/Builds`. Select that approved build under `/Streaming`, choose Steam multiplayer, and select one already configured Sandboxie box per joining player. For the qualified two-player setup, normal Steam hosts and `DefaultBox` joins. Both contexts must already be signed in to distinct Steam accounts; friendship/invitation rules still apply to the friends-only lobby. See [Steam streaming](steam-streaming.md) for prerequisites, UI behavior and configuration paths.

The 2026-11-07 live run established two distinct accounts, one shared Steam lobby, two spawned gameplay players, two 1280×720 streams at approximately 30 FPS, per-player browser input, movement and firing tracers. Escape released control. Normal stop exited both owned games with code zero, removed private launch configs, released the approved artifact reservation and preserved Steam. Relaunching the same approved build and cleanup after an owned peer exited were also exercised.

The recorded supporting checks were 151 backend fixtures, 112 runtime SDK Edit Mode tests, 11 Steam configuration tests and 347 frontend fixtures. Later integration checks also cover the local KCP latency feature; those are distinct from this two-account Steam result. See [Steam qualification and measurement limits](steam-streaming.md#live-validation--2026-11-07) and [KCP latency qualification](latency-inducer.md).

This record does not establish live four-account Steam support, WAN performance, a specific Steam relay route, host migration, production Steam authentication, or distribution of a complete Unity example through this repository. App ID 480 is the fixture's development identity. The PR provides the integration source and an auditable external-project configuration record; access to the separate Unity Version Control project remains necessary to reproduce the example itself.
