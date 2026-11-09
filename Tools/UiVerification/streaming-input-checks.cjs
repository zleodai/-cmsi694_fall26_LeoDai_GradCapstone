"use strict";

// Dependency-free checks against the real streaming input and viewer scripts.
// Run from the repository root: node Tools/UiVerification/streaming-input-checks.cjs
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.resolve(__dirname, "../..");
const inputFile = path.join(root, "PlaytestOps.Web/wwwroot/js/streaming-input.js");
const viewerFile = path.join(root, "PlaytestOps.Web/wwwroot/js/streaming.js");
const inputSource = fs.readFileSync(inputFile, "utf8");
const viewerSource = fs.readFileSync(viewerFile, "utf8");
let checks = 0;

function check(condition, description) {
    assert.ok(condition, description);
    checks++;
    process.stdout.write(`PASS ${description}\n`);
}

class FakeDataChannel {
    constructor() {
        this.readyState = "open";
        this.bufferedAmount = 0;
        this.bufferedAmountLowThreshold = 0;
        this.listeners = new Map();
        this.sent = [];
    }

    addEventListener(name, callback) {
        const callbacks = this.listeners.get(name) || [];
        callbacks.push(callback);
        this.listeners.set(name, callbacks);
    }

    removeEventListener(name, callback) {
        this.listeners.set(name, (this.listeners.get(name) || []).filter(existing => existing !== callback));
    }

    send(value) {
        this.sent.push(JSON.parse(value));
    }

    emit(name, event = {}) {
        for (const callback of [...(this.listeners.get(name) || [])]) callback(event);
    }
}

function createRig(options = {}) {
    let now = 0;
    let nextTimer = 1;
    const intervals = new Map();
    const window = {
        performance: { now: () => now },
        setInterval(callback, delay) {
            const id = nextTimer++;
            intervals.set(id, { callback, delay });
            return id;
        },
        clearInterval(id) { intervals.delete(id); }
    };
    vm.runInNewContext(inputSource, { window }, { filename: inputFile, timeout: 1000 });
    const createPeer = (peerOptions = {}) => {
        const statuses = [];
        const rtts = [];
        const errors = [];
        const input = new window.StreamingInput({
            diagnosticsEnabled: peerOptions.diagnosticsEnabled || (() => false),
            isInputActive: peerOptions.isInputActive,
            onStatus: message => statuses.push(message),
            onRtt: value => rtts.push(value),
            onInputError: message => errors.push(message)
        });
        const channel = new FakeDataChannel();
        const sessionId = peerOptions.sessionId || "session-test";
        const instanceId = peerOptions.instanceId || "instance-test";
        input.setIdentity(sessionId, instanceId);
        input.setChannel(channel);
        return { input, channel, statuses, rtts, errors, sessionId, instanceId };
    };
    return {
        ...createPeer(options),
        addPeer: createPeer,
        setNow(value) { now = value; },
        tick() { for (const timer of intervals.values()) timer.callback(); },
        timerCount() { return intervals.size; }
    };
}

function ack(rig, seq) {
    const packet = rig.channel.sent.find(value => value.seq === seq);
    assert.ok(packet, `sent packet ${seq} must exist`);
    rig.channel.emit("message", {
        data: JSON.stringify({ type: "input.ack", sessionId: rig.sessionId, instanceId: rig.instanceId, leaseId: packet.leaseId, seq, clientSentMs: packet.clientSentMs })
    });
}

const rig = createRig();
rig.input.setLease({ leaseId: "lease-buttons", leaseToken: "token-buttons" });
check(rig.channel.sent.length === 1 && rig.channel.sent[0].kind === "state", "control grant sends an initial state snapshot");

const buttonMasks = [1, 4, 2];
for (let button = 0; button < buttonMasks.length; button++) {
    check(rig.input.button(button, true, { x: 0.25, y: 0.75 }), `button ${button} press is accepted`);
    check(rig.channel.sent.at(-1).buttons === buttonMasks[button], `button ${button} uses the expected runtime mask`);
    check(rig.input.button(button, false, { x: 0.25, y: 0.75 }), `button ${button} release is accepted`);
    check(rig.channel.sent.at(-1).buttons === 0, `button ${button} release clears its mask`);
}
const sequences = rig.channel.sent.map(packet => packet.seq);
check(sequences.every((sequence, index) => sequence === index + 1), "input sequence numbers are strictly ordered without gaps");

const ackedSequence = rig.channel.sent[1].seq;
const pendingBeforeAck = rig.input.snapshot().pendingCount;
ack(rig, ackedSequence);
const afterFirstAck = rig.input.snapshot();
ack(rig, ackedSequence);
const afterDuplicateAck = rig.input.snapshot();
check(afterFirstAck.pendingCount === pendingBeforeAck - 1 && afterFirstAck.ackedCount === 1, "first acknowledgement removes one pending packet");
check(afterDuplicateAck.pendingCount === afterFirstAck.pendingCount && afterDuplicateAck.ackedCount === 1, "duplicate acknowledgement is ignored");

rig.input.key("KeyW", true);
rig.input.button(2, true);
const countBeforeReset = rig.channel.sent.length;
rig.input.releaseAll("Test release", true);
const releasePacket = rig.channel.sent.at(-1);
check(rig.channel.sent.length === countBeforeReset + 1 && releasePacket.kind === "reset", "releaseAll sends one reliable reset packet");
check(releasePacket.keys.length === 0 && releasePacket.buttons === 0, "release reset clears every held key and button");

const countBeforeLeaseRelease = rig.channel.sent.length;
rig.input.setLease(null);
check(!rig.input.key("KeyA", true) && !rig.input.button(0, true) && !rig.input.pointer({ x: 0.5, y: 0.5 }), "input calls are rejected after control lease release");
check(rig.channel.sent.length === countBeforeLeaseRelease, "no input packet is sent without an active control lease");

