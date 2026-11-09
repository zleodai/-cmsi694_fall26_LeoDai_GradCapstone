# Local KCP latency inducer

**Status: implemented and live-validated on 2026-11-08.** Both requirements of [LDAI-36](https://awsomeleodai.atlassian.net/browse/LDAI-36) passed: actual host/client game traffic receives induced latency, and the dashboard changes it during a running playtest. Validation used the existing `/Streaming` page with one host and three KCP clients.

## Dashboard use

Start a local KCP multiplayer build, expand **Game network latency (KCP)**, enter the minimum and maximum added RTT, and select **Apply**. Select **Reset** to turn the delay off. The disclosure defaults to collapsed so the four video feeds retain space; its summary shows the confirmed target. Opening it allows the page to scroll while retaining useful video height.

Show a compact **Game network latency (KCP)** disclosure only while a supported `arena-kcp` session is active. It defaults collapsed and its summary shows the current applied target; opening it reveals minimum and maximum values in milliseconds, **Apply**, **Reset**, update status, and per-client telemetry. With the panel present, KCP video stages retain a 120 px minimum height so opening the controls does not collapse the four feeds. Other profiles keep the existing grid layout.

- **Pending target** — the submitted range while the dashboard awaits the server response.
- **Applied target** — the last range acknowledged as active by the inducer.
- **Measured game RTT** — runtime/game-network RTT with its sample time or freshness. Do not derive this from browser data-channel acknowledgements.

Keep the existing per-player **Input RTT** (browser data-channel acknowledgements) and **Input to video** marker measurements separate and unchanged. Disable edits during an in-flight apply/reset; do not display a requested value as applied until an acknowledgement arrives. On failure, preserve the last acknowledged value and show the failure. Reset requests the agreed neutral/no-injected-latency state and is itself pending until acknowledged.

The target is an **added RTT** range, defaulting to 50–100 ms, bounded to integer values from 0 to 1000 ms with minimum no greater than maximum. The relay samples a uniform delay from half the range for each direction, so actual game RTT also includes baseline network timing and game ticks. One setting applies to every remote KCP client; the host's local player has no UDP client link. Reset disables added delay while preserving the selected range.

The backend owns one loopback UDP relay per KCP remote client and the launcher redirects that client's KCP port through it. `/api/streaming/state` reports `networkLatency` only for the active, fully game-ready KCP group; otherwise it is `null`. `POST /api/streaming/groups/{groupId}/latency` accepts `{ enabled, minRttMs, maxRttMs }` and returns the authoritative `networkLatency` snapshot synchronously. A 200 response is applied confirmation; validation and group-state conflicts return 400/409. While that request is pending, only its originating page disables the controls.

Per-client relay links expose queue/drop/forward counters and optional game RTT and standard deviation from a recent matching runtime arena snapshot. The dashboard must show the sample time and must treat a missing, stale, future-dated, or invalid sample as unavailable. This measured game RTT remains separate from browser **Input RTT** acknowledgements and **Input to video** marker measurements.

The optional measured RTT comes from the KCP client's `arena-state.json` snapshot only when session/group/instance identities match and the snapshot declares game RTT available with finite values in range. Samples older than three seconds or more than one second in the future are unavailable; poll responses represent the latest three metrics (RTT, standard deviation, sample UTC), with nulls when no qualified sample exists. The dashboard displays missing measurements as N/A rather than estimating them from video or input data.

## Acceptance criteria

1. The control is available only for an active, ready KCP multiplayer session; it is unavailable for single-player, Steam, unsupported profiles, and inactive sessions.
2. The user can set a valid minimum/maximum target and apply it during a live session without restarting the game group.
3. Invalid ranges are rejected in the page before submission and by the server at the API boundary. Numeric bounds and defaults are agreed with the runtime/API contract before implementation.
4. Pending, acknowledged-applied, and measured game RTT are visibly separate. Delayed, failed, or stale responses cannot overwrite a newer apply/reset result.
5. Reset returns to the contract-defined neutral setting and reports completion only after acknowledgement.
6. Existing player selection, click-to-control, automatic pointer lock, Escape release, Steam box selection, Input RTT, and Input-to-video behavior remain unchanged.

## `KcpLatencyChecks` fixture responsibility

The focused backend fixture is one static `KcpLatencyChecks` class with `Run` and private `CheckBounds`, `Bind`, `Port`, `Send`, `Receive`, `Exchange`, and `Until` helpers; it has no nested helper classes. It owns only disposable loopback UDP echo sockets and relay instances. It does not launch Unity or Steam, or alter global socket/network settings. It creates an ephemeral server and relay, exchanges tagged datagrams, changes settings during the session, awaits cancellation/disposal, and verifies that the relay port can be reused.

The relay API uses `KcpLatencySettings(bool Enabled = false, int MinRttMs = 50, int MaxRttMs = 100)` and `KcpLatencyRelay(int serverPort, int maximumQueuedPackets = 1024, int maximumQueuedBytes = 4 * 1024 * 1024)`. Settings validate ranges; `Configure` returns a new snapshot and increments its revision. Snapshot values include settings, revision, queue and forward/drop counters, client-connected state, and stopped state. Applying/resetting retains the relay port; stopping discards queued datagrams and releases the socket. These behaviors have loopback/HTTP fixtures and the scoped live validation below.

`StreamingSupervisor.Group` owns each joining `Session.LatencyRelay`, creates it before launching the client, passes its port in the existing arena launch configuration, and disposes it during group completion, including exceptional completion. Build pins remain reserved until owned-child exit is confirmed. `StreamingLatencyState` is the resource-free group configuration/revision/link snapshot; `StreamingLatencyLinkState` carries one client's identity, counters, and optional RTT observation; `StreamingLatencyResult` carries the HTTP operation's state, error and status code. The supervisor creates these records for the endpoints, and the dashboard consumes them without owning sockets or processes.

The existing Unity `ArenaStreamingSession.Snapshot` adds `hasGameNetworkRtt`, `gameNetworkRttMs`, and `gameNetworkRttStdDevMs`. Connected remote clients populate them from `NetworkTime.rtt` and the square root of `NetworkTime.rttVariance`; the host reports no remote-client RTT. Existing atomic arena observations own their file lifecycle. The backend's `ReadGameRtt` reads these optional values without controlling the game, and rejects observations with wrong identities, invalid values, or stale timestamps.

Its 35 passing checks cover configuration validation; delay in both directions and summed RTT within scheduler tolerance; stable relay port across apply/reset; isolation between clients; bounded queues and drop counters; and shutdown/socket release. The class owns local socket/task state and collected timings/data.

## Validation results

The backend verification suite passed 222 checks, including 35 `KcpLatencyChecks` loopback-UDP checks and HTTP apply/change/reset/rejection, RTT snapshot parsing, and viewer/control/process-preservation fixtures. The backend build had zero warnings/errors. The full check log, machine-readable summary and exit code are retained locally; this section records their results.

Dashboard fixtures passed 174 multistream checks (including 21 latency/layout-specific cases), 131 input-safety checks, 45 marker lifecycle checks, and 18 Steam UI checks. Raw logs and the machine-readable summary are retained locally. These are deterministic fixtures; they do not prove that a Unity game exhibits the expected RTT or that apply/reset works during a live KCP streamed multiplayer session.

On 2026-11-08, a live four-player KCP session showed the three remote clients' measured RTT medians at 29.0/28.9/32.5 ms baseline, 113.3/112.7/111.3 ms with a 50–100 ms added range, 279.5/282.7/281.5 ms with a fixed 250 ms added range, then 41.0/26.2/39.2 ms after reset. These are medians of eight late-phase observations of Mirror's smoothed application RTT per client, not raw packet measurements. Relay drop counters remained zero during these phases. Player PIDs remained unchanged, and all four video feeds stayed near 30 FPS.

Live four-player input verification also passed with 50–100 ms added RTT: each player produced four WASD-down and four WASD-up actions, moved 1.8–2.2 world units, and fired three or four server-authoritative shots with replicated tracers. All four players saw all four players in the arena, with zero arena errors. Automatic pointer lock and Escape release worked for each player; there were zero input ACK gaps and zero runtime input errors. One checkpoint had two ping ACKs in flight; gameplay counters independently confirmed all movement and firing actions.

Normal Stop closed all four game processes with exit code 0, cleared latency state, released the approved executable's write pin, and allowed all four host/relay UDP ports to be rebound. Restarting the same approved build with four players succeeded. Apply and Reset worked again, including Reset while the draft range was invalid.

Six live layout cases covered expanded/collapsed controls at 996×1039, 1440×1000, and 800×900 CSS pixels. The player tiles did not overlap or overflow horizontally. Desktop video stages retained at least 120 px height, all four feeds fitted the collapsed desktop view, and mobile feeds flowed in one column.

The approved Test build was `7f1de12cb9564e32851583ad5e6a1d81`. The detailed validation report, screenshots, raw observations, generated build and cleanup records are retained locally and are not included in this repository. The dated results and qualification limits are summarized above.
