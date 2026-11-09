# PlaytestOps Runtime Streaming

Optional player-side framebuffer video, virtual keyboard/mouse input, and bounded runtime log forwarding for a compatible PlaytestOps dashboard. It is separate from the Editor integration.

## Install and requirements

For the dashboard source in this repository, install both packages from the **same checkout**. In Unity's Package Manager, choose **Install package from disk** and select these manifests in order:

1. `<checkout>/UnityPackages/com.playtestops.editor/package.json`
2. `<checkout>/UnityPackages/com.playtestops.runtime/package.json`

Run the dashboard from that checkout and create a fresh player build after the packages resolve. Existing players retain the runtime version they were built with; changing the installed package does not update an old executable.

The following Git URL is the **historical v0.5.1 release**, not the installation source for this checkout's current streaming protocol:

```text
https://github.com/zleodai/PlaytestOps-UnityPackage.git?path=/Packages/com.playtestops.runtime#v0.5.1
```

The manifest minimum is Unity 6000.0; earlier Unity 6 versions still require independent compatibility validation. Current streaming support is limited to **Windows x64 players**, a real graphics device, and a visible player window. macOS, Linux, mobile and headless players are not supported.

The published historical tag predates lease-scoped input replies and the current Steam integration. Use the disk installation above with this dashboard; an older player build cannot qualify the updated protocol.

Dependencies are resolved from Unity's registry: WebRTC 3.0.0, Input System 1.14.0 or a compatible project-selected version, and the Screen Capture, IMGUI, and JSON Serialization engine modules. Set **Player Settings > Other Settings > Configuration > Active Input Handling** to **Input System Package (New)** or **Both** and restart Unity when prompted. Installing the dependency alone does not enable it; Windows builds are rejected when Input System is disabled.

## Use

1. Install the separate PlaytestOps Editor package and connect it to a compatible local dashboard.
2. Build a Windows x64 player from the dashboard. The package writes `playtestops-streaming.json` beside supported output so the dashboard can validate streaming capability.
3. On the dashboard's **Streaming** page, select that build, choose its player count and window resolution, and start the streams. Default launch size is 1280 x 720 per player; video uses each player's actual framebuffer, not desktop capture.
4. Click a tile to take control. Its mouse locks automatically when the game requests mouse look. Press Escape to release control and unlock, then click any tile to resume. The old runtime must acknowledge consumed neutral input before the next runtime can receive a lease. Stop the whole session when finished.

Normal player launches do not start streaming. Runtime startup requires explicit, session-scoped configuration supplied by the dashboard through `PLAYTESTOPS_STREAM_CONFIG`. That configuration is consumed at startup; do not place credentials in source control or command-line arguments. Runtime signaling accepts loopback server origins only.

## Local KCP arena integration

The PlaytestOps Test adapter supports **Host + 1 client**, **Host + 2 clients** and **Host + 3 clients**. Every participant has its own rendered Windows process and WebRTC connection. Four players use a responsive 2 x 2 dashboard grid; resizing the dashboard scales the displayed video while retaining each requested capture resolution.

This is an opt-in game integration. The game's build postprocessor adds `launchProfiles: ["arena-kcp"]` to the capability marker. The supervisor supplies `PLAYTESTOPS_ARENA_CONFIG` with the group's identities, role/index, player count, local KCP address/port and private instance storage. The game adapter starts Mirror/KCP, calls `StreamingGameReadiness.MarkReady("host")` or `MarkReady("client")` only after joining its lobby, and readies/starts the exact requested group through the game's normal lobby rules. The supervisor waits for host readiness before launching joining clients.

Each input ACK/error carries its originating `leaseId`. The viewer matches the current lease and pending sequence; echoed timestamp differences up to 0.000001 milliseconds accommodate JSON numeric roundtrip precision. Replies from an older lease cannot reset or acknowledge the current controller. Runtime input remains restricted to the dedicated virtual devices exposed by `StreamingInput`.

The runtime reports each game's mouse-lock intent through authenticated `runtime.cursor` messages. By default it follows `Cursor.lockState == CursorLockMode.Locked`. Games that keep native windows unlocked while streaming can call `StreamingInput.SetMouseLockRequested(true)` for gameplay and `false` for menus; passing `null` restores native cursor-state tracking. This preference does not grant input control. The browser still requires a user click and may deny pointer-lock permission; Escape cancels control even while a lease grant is pending.

## Limits

- Virtual input uses Unity's Input System, not legacy `UnityEngine.Input` or operating-system input injection. Games that restrict Input Actions to particular devices/users may need an explicit integration.
- Input uses a separate reliable ordered WebRTC data channel; reliability does not guarantee zero delay or a game action for every input during stalls/disconnection.
- A diagnostic input-to-video marker is optional and visible in the captured framebuffer when enabled. Disable it for ordinary viewing.
- Runtime log capture is bounded and may truncate during an error flood. Logs can contain sensitive game data.
- The generic runtime package does not create a game's multiplayer lobby. Multiplayer orchestration requires the explicit adapter above. It does not provide public remote access, dashboard authentication or NAT/TURN hosting.