const pressure = createRig();
pressure.input.setLease({ leaseId: "lease-pressure", leaseToken: "token-pressure" });
pressure.channel.bufferedAmount = 64 * 1024 + 1;
check(!pressure.input.pointer({ x: 0.9, y: 0.9 }), "input is refused when the data channel exceeds the byte limit");
check(pressure.input.pausedForBackpressure && pressure.input.resetPending, "backpressure pauses input and defers the reset until the queue drains");
check(pressure.channel.sent.length === 1 && pressure.input.snapshot().pendingCount === 0, "backpressure clears pending events without adding more queued data");
check(!pressure.input.key("KeyW", true) && !pressure.input.button(0, true), "key and button input are refused while backpressure recovery is pending");
pressure.channel.bufferedAmount = 1024;
pressure.channel.emit("bufferedamountlow");
const pressureReset = pressure.channel.sent.at(-1);
check(pressureReset.kind === "reset" && pressureReset.keys.length === 0 && pressureReset.buttons === 0, "drained channel receives an empty reset before input resumes");
check(!pressure.input.pausedForBackpressure && !pressure.input.resetPending, "input resumes only after the reset is queued");
pressure.setNow(300);
pressure.tick();
const recoveryState = pressure.channel.sent.at(-1);
check(recoveryState.kind === "state" && recoveryState.keys.length === 0 && recoveryState.buttons === 0, "recovery snapshots contain no stale held input");

const bounded = createRig();
bounded.input.setLease({ leaseId: "lease-pending-limit", leaseToken: "token-pending-limit" });
for (let index = 0; index < 63; index++) {
    assert.equal(bounded.input.key(`Key${index}`, true), true);
}
check(bounded.input.snapshot().pendingCount === 64, "unacknowledged input queue reaches its configured packet cap");
check(!bounded.input.key("OverflowKey", true), "input is refused when the acknowledgement queue is full");
check(bounded.input.snapshot().pendingCount <= 1 && bounded.channel.sent.at(-1).kind === "reset", "full acknowledgement queue is cleared and replaced with a reset");

let activeTile = "host";
const pair = createRig({ sessionId: "session-host", instanceId: "instance-host", isInputActive: () => activeTile === "host" });
const client = pair.addPeer({ sessionId: "session-client", instanceId: "instance-client", isInputActive: () => activeTile === "client" });
pair.input.setLease({ leaseId: "lease-host", leaseToken: "token-host" });
client.input.setLease({ leaseId: "lease-client", leaseToken: "token-client" });
check(pair.timerCount() === 2, "two inputs coexist in one window with independent timers");
check(pair.input.key("KeyW", true) && pair.input.button(0, true), "the active tile accepts its own key and button input");
check(!client.input.key("KeyW", true) && !client.input.button(0, true) && !client.input.pointer({ dx: 17, dy: -8 }), "inactive tile rejects keys, buttons and pointer-lock deltas");
check(client.channel.sent.length === 1 && client.input.pressedKeys.size === 0 && client.input.buttons === 0, "inactive tile input rejection does not mutate or transmit held state");
check(pair.channel.sent.every(packet => packet.sessionId === pair.sessionId && packet.instanceId === pair.instanceId && packet.leaseId === "lease-host"), "host input packets retain only the host tile identity and lease");
const clientPending = client.input.snapshot().pendingCount;
const clientState = client.channel.sent[0];
client.channel.emit("message", { data: JSON.stringify({ type: "input.ack", sessionId: pair.sessionId, instanceId: pair.instanceId, leaseId: clientState.leaseId, seq: clientState.seq, clientSentMs: clientState.clientSentMs }) });
check(client.input.snapshot().pendingCount === clientPending && client.input.snapshot().ackedCount === 0, "another tile's identity cannot acknowledge this tile even when sequence and timestamp match");
pair.input.releaseAll("Focus switched to client", true);
const hostReset = pair.channel.sent.at(-1);
activeTile = "client";
check(hostReset.kind === "reset" && hostReset.keys.length === 0 && hostReset.buttons === 0, "focus switch explicitly neutralizes the old tile before routing input to the new tile");
check(client.input.key("KeyD", true) && client.input.button(2, true) && client.input.pointer({ dx: 9, dy: -4 }), "newly active tile accepts its own keyboard, button and relative pointer input");
const hostCount = pair.channel.sent.length;
check(!pair.input.key("KeyA", true) && !pair.input.button(2, true) && !pair.input.pointer({ dx: 9, dy: -4 }) && pair.channel.sent.length === hostCount, "old tile cannot react to the newly active tile's input or pointer lock");
client.channel.bufferedAmount = 64 * 1024 + 1;
client.input.pointer({ dx: 1 });
check(client.input.pausedForBackpressure && !pair.input.pausedForBackpressure, "backpressure pauses only the affected tile");
pair.setNow(1100);
pair.tick();
check(pair.channel.sent.at(-1).kind === "ping" && pair.channel.sent.at(-1).keys.length === 0 && pair.channel.sent.at(-1).buttons === 0, "inactive leased tile retains neutral heartbeat snapshots without accepting user input");
const hostSnapshot = JSON.stringify(pair.input.snapshot());
client.channel.bufferedAmount = 0;
client.channel.emit("bufferedamountlow");
check(client.channel.sent.at(-1).kind === "reset" && JSON.stringify(pair.input.snapshot()) === hostSnapshot, "one tile's reset recovery leaves the other tile's counters and queue unchanged");
client.input.dispose();
check(pair.timerCount() === 1 && pair.input.hasControl(), "disposing one tile leaves the other timer and lease intact");

const renewal = createRig();
const renewLease = { leaseId: "lease-renew", leaseToken: "token-renew", expiresUtc: "first" };
renewal.input.setLease(renewLease);
renewal.input.key("KeyW", true);
renewal.input.button(0, true);
const renewalCount = renewal.channel.sent.length;
const renewalSequence = renewal.input.sequence;
const renewalPending = renewal.input.snapshot().pendingCount;
renewal.input.setLease({ ...renewLease, expiresUtc: "later" });
check(renewal.input.pressedKeys.has("KeyW") && renewal.input.buttons === 1 && renewal.input.lease.expiresUtc === "later", "same-lease renewal preserves held keys/buttons and updates lease metadata");
check(renewal.channel.sent.length === renewalCount && renewal.input.sequence === renewalSequence && renewal.input.snapshot().pendingCount === renewalPending, "same-lease renewal sends no false neutral state and preserves sequencing and acknowledgements");
renewal.input.setChannel(renewal.channel);
check(renewal.input.pressedKeys.has("KeyW") && renewal.input.snapshot().pendingCount === renewalPending, "reattaching the current channel is idempotent");
renewal.channel.bufferedAmount = 64 * 1024 + 1;
renewal.input.pointer({ dx: 1 });
renewal.input.setLease({ ...renewLease, expiresUtc: "latest" });
check(renewal.input.pausedForBackpressure && renewal.input.resetPending, "same-lease renewal cannot bypass pending neutralization after backpressure");
renewal.input.setIdentity("session-new", "instance-new");
check(!renewal.input.hasControl() && !renewal.input.pausedForBackpressure && !renewal.input.resetPending && renewal.input.snapshot().pendingCount === 0, "identity replacement clears the old lease, retry queue and backpressure recovery state");
renewal.channel.bufferedAmount = 0;
renewal.input.setLease({ leaseId: "lease-new", leaseToken: "token-new" });
check(renewal.channel.sent.at(-1).sessionId === "session-new" && renewal.channel.sent.at(-1).instanceId === "instance-new" && renewal.channel.sent.at(-1).keys.length === 0, "new identity starts with its own neutral state");

