# Steam multiplayer streaming

## Build and select a Steam player

Connect the Unity Editor that owns the build, then use `/Builds` to create a Windows player with the `steamMultiplayer` Build Profile. After a successful build, open `/Streaming` and select that build. The page offers **Steam multiplayer** when the approved build advertises the `arena-steam` capability. If the build also supports local KCP multiplayer, choose **Steam multiplayer** from **Play mode**; a Steam-only build selects it automatically.

Choose 2–4 total players. The normal Steam session is the host; select exactly one available Sandboxie box for each additional player. For example, 3 total players requires 2 selected boxes. The page lists configured boxes with a matching Steam process running. That availability check does not prove the account is signed in. Sign in manually in every context; the Steam build must report distinct account identities and join the host lobby for game readiness. The page does not install Steam or Sandboxie, configure boxes, or collect credentials.

Once the session starts, use the existing player tiles to watch and control players. Click a tile to request control; press **Escape** to release control and unlock the mouse. Stream control and capture remain part of the existing `/Streaming` workflow.

## Local prerequisites and paths

Run the dashboard on Windows x64 with Steam and Sandboxie already installed. Before starting a Steam session, start Steam and sign in manually in the normal host session and in each Sandboxie box you want to use. Use a distinct Steam account in each context. Keep the Sandboxie boxes preconfigured; the dashboard reads their state but does not change their settings.

The server checks these default executable locations:

- Sandboxie: `C:\Program Files\Sandboxie\Start.exe`, then `C:\Program Files\Sandboxie-Plus\Start.exe`.
- Steam: `C:\Program Files (x86)\Steam\steam.exe`.

For a non-default installation, set the optional paths in the local dashboard configuration before starting the server. Do not commit machine-specific values:

```json
{
  "SteamSessions": {
    "SandboxieStartPath": "C:\\Program Files\\Sandboxie-Plus\\Start.exe",
    "SteamExecutablePath": "D:\\Apps\\Steam\\steam.exe"
  }
}
```

The same settings can be supplied as environment variables named `SteamSessions__SandboxieStartPath` and `SteamSessions__SteamExecutablePath`. These are executable paths only; sign-in and Steam account setup remain manual.

## Live validation — 2026-11-07

Approved build `d7eaa480876b4d1e959ac2b1a5154557` was streamed with two players: normal Steam as host and `DefaultBox` as the client. The accounts were distinct and both players joined the same Steam lobby. Both captures were 1280×720 at about 30 FPS.

On each tile, click control acquired input, the game requested automatic pointer lock, **Escape** released control, W/S movement was observed, and yellow gameplay tracers appeared after firing. The input exercise found no cross-routed acknowledgements, acknowledgement gaps, pending input packets, or runtime input errors.

Input-to-video measurements below are browser input dispatch to presented game frames, not network-only latency. Each role had 49 frame samples. The raw `latency-summary.json` is retained locally with the validation evidence; the recorded results are summarized here.

| Player | Input-to-video p50 | p95 | Max | FPS | Dropped / lost |
| --- | ---: | ---: | ---: | ---: | ---: |
| Host | 94.0 ms | 110.9 ms | 119.1 ms | 30 | 0 / 0 |
| Client | 97.6 ms | 121.1 ms | 126.3 ms | 30 | 0 / 0 |

Normal **Stop** returned exit code 0 for both players. Post-stop checks found no remaining game processes or private launch configuration files, and both Steam processes remained available. An exclusive write-open against the approved executable succeeded after stopping, confirming its build reservation was released. Launching the same approved build again after stopping succeeded. A final repeat forwarded W, A, S, D and shooting input to each player with mouse lock, zero pending acknowledgements, zero acknowledgement gaps and zero runtime input errors, then stopped both players cleanly.

A separate injected unexpected Sandboxie-client exit correctly failed and stopped the whole streaming group. Cleanup left zero game processes and zero private configuration files while preserving both Steam processes.

This live run covered two players only; four-player Steam streaming is fixture-tested but has not been qualified live. Backend verification passed 151 checks; Unity Edit Mode passed 112 SDK checks and 11 Steam configuration checks. The local Node VM suites passed 153 multistream, 131 input, 45 marker, and 18 Steam UI checks. Raw logs are retained locally and are not included in this repository; fixture results do not replace live Steam evidence.