const stale = createRig();
stale.input.setLease({ leaseId: "lease-stale", leaseToken: "token-stale" });
stale.input.key("KeyW", true);
const oldChannel = stale.channel;
const oldListeners = new Map([...oldChannel.listeners].map(([name, listeners]) => [name, [...listeners]]));
const replacement = new FakeDataChannel();
stale.input.setChannel(replacement);
stale.channel = replacement;
check([...oldChannel.listeners.values()].every(listeners => listeners.length === 0), "channel replacement detaches every old channel listener");
check(stale.input.pressedKeys.size === 0 && stale.input.buttons === 0 && stale.input.snapshot().pendingCount === 0, "channel replacement drops old held inputs and retries locally");
stale.input.key("KeyA", true);
const replacementPacket = replacement.sent.at(-1);
const replacementBefore = JSON.stringify(stale.input.snapshot());
const statusBefore = stale.statuses.length;
const errorsBefore = stale.errors.length;
for (const name of ["open", "close", "error", "bufferedamountlow"]) {
    oldChannel.emit(name);
    for (const listener of oldListeners.get(name)) listener({});
}
for (const listener of oldListeners.get("message")) {
    listener({ data: JSON.stringify({ type: "input.ack", sessionId: stale.sessionId, instanceId: stale.instanceId, leaseId: replacementPacket.leaseId, seq: replacementPacket.seq, clientSentMs: replacementPacket.clientSentMs }) });
    listener({ data: JSON.stringify({ type: "input.error", sessionId: stale.sessionId, instanceId: stale.instanceId, leaseId: replacementPacket.leaseId, reason: "unauthorized_input" }) });
}
check(stale.input.pressedKeys.has("KeyA") && JSON.stringify(stale.input.snapshot()) === replacementBefore, "already-queued old-channel callbacks cannot clear held input or acknowledge the replacement queue");
check(stale.statuses.length === statusBefore && stale.errors.length === errorsBefore, "stale channel callbacks cannot update replacement status or report input errors");
replacement.bufferedAmount = 64 * 1024 + 1;
stale.input.pointer({ dx: 1 });
replacement.bufferedAmount = 0;
const beforeOldLow = replacement.sent.length;
for (const listener of oldListeners.get("bufferedamountlow")) listener({});
check(replacement.sent.length === beforeOldLow && stale.input.resetPending, "old channel drain callback cannot flush a new channel's reset");
replacement.emit("bufferedamountlow");
check(replacement.sent.at(-1).kind === "reset" && !stale.input.resetPending, "current channel drain callback still completes neutral recovery");
const closingListeners = new Map([...replacement.listeners].map(([name, listeners]) => [name, [...listeners]]));
stale.input.dispose();
const disposedSnapshot = JSON.stringify(stale.input.snapshot());
const disposedStatuses = stale.statuses.length;
const disposedErrors = stale.errors.length;
const disposedPackets = replacement.sent.length;
for (const listener of closingListeners.get("message")) listener({ data: JSON.stringify({ type: "input.error", sessionId: stale.sessionId, instanceId: stale.instanceId, leaseId: replacementPacket.leaseId }) });
for (const name of ["open", "close", "error", "bufferedamountlow"]) for (const listener of closingListeners.get(name)) listener({});
stale.input.dispose();
stale.input.setIdentity("late-session", "late-instance");
stale.input.setLease({ leaseId: "late-lease", leaseToken: "late-token" });
stale.input.setChannel(oldChannel);
check(stale.timerCount() === 0 && !stale.input.hasControl() && stale.input.channel === null, "disposed tile cannot restart a timer, channel or control lease");
check(JSON.stringify(stale.input.snapshot()) === disposedSnapshot && stale.statuses.length === disposedStatuses && stale.errors.length === disposedErrors && replacement.sent.length === disposedPackets, "disposal is idempotent and suppresses every saved channel callback");

const rollover = createRig();
rollover.input.setLease({ leaseId: "lease-before", leaseToken: "token-before" });
const oldLeasePacket = rollover.channel.sent.at(-1);
rollover.input.setLease({ leaseId: "lease-after", leaseToken: "token-after" });
const newLeasePacket = rollover.channel.sent.at(-1);
check(oldLeasePacket.seq === 1 && newLeasePacket.seq === 1 && oldLeasePacket.clientSentMs !== newLeasePacket.clientSentMs, "lease sequence restarts retain unique echoed timestamps even within one clock tick");
for (const clientSentMs of [undefined, null, "0", oldLeasePacket.clientSentMs]) {
    rollover.channel.emit("message", { data: JSON.stringify({ type: "input.ack", sessionId: rollover.sessionId, instanceId: rollover.instanceId, leaseId: newLeasePacket.leaseId, seq: 1, clientSentMs }) });
}
check(rollover.input.snapshot().pendingCount === 1 && rollover.input.snapshot().ackedCount === 0, "missing, nonnumeric or previous-lease ACK timestamps cannot consume the new lease's packet");
rollover.channel.emit("message", { data: JSON.stringify({ type: "input.ack", sessionId: rollover.sessionId, instanceId: rollover.instanceId, leaseId: newLeasePacket.leaseId, seq: 1, clientSentMs: newLeasePacket.clientSentMs }) });
check(rollover.input.snapshot().pendingCount === 0 && rollover.input.snapshot().ackedCount === 1, "matching sequence and echoed timestamp acknowledge the current lease packet");

const throwing = createRig({ diagnosticsEnabled: () => true });
throwing.input.setLease({ leaseId: "lease-throw", leaseToken: "token-throw" });
ack(throwing, 1);
let throwAttempts = 0;
const workingSend = throwing.channel.send.bind(throwing.channel);
throwing.channel.send = () => { throwAttempts++; throw new Error("send failed"); };
check(!throwing.input.key("KeyW", true) && throwAttempts === 1, "throwing channel send returns refusal without recursively sending resets");
check(throwing.input.pausedForBackpressure && throwing.input.resetPending && throwing.input.pressedKeys.size === 0 && throwing.input.snapshot().pendingCount === 0, "send failure leaves a bounded paused queue with neutral local state");
check(throwing.input.diagnosticSentAt.size === 0, "failed sends do not create marker observations for packets that never left the viewer");
throwing.setNow(250);
throwing.tick();
check(throwAttempts === 2 && throwing.input.snapshot().pendingCount === 0, "a failed reset is retried at most once per recovery tick");
throwing.channel.send = workingSend;
throwing.tick();
check(throwing.channel.sent.at(-1).kind === "reset" && throwing.channel.sent.at(-1).keys.length === 0 && !throwing.input.pausedForBackpressure, "send recovery queues one neutral reset before accepting user input again");
check(throwing.input.key("KeyD", true), "input resumes after successful reset recovery");

const markers = createRig({ diagnosticsEnabled: () => true });
markers.input.setLease({ leaseId: "lease-markers", leaseToken: "token-markers" });
ack(markers, 1);
markers.setNow(10);
markers.input.pointer({ dx: 1 });
const firstMarker = markers.channel.sent.at(-1).diagnosticId;
ack(markers, markers.channel.sent.at(-1).seq);
markers.setNow(20);
markers.input.pointer({ dx: 1 });
const secondMarker = markers.channel.sent.at(-1).diagnosticId;
ack(markers, markers.channel.sent.at(-1).seq);
markers.setNow(30);
markers.input.pointer({ dx: 1 });
const thirdMarker = markers.channel.sent.at(-1).diagnosticId;
ack(markers, markers.channel.sent.at(-1).seq);
markers.setNow(60);
check(markers.input.markDiagnosticPresented(secondMarker, 55, "expectedDisplayTime") === 35, "presented marker latency uses the selected display timestamp");
const markerSnapshot = markers.input.snapshot();
check(markerSnapshot.supersededMarkerCount === 1 && markerSnapshot.expiredMarkerCount === 0 && markers.input.readDiagnostic(firstMarker) === null && markers.input.readDiagnostic(thirdMarker) === 30, "presenting a newer marker counts only older unpresented IDs as superseded");
check(markerSnapshot.frameSamples.length === 1 && markerSnapshot.frameSamples[0].timestampSource === "expectedDisplayTime" && markerSnapshot.frameSamples[0].atMs === 60, "marker samples preserve the timestamp source and ring observation time");
check(markers.input.markDiagnosticPresented(secondMarker, 70) === null && markers.input.markDiagnosticPresented(0, 70) === null && markers.input.snapshot().frameSamples.length === 1, "duplicate and unknown markers never create latency samples or double-count superseded markers");
markers.setNow(10031);
markers.input.pointer({ dx: 1 });
ack(markers, markers.channel.sent.at(-1).seq);
check(markers.input.snapshot().expiredMarkerCount === 1 && markers.input.readDiagnostic(thirdMarker) === null, "marker TTL pruning counts expired unpresented observations separately from packet loss");
markers.input.diagnosticSentAt.clear();
for (let index = 0; index < 257; index++) {
    markers.setNow(11000 + index);
    markers.input.pointer({ dx: 1 });
    ack(markers, markers.channel.sent.at(-1).seq);
}
check(markers.input.diagnosticSentAt.size === 256 && markers.input.snapshot().expiredMarkerCount === 2, "marker tracking remains bounded at 256 and counts size eviction as expired");
check(markers.input.snapshot().frameSamples.length === 1 && markers.input.snapshot().ackedCount === markers.channel.sent.length, "marker pruning neither invents frame samples nor changes successful packet acknowledgement counts");
markers.input.diagnosticSentAt.clear();
for (let index = 0; index < 101; index++) {
    markers.setNow(12000 + index * 2);
    markers.input.pointer({ dx: 1 });
    const packet = markers.channel.sent.at(-1);
    ack(markers, packet.seq);
    markers.input.markDiagnosticPresented(packet.diagnosticId, packet.clientSentMs + 1);
}
const frameRing = markers.input.snapshot().frameSamples;
check(frameRing.length === 100 && frameRing.every(sample => sample.timestampSource === "callback" && Number.isFinite(sample.atMs)), "frame sample ring remains bounded and defaults timestamp source to callback");
const disabledMarkers = createRig();
disabledMarkers.input.setLease({ leaseId: "lease-no-markers", leaseToken: "token-no-markers" });
disabledMarkers.input.pointer({ dx: 1 });
check(disabledMarkers.channel.sent.every(packet => packet.diagnosticId === undefined) && disabledMarkers.input.markDiagnosticPresented(1) === null && disabledMarkers.input.snapshot().frameSamples.length === 0 && disabledMarkers.input.snapshot().supersededMarkerCount === 0 && disabledMarkers.input.snapshot().expiredMarkerCount === 0, "disabled diagnostics produce no marker IDs, latency samples or marker pruning counters");

const markerPair = createRig({ sessionId: "marker-host", instanceId: "marker-host-instance", diagnosticsEnabled: () => true });
const markerClient = markerPair.addPeer({ sessionId: "marker-client", instanceId: "marker-client-instance", diagnosticsEnabled: () => true });
markerPair.input.setLease({ leaseId: "marker-host-lease", leaseToken: "marker-host-token" });
markerClient.input.setLease({ leaseId: "marker-client-lease", leaseToken: "marker-client-token" });
// Force an ID collision to prove tile-local tracking rather than relying on random seeds.
markerPair.input.diagnosticId = markerClient.input.diagnosticId = 50;
markerPair.setNow(10);
markerPair.input.pointer({ dx: 1 });
markerClient.input.pointer({ dx: 2 });
markerPair.setNow(60);
markerPair.input.markDiagnosticPresented(51, 55, "expectedDisplayTime");
check(markerClient.input.readDiagnostic(51) === 10 && markerClient.input.snapshot().frameSamples.length === 0 && markerPair.input.snapshot().frameSamples.length === 1, "matching marker IDs in different tiles cannot consume each other's diagnostic observations");
markerClient.input.markDiagnosticPresented(51, 70);
check(markerClient.input.snapshot().frameSamples[0].ms === 60 && markerPair.input.snapshot().frameSamples[0].ms === 45, "each tile computes marker latency from its own presentation callback");

function emitInputMessage(target, message) {
    target.channel.emit("message", { data: JSON.stringify({ sessionId: target.sessionId, instanceId: target.instanceId, leaseId: target.input.lease?.leaseId, ...message }) });
}

function diagnosticRig() {
    const target = createRig();
    target.input.setLease({ leaseId: "diagnostic-lease", leaseToken: "PRIVATE-LEASE-TOKEN-DO-NOT-RECORD" });
    return target;
}

const rejectedAcks = diagnosticRig();
const expectedTimestamp = rejectedAcks.channel.sent[0].clientSentMs;
rejectedAcks.setNow(100);
emitInputMessage(rejectedAcks, { type: "input.ack", seq: 999, clientSentMs: 12 });
let rejection = rejectedAcks.input.snapshot().lastRejectedAck;
check(rejection.type === "ack-rejected" && rejection.reason === "no_pending_entry" && rejection.seq === 999 && rejection.atMs === 100 && rejection.expectedClientSentMs === null && rejection.echoedClientSentMs === 12 && rejection.deltaMs === null, "unknown ACK records safe sequence and timestamp context without an expected packet");
check(rejection.pendingAgeMs === null && rejection.retries === null && rejection.hasControl === true, "unknown ACK reports no fabricated packet age or retry count");
for (const clientSentMs of [undefined, null, "PRIVATE-ACK-TIMESTAMP", Infinity])
    emitInputMessage(rejectedAcks, { type: "input.ack", seq: 1, clientSentMs });
rejection = rejectedAcks.input.snapshot().lastRejectedAck;
check(rejection.reason === "invalid_client_timestamp" && rejection.expectedClientSentMs === expectedTimestamp && rejection.echoedClientSentMs === null && rejection.deltaMs === null && rejection.pendingAgeMs === 100 && rejection.retries === 0, "invalid ACK timestamps remain null in diagnostics while preserving pending packet context");
rejectedAcks.setNow(125);
emitInputMessage(rejectedAcks, { type: "input.ack", seq: 1, clientSentMs: expectedTimestamp + .125 });
rejection = rejectedAcks.input.snapshot().lastRejectedAck;
check(rejection.reason === "client_timestamp_mismatch" && rejection.expectedClientSentMs === expectedTimestamp && rejection.echoedClientSentMs === expectedTimestamp + .125 && rejection.deltaMs === .125 && rejection.atMs === 125, "mismatched ACK records exact echoed-minus-expected delta without changing timestamp comparison behavior");
const rejectionState = rejectedAcks.input.snapshot();
check(rejectionState.ackRejectedCount === 6 && rejectionState.ackRejectionReasons.no_pending_entry === 1 && rejectionState.ackRejectionReasons.invalid_client_timestamp === 4 && rejectionState.ackRejectionReasons.client_timestamp_mismatch === 1, "ACK rejection counters distinguish all three exact reasons");
check(rejectionState.pendingCount === 1 && rejectionState.ackedCount === 0 && rejectionState.lastAckedSeq === 0 && rejectionState.recoveryCount === 0 && rejectionState.resetCount === 0 && rejectionState.refusalCount === 0 && rejectionState.resendCount === 0, "rejected ACK diagnostics do not consume pending input, trigger recovery or alter existing counters");
ack(rejectedAcks, 1);
ack(rejectedAcks, 1);
check(rejectedAcks.input.snapshot().ackedCount === 1 && rejectedAcks.input.snapshot().pendingCount === 0 && rejectedAcks.input.snapshot().ackRejectedCount === 7 && rejectedAcks.input.snapshot().lastRejectedAck.reason === "no_pending_entry", "accepted ACK still counts once and duplicate ACK is only recorded as a rejection");
const beforeForeignMessage = JSON.stringify(rejectedAcks.input.snapshot());
emitInputMessage(rejectedAcks, { type: "input.ack", sessionId: "wrong-session", seq: 999, clientSentMs: 0 });
emitInputMessage(rejectedAcks, { type: "input.error", instanceId: "wrong-instance", reason: "queue_overflow" });
check(JSON.stringify(rejectedAcks.input.snapshot()) === beforeForeignMessage, "foreign session and instance messages cannot pollute transport diagnostics");

const pendingDiagnostic = diagnosticRig();
for (let index = 0; index < 63; index++) pendingDiagnostic.input.key("DiagnosticKey" + index, true);
pendingDiagnostic.setNow(200);
pendingDiagnostic.input.key("DiagnosticOverflow", true);
let recovery = pendingDiagnostic.input.snapshot().lastRecovery;
check(recovery.type === "recovery" && recovery.reason === "pending_limit" && recovery.atMs === 200 && recovery.pendingCount === 64 && recovery.bufferedAmount === 0 && recovery.seq === null && recovery.retries === null, "pending-limit recovery captures the full queue before clearing it");
check(pendingDiagnostic.input.snapshot().recoveryCount === 1 && pendingDiagnostic.input.snapshot().recoveryReasons.pending_limit === 1 && pendingDiagnostic.input.snapshot().pendingCount === 1 && pendingDiagnostic.input.snapshot().resetCount === 1, "pending-limit instrumentation preserves one neutral recovery reset and existing counters");

const bufferedDiagnostic = diagnosticRig();
bufferedDiagnostic.channel.bufferedAmount = 65537;
bufferedDiagnostic.input.pointer({ dx: 1 });
recovery = bufferedDiagnostic.input.snapshot().lastRecovery;
check(recovery.reason === "channel_buffered" && recovery.pendingCount === 1 && recovery.bufferedAmount === 65537 && bufferedDiagnostic.input.snapshot().recoveryReasons.channel_buffered === 1 && bufferedDiagnostic.input.snapshot().pendingCount === 0, "byte-limit recovery records buffered bytes and the pending queue before it is cleared");

const failedDiagnostic = diagnosticRig();
failedDiagnostic.input.key("KeyW", true);
failedDiagnostic.channel.send = () => { throw new Error("PRIVATE-SEND-EXCEPTION-DO-NOT-RECORD"); };
failedDiagnostic.input.key("KeyA", true);
recovery = failedDiagnostic.input.snapshot().lastRecovery;
check(recovery.reason === "send_failed" && recovery.pendingCount === 2 && recovery.bufferedAmount === 0 && failedDiagnostic.input.snapshot().recoveryCount === 1 && failedDiagnostic.input.snapshot().pendingCount === 0 && failedDiagnostic.input.pausedForBackpressure, "initial send failure records its distinct recovery reason without recursion or replay");
check(!JSON.stringify(failedDiagnostic.input.snapshot()).includes("PRIVATE-SEND-EXCEPTION"), "recovery diagnostics never include raw send exception text");

const retryDiagnostic = diagnosticRig();
const retryAt = time => { retryDiagnostic.setNow(time); retryDiagnostic.input.retryPending(time); };
retryAt(499);
check(retryDiagnostic.channel.sent.length === 1 && retryDiagnostic.input.snapshot().resendCount === 0, "diagnostic instrumentation does not retry before 500 ms");
retryAt(500);
retryAt(999);
check(retryDiagnostic.channel.sent.length === 2 && retryDiagnostic.input.snapshot().resendCount === 1, "first retry occurs at 500 ms and is not repeated before its next interval");
retryAt(1000);
retryAt(1499);
check(retryDiagnostic.channel.sent.length === 3 && retryDiagnostic.input.snapshot().resendCount === 2 && retryDiagnostic.input.snapshot().recoveryCount === 0, "second retry occurs at 1000 ms without early recovery");
retryAt(1500);
recovery = retryDiagnostic.input.snapshot().lastRecovery;
check(recovery.reason === "retry_exhausted" && recovery.seq === 1 && recovery.retries === 2 && recovery.pendingCount === 1 && recovery.atMs === 1500 && retryDiagnostic.input.snapshot().resendCount === 2 && retryDiagnostic.channel.sent.at(-1).kind === "reset", "retry exhaustion keeps the original two-retry limit and records the exhausted packet before clearing");
check(retryDiagnostic.channel.sent.slice(0, 3).every(packet => packet.seq === 1 && packet.clientSentMs === expectedTimestamp), "retries preserve the original packet sequence and echoed timestamp");

const retryFailure = diagnosticRig();
const successfulRetrySend = retryFailure.channel.send.bind(retryFailure.channel);
let retryAttempts = 0;
retryFailure.channel.send = value => {
    if (++retryAttempts === 1) throw new Error("PRIVATE-RETRY-EXCEPTION");
    successfulRetrySend(value);
};
retryFailure.setNow(500);
retryFailure.input.retryPending(500);
recovery = retryFailure.input.snapshot().lastRecovery;
check(recovery.reason === "retry_send_failed" && recovery.seq === 1 && recovery.retries === 0 && recovery.pendingCount === 1 && retryFailure.input.snapshot().resendCount === 0 && retryFailure.input.snapshot().recoveryReasons.retry_send_failed === 1 && retryFailure.channel.sent.at(-1).kind === "reset", "retry send failure is distinguished from initial send failure and retains pre-clear retry context");

const runtimeDiagnostic = diagnosticRig();
const runtimeReasons = ["unauthorized_input", "queue_overflow", "application_timeout", "lease_expired", "input_timeout", "input_devices_disabled", "application_backpressure", "stale_input", "invalid_input", "stale_sequence", "sequence_gap", "state_required"];
for (const [index, reason] of runtimeReasons.entries()) {
    runtimeDiagnostic.setNow(600 + index);
    emitInputMessage(runtimeDiagnostic, { type: "input.error", reason, seq: index + 1, expectedSeq: index + 2 });
}
let runtimeState = runtimeDiagnostic.input.snapshot();
check(runtimeReasons.every(reason => runtimeState.runtimeErrorReasons[reason] === 1) && runtimeState.runtimeErrorCount === runtimeReasons.length, "runtime error diagnostics retain every allowlisted enum reason and the original error counter");
check(runtimeState.lastRuntimeError.type === "runtime-error" && runtimeState.lastRuntimeError.reason === "state_required" && runtimeState.lastRuntimeError.seq === 12 && runtimeState.lastRuntimeError.expectedSeq === 13 && runtimeState.lastRuntimeError.hasControl === true && runtimeState.lastRuntimeError.atMs === 611, "runtime error event records safe numeric context and whether control was held");
check(runtimeState.recoveryCount === runtimeReasons.length && runtimeState.recoveryReasons.runtime_error === runtimeReasons.length && runtimeState.lastRecovery.reason === "runtime_error" && runtimeState.lastRecovery.pendingCount === 1, "runtime-triggered recovery records its call-site reason before dropping pending input");
check(runtimeState.diagnosticEvents.slice(-2).map(event => event.type).join(",") === "runtime-error,recovery", "runtime error and resulting recovery appear in their actual diagnostic order");
runtimeDiagnostic.channel.bufferedAmount = NaN;
emitInputMessage(runtimeDiagnostic, {
    type: "input.error", reason: "PRIVATE-ARBITRARY-ERROR-REASON", seq: "PRIVATE-SEQUENCE", expectedSeq: { token: "PRIVATE-EXPECTED-SEQUENCE" },
    leaseToken: "PRIVATE-LEASE-TOKEN-DO-NOT-RECORD", message: "PRIVATE-RUNTIME-MESSAGE", stack: "PRIVATE-RUNTIME-STACK"
});
runtimeState = runtimeDiagnostic.input.snapshot();
check(runtimeState.lastRuntimeError.reason === "unknown" && runtimeState.lastRuntimeError.seq === null && runtimeState.lastRuntimeError.expectedSeq === null && runtimeState.lastRuntimeError.hasControl === true && runtimeState.runtimeErrorReasons.unknown === 1 && runtimeState.lastRecovery.bufferedAmount === null, "current-lease unknown runtime reasons and nonnumeric context are reduced to safe enum/null values");
check(!JSON.stringify(runtimeState).includes("PRIVATE-") && !Object.hasOwn(runtimeState.lastRuntimeError, "message") && !Object.hasOwn(runtimeState.lastRuntimeError, "stack"), "diagnostic snapshots contain no lease token, arbitrary reason, raw message or stack text");

emitInputMessage(runtimeDiagnostic, { type: "input.ack", seq: "PRIVATE-ACK-SEQUENCE", clientSentMs: "PRIVATE-ACK-ECHO" });
check(runtimeDiagnostic.input.snapshot().lastRejectedAck.seq === null && runtimeDiagnostic.input.snapshot().lastRejectedAck.echoedClientSentMs === null && !JSON.stringify(runtimeDiagnostic.input.snapshot()).includes("PRIVATE-"), "rejected ACKs sanitize arbitrary sequence and timestamp payloads");
const detachedDiagnostics = runtimeDiagnostic.input.snapshot();
const diagnosticsBeforeMutation = JSON.stringify(runtimeDiagnostic.input.snapshot());
detachedDiagnostics.ackRejectionReasons.injected = 999;
detachedDiagnostics.recoveryReasons.injected = 999;
detachedDiagnostics.runtimeErrorReasons.injected = 999;
detachedDiagnostics.lastRejectedAck.reason = "mutated";
detachedDiagnostics.lastRecovery.reason = "mutated";
detachedDiagnostics.lastRuntimeError.reason = "mutated";
detachedDiagnostics.diagnosticEvents[0].reason = "mutated";
detachedDiagnostics.diagnosticEvents.push({ reason: "mutated" });
check(JSON.stringify(runtimeDiagnostic.input.snapshot()) === diagnosticsBeforeMutation, "snapshot reason maps, last-event objects and every ring event are detached from internal diagnostics");
for (let index = 0; index < 40; index++) {
    runtimeDiagnostic.setNow(1000 + index);
    emitInputMessage(runtimeDiagnostic, { type: "input.ack", seq: 100 + index, clientSentMs: 50 });
}
runtimeState = runtimeDiagnostic.input.snapshot();
check(runtimeState.diagnosticEvents.length === 32 && runtimeState.diagnosticEvents[0].seq === 108 && runtimeState.diagnosticEvents.at(-1).seq === 139 && runtimeState.diagnosticEvents[0].atMs === 1008 && runtimeState.diagnosticEvents.at(-1).atMs === 1039, "combined diagnostic ring retains exactly its latest 32 events in order");
check(runtimeState.ackRejectedCount === 41 && runtimeState.ackRejectionReasons.no_pending_entry === 41 && runtimeState.runtimeErrorCount === 13 && runtimeState.recoveryCount === 13, "ring eviction preserves lifetime rejection, runtime-error and recovery counters");
check(runtimeState.diagnosticEvents.every(event => ["ack-rejected", "recovery", "runtime-error"].includes(event.type) && Number.isFinite(event.atMs) && (event.seq === null || Number.isFinite(event.seq))), "diagnostic ring events expose only the agreed event types and numeric-or-null sequence context");
runtimeDiagnostic.input.setIdentity("fresh-diagnostic-session", "fresh-diagnostic-instance");
const resetDiagnostics = runtimeDiagnostic.input.snapshot();
check(resetDiagnostics.ackRejectedCount === 0 && resetDiagnostics.recoveryCount === 0 && resetDiagnostics.runtimeErrorCount === 0 && Object.keys(resetDiagnostics.ackRejectionReasons).length === 0 && Object.keys(resetDiagnostics.recoveryReasons).length === 0 && Object.keys(resetDiagnostics.runtimeErrorReasons).length === 0 && resetDiagnostics.lastRejectedAck === null && resetDiagnostics.lastRecovery === null && resetDiagnostics.lastRuntimeError === null && resetDiagnostics.diagnosticEvents.length === 0, "identity replacement resets all new diagnostic counters, reasons, last events and ring entries");

function adjacentPositiveDouble(value, direction) {
    const bytes = new ArrayBuffer(8);
    const view = new DataView(bytes);
    view.setFloat64(0, value);
    view.setBigUint64(0, view.getBigUint64(0) + BigInt(direction));
    return view.getFloat64(0);
}

for (const [label, echoed, accepted] of [
    ["exact timestamp", 0, true],
    ["positive inclusive tolerance boundary", 1e-6, true],
    ["negative inclusive tolerance boundary", -1e-6, true],
    ["one ULP above positive tolerance", adjacentPositiveDouble(1e-6, 1), false],
    ["one ULP beyond negative tolerance", -adjacentPositiveDouble(1e-6, 1), false]
]) {
    const tolerance = diagnosticRig();
    emitInputMessage(tolerance, { type: "input.ack", seq: 1, clientSentMs: echoed });
    const result = tolerance.input.snapshot();
    check(accepted
        ? result.pendingCount === 0 && result.ackedCount === 1 && result.ackRejectedCount === 0
        : result.pendingCount === 1 && result.ackedCount === 0 && result.ackRejectedCount === 1 && result.lastRejectedAck.reason === "client_timestamp_mismatch",
    "current-lease ACK " + label + (accepted ? " is accepted" : " remains rejected"));
}

for (const direction of [-1, 1]) {
    const unityRoundTrip = createRig();
    unityRoundTrip.setNow(123456.789);
    unityRoundTrip.input.setLease({ leaseId: "unity-roundtrip-lease", leaseToken: "unity-roundtrip-token" });
    const packet = unityRoundTrip.channel.sent[0];
    const echoed = adjacentPositiveDouble(packet.clientSentMs, direction);
    assert.notEqual(echoed, packet.clientSentMs);
    emitInputMessage(unityRoundTrip, { type: "input.ack", seq: packet.seq, clientSentMs: echoed });
    check(unityRoundTrip.input.snapshot().ackedCount === 1 && unityRoundTrip.input.snapshot().pendingCount === 0 && unityRoundTrip.input.snapshot().diagnosticEvents.length === 0,
        "current-lease ACK tolerates an adjacent-double Unity round-trip in direction " + direction);
}

for (const [label, expected, echoed] of [
    ["live viewer", 64059.700000047684, 64059.70000004768],
    ["Unity probe", 914514.1977637607, 914514.1977637608]
]) {
    const observedRoundTrip = createRig();
    observedRoundTrip.setNow(expected);
    observedRoundTrip.input.setLease({ leaseId: "observed-roundtrip-lease", leaseToken: "observed-roundtrip-token" });
    assert.equal(observedRoundTrip.channel.sent[0].clientSentMs, expected);
    assert.notEqual(echoed, expected);
    emitInputMessage(observedRoundTrip, { type: "input.ack", seq: 1, clientSentMs: echoed });
    const result = observedRoundTrip.input.snapshot();
    check(result.ackedCount === 1 && result.pendingCount === 0 && result.ackRejectedCount === 0 && result.recoveryCount === 0,
        "recorded " + label + " timestamp round-trip acknowledges without rejection or recovery");
}

for (const literal of ["1e309", "-1e309", "null", '"NaN"']) {
    const nonfinite = diagnosticRig();
    const payload = JSON.stringify({ type: "input.ack", sessionId: nonfinite.sessionId, instanceId: nonfinite.instanceId,
        leaseId: nonfinite.input.lease.leaseId, seq: 1, clientSentMs: "TIMESTAMP_LITERAL" }).replace('"TIMESTAMP_LITERAL"', literal);
    nonfinite.channel.emit("message", { data: payload });
    const result = nonfinite.input.snapshot();
    check(result.pendingCount === 1 && result.ackedCount === 0 && result.ackRejectedCount === 1 && result.lastRejectedAck.reason === "invalid_client_timestamp" && result.lastRejectedAck.echoedClientSentMs === null,
        "current-lease ACK rejects nonfinite or nonnumeric wire timestamp " + literal);
}

const leaseScoped = createRig();
leaseScoped.input.setLease({ leaseId: "old-reply-lease", leaseToken: "old-reply-token" });
leaseScoped.input.setLease({ leaseId: "current-reply-lease", leaseToken: "current-reply-token" });
leaseScoped.input.key("KeyW", true);
leaseScoped.input.button(0, true);
const currentReplyPacket = leaseScoped.channel.sent.at(-1);
const currentBefore = JSON.stringify(leaseScoped.input.snapshot());
const currentPending = JSON.stringify([...leaseScoped.input.pending.values()]);
const currentSentCount = leaseScoped.channel.sent.length;
const currentStatusCount = leaseScoped.statuses.length;
const currentErrorCount = leaseScoped.errors.length;
for (const leaseId of [undefined, null, "wrong-reply-lease", "old-reply-lease"]) {
    emitInputMessage(leaseScoped, { type: "input.ack", leaseId, seq: currentReplyPacket.seq, clientSentMs: currentReplyPacket.clientSentMs });
    emitInputMessage(leaseScoped, { type: "input.error", leaseId, seq: currentReplyPacket.seq, expectedSeq: 1, reason: "unauthorized_input" });
}
check(JSON.stringify(leaseScoped.input.snapshot()) === currentBefore && JSON.stringify([...leaseScoped.input.pending.values()]) === currentPending,
    "missing, null, wrong and previous-lease ACK/error replies cannot alter current pending packets or any diagnostic counters");
check(leaseScoped.input.pressedKeys.has("KeyW") && leaseScoped.input.buttons === 1 && !leaseScoped.input.pausedForBackpressure && leaseScoped.channel.sent.length === currentSentCount && leaseScoped.statuses.length === currentStatusCount && leaseScoped.errors.length === currentErrorCount,
    "foreign-lease replies cannot clear held input, send resets or invoke status/error callbacks");
emitInputMessage(leaseScoped, { type: "input.ack", seq: currentReplyPacket.seq, clientSentMs: currentReplyPacket.clientSentMs });
check(leaseScoped.input.snapshot().pendingCount === 2 && leaseScoped.input.snapshot().ackedCount === 1 && leaseScoped.input.pressedKeys.has("KeyW") && leaseScoped.input.buttons === 1,
    "matching current-lease ACK still acknowledges its packet without changing held input");
emitInputMessage(leaseScoped, { type: "input.error", seq: currentReplyPacket.seq, expectedSeq: 4, reason: "queue_overflow" });
const currentError = leaseScoped.input.snapshot();
check(currentError.runtimeErrorCount === 1 && currentError.runtimeErrorReasons.queue_overflow === 1 && currentError.recoveryCount === 1 && currentError.recoveryReasons.runtime_error === 1 && currentError.lastRuntimeError.hasControl === true && currentError.lastRecovery.pendingCount === 2,
    "matching current-lease runtime error still records safe diagnostics and pre-clear recovery context");
check(leaseScoped.errors.length === currentErrorCount + 1 && leaseScoped.input.pressedKeys.size === 0 && leaseScoped.input.buttons === 0 && leaseScoped.channel.sent.at(-1).kind === "reset",
    "matching current-lease runtime error still clears held input and invokes recovery callbacks");
const releasedReplyPacket = leaseScoped.channel.sent.at(-1);
leaseScoped.input.setLease(null);
const withoutLeaseBefore = JSON.stringify(leaseScoped.input.snapshot());
const withoutLeaseStatuses = leaseScoped.statuses.length;
const withoutLeaseErrors = leaseScoped.errors.length;
const withoutLeaseSent = leaseScoped.channel.sent.length;
emitInputMessage(leaseScoped, { type: "input.ack", leaseId: releasedReplyPacket.leaseId, seq: releasedReplyPacket.seq, clientSentMs: releasedReplyPacket.clientSentMs });
emitInputMessage(leaseScoped, { type: "input.error", leaseId: releasedReplyPacket.leaseId, seq: releasedReplyPacket.seq, reason: "lease_expired" });
check(!leaseScoped.input.hasControl() && JSON.stringify(leaseScoped.input.snapshot()) === withoutLeaseBefore && leaseScoped.statuses.length === withoutLeaseStatuses && leaseScoped.errors.length === withoutLeaseErrors && leaseScoped.channel.sent.length === withoutLeaseSent,
    "ACK/error replies after local lease revocation are ignored before state, counters or callbacks change");

function functionSlice(source, start, end) {
    const from = source.indexOf(start);
    const to = source.indexOf(end, from + start.length);
    assert.ok(from >= 0 && to > from, `could not locate ${start}`);
    return source.slice(from, to);
}

const offerBody = functionSlice(viewerSource, "async function receiveOffer(", "async function receiveIce(");
const iceBody = functionSlice(viewerSource, "async function receiveIce(", "function sendViewer(");
check(/const pc = context\.pc/.test(offerBody) && /await pc\.setRemoteDescription\([\s\S]*?if \(!isCurrentViewer\(context\)\) return;/.test(offerBody), "offer negotiation captures its peer and checks generation after setting the remote description");
check(/await pc\.createAnswer\(\);\s*if \(!isCurrentViewer\(context\)\) return;/.test(offerBody), "offer negotiation cannot create an answer after its viewer context becomes stale");
check(/await pc\.setLocalDescription\(answer\);\s*if \(!isCurrentViewer\(context\)[^\n]*return;/.test(offerBody), "offer negotiation checks generation after setting the local description");
check(/await pc\.addIceCandidate\(candidate\);\s*if \(!isCurrentViewer\(context\)\) return;/.test(iceBody), "ICE completion is ignored when its viewer context becomes stale");

process.stdout.write(`SUCCESS: ${checks} streaming UI safety checks\n`);
