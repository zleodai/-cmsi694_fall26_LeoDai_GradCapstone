(function (global) {
    "use strict";

    const MAX_BUFFERED_BYTES = 64 * 1024;
    const LOW_BUFFERED_BYTES = 16 * 1024;
    const MAX_PENDING_ACKS = 64;
    const MAX_RETRIES = 2;
    const ACK_RETRY_MS = 500;
    const ACK_TIMESTAMP_TOLERANCE_MS = 0.000001;
    const INPUT_TICK_MS = 250;
    const PING_INTERVAL_MS = 1000;
    const MAX_DIAGNOSTIC_EVENTS = 32;
    // Store only known protocol reasons, never arbitrary runtime text or credentials.
    const RUNTIME_ERROR_REASONS = new Set([
        "queue_overflow", "application_timeout", "lease_expired", "input_timeout",
        "input_devices_disabled", "application_backpressure", "unauthorized_input",
        "stale_input", "invalid_input", "stale_sequence", "sequence_gap", "state_required"
    ]);
    const RECOVERY_REASONS = new Set([
        "pending_limit", "channel_buffered", "send_failed", "retry_exhausted",
        "retry_send_failed", "runtime_error"
    ]);
    const finiteOrNull = value => Number.isFinite(value) ? value : null;
    // PointerEvent.button is left, middle, right, back, forward; the runtime
    // snapshots use Unity Input System order: left, right, middle, back, forward.
    const DOM_BUTTON_MASKS = Object.freeze([1, 4, 2, 8, 16]);

    class StreamingInput {
        constructor(options) {
            this.options = options;
            this.sessionId = "";
            this.instanceId = "";
            this.channel = null;
            this.channelListeners = null;
            this.disposed = false;
            this.lease = null;
            this.sequence = 0;
            this.lastClientSentMs = -1;
            this.diagnosticId = this.randomDiagnosticSeed();
            this.lastPingAt = 0;
            this.pressedKeys = new Set();
            this.buttons = 0;
            this.pointerX = 0.5;
            this.pointerY = 0.5;
            this.pending = new Map();
            this.diagnosticSentAt = new Map();
            this.resetPending = false;
            this.pausedForBackpressure = false;
            this.latestRttMs = null;
            this.metrics = {
                lastSentSeq: 0,
                lastAckedSeq: 0,
                ackedCount: 0,
                ackGaps: 0,
                resetCount: 0,
                refusalCount: 0,
                resendCount: 0,
                runtimeErrorCount: 0,
                ackRejectedCount: 0,
                ackRejectionReasons: {},
                lastRejectedAck: null,
                recoveryCount: 0,
                recoveryReasons: {},
                lastRecovery: null,
                runtimeErrorReasons: {},
                lastRuntimeError: null,
                diagnosticEvents: [],
                supersededMarkerCount: 0,
                expiredMarkerCount: 0,
                rttSamples: [],
                frameSamples: []
            };
            this.tickHandle = global.setInterval(() => this.tick(), INPUT_TICK_MS);
        }

        setIdentity(sessionId, instanceId) {
            if (this.disposed) return;
            this.sessionId = sessionId;
            this.instanceId = instanceId;
            this.lease = null;
            this.sequence = 0;
            this.diagnosticId = this.randomDiagnosticSeed();
            this.pressedKeys.clear();
            this.buttons = 0;
            this.pointerX = 0.5;
            this.pointerY = 0.5;
            this.pending.clear();
            this.diagnosticSentAt.clear();
            this.resetPending = false;
            this.pausedForBackpressure = false;
            this.lastPingAt = 0;
            this.latestRttMs = null;
            this.metrics = {
                lastSentSeq: 0,
                lastAckedSeq: 0,
                ackedCount: 0,
                ackGaps: 0,
                resetCount: 0,
                refusalCount: 0,
                resendCount: 0,
                runtimeErrorCount: 0,
                ackRejectedCount: 0,
                ackRejectionReasons: {},
                lastRejectedAck: null,
                recoveryCount: 0,
                recoveryReasons: {},
                lastRecovery: null,
                runtimeErrorReasons: {},
                lastRuntimeError: null,
                diagnosticEvents: [],
                supersededMarkerCount: 0,
                expiredMarkerCount: 0,
                rttSamples: [],
                frameSamples: []
            };
            this.options.onRtt(null);
        }

        setChannel(channel) {
            if (this.disposed || this.channel === channel) return;
            if (this.channel && this.channelListeners) {
                for (const [name, listener] of this.channelListeners) this.channel.removeEventListener(name, listener);
            }
            this.releaseAll(null, false);
            this.channel = channel;
            this.channelListeners = null;
            this.pending.clear();
            this.resetPending = false;
            this.pausedForBackpressure = false;
            if (!channel) return;
            const listeners = new Map();
            this.channelListeners = listeners;
            const listen = (name, callback) => {
                const listener = event => {
                    if (!this.disposed && this.channel === channel && this.channelListeners === listeners) callback(event);
                };
                listeners.set(name, listener);
                channel.addEventListener(name, listener);
            };
            channel.bufferedAmountLowThreshold = LOW_BUFFERED_BYTES;
            listen("open", () => this.options.onStatus("Input channel ready."));
            listen("close", () => {
                this.releaseAll("Input channel closed", false);
                this.options.onStatus("Input channel closed. Reconnect before sending controls.");
            });
            listen("error", () => this.options.onStatus("Input channel error. Input is paused."));
            listen("bufferedamountlow", () => this.flushReset());
            listen("message", event => this.onMessage(event));
        }

        setLease(lease) {
            if (this.disposed) return;
            const newLease = !!lease && this.lease?.leaseId !== lease.leaseId;
            this.lease = lease;
            if (lease && !newLease) return;
            this.pressedKeys.clear();
            this.buttons = 0;
            this.resetPending = false;
            this.pausedForBackpressure = false;
            if (!lease) {
                this.pending.clear();
            } else if (newLease) {
                this.pending.clear();
                this.sequence = 0;
                this.diagnosticSentAt.clear();
                this.metrics.lastSentSeq = 0;
                this.metrics.lastAckedSeq = 0;
                this.lastPingAt = 0;
            }
            if (lease) {
                this.options.onStatus("Manual control active. Use the video player to send input.");
                this.send("state", { x: this.pointerX, y: this.pointerY, keys: [], buttons: 0 });
            }
        }

        hasControl() {
            return !this.disposed && !!this.lease && !!this.sessionId && !!this.instanceId;
        }

        isInputActive() {
            return this.hasControl() && (!this.options.isInputActive || this.options.isInputActive());
        }

        key(code, down) {
            if (!this.isInputActive() || !code) return false;
            if (this.pausedForBackpressure) {
                this.metrics.refusalCount++;
                return false;
            }
            const wasDown = this.pressedKeys.has(code);
            if (wasDown === down) return true;
            if (down) this.pressedKeys.add(code);
            else this.pressedKeys.delete(code);
            if (!this.send("key", { code, down, keys: [...this.pressedKeys], buttons: this.buttons })) {
                if (!this.pausedForBackpressure) {
                    if (down) this.pressedKeys.delete(code);
                    else this.pressedKeys.add(code);
                }
                return false;
            }
            return true;
        }

        pointer(fields) {
            if (!this.isInputActive()) return false;
            const x = Number.isFinite(fields.x) ? fields.x : this.pointerX;
            const y = Number.isFinite(fields.y) ? fields.y : this.pointerY;
            const sent = this.send("pointer", {
                x,
                y,
                dx: fields.dx || 0,
                dy: fields.dy || 0,
                wheelX: fields.wheelX || 0,
                wheelY: fields.wheelY || 0,
                buttons: this.buttons,
                keys: [...this.pressedKeys]
            });
            if (sent) {
                this.pointerX = x;
                this.pointerY = y;
            }
            return sent;
        }

        button(button, down, point) {
            if (!this.isInputActive() || button < 0 || button > 4) return false;
            if (this.pausedForBackpressure) {
                this.metrics.refusalCount++;
                return false;
            }
            const bit = DOM_BUTTON_MASKS[button];
            const next = down ? (this.buttons | bit) : (this.buttons & ~bit);
            if (next === this.buttons) return true;
            if (Number.isFinite(point?.x)) this.pointerX = point.x;
            if (Number.isFinite(point?.y)) this.pointerY = point.y;
            this.buttons = next;
            if (!this.send("pointer", {
                x: this.pointerX,
                y: this.pointerY,
                dx: 0,
                dy: 0,
                buttons: this.buttons,
                keys: [...this.pressedKeys]
            })) {
                if (!this.pausedForBackpressure) this.buttons = down ? (this.buttons & ~bit) : (this.buttons | bit);
                return false;
            }
            return true;
        }

        releaseAll(reason, attemptSend = true) {
            const hadState = this.pressedKeys.size > 0 || this.buttons !== 0;
            this.pressedKeys.clear();
            this.buttons = 0;
            if (attemptSend && this.hasControl() && (hadState || reason)) {
                if (!this.send("reset", { keys: [], buttons: 0 }, { allowWhilePaused: true })) {
                    this.resetPending = true;
                    this.pausedForBackpressure = true;
                }
            }
            if (reason) this.options.onStatus(`${reason}; all held inputs were cleared.`);
        }

        acknowledgeAll() {
            this.pending.clear();
        }

        dispose() {
            if (this.disposed) return;
            global.clearInterval(this.tickHandle);
            this.releaseAll("Viewer closed", true);
            this.lease = null;
            this.setChannel(null);
            this.pending.clear();
            this.diagnosticSentAt.clear();
            this.disposed = true;
        }

        send(kind, fields = {}, options = {}) {
            const channel = this.channel;
            if (!this.hasControl() || !channel || channel.readyState !== "open") {
                this.metrics.refusalCount++;
                return false;
            }
            if (this.pausedForBackpressure && !options.allowWhilePaused) {
                this.metrics.refusalCount++;
                return false;
            }
            if (channel.bufferedAmount > MAX_BUFFERED_BYTES || this.pending.size >= MAX_PENDING_ACKS) {
                this.metrics.refusalCount++;
                this.enterBackpressure(true, channel.bufferedAmount > MAX_BUFFERED_BYTES ? "channel_buffered" : "pending_limit");
                return false;
            }

            // The echoed timestamp also disambiguates a delayed ACK after lease sequence
            // numbers restart. Keep it unique even within one reduced-precision clock tick.
            const clientSentMs = Math.max(global.performance.now(), this.lastClientSentMs + 0.001);
            const nextSequence = this.sequence + 1;
            const packet = {
                type: "input",
                sessionId: this.sessionId,
                instanceId: this.instanceId,
                leaseId: this.lease.leaseId,
                leaseToken: this.lease.leaseToken,
                seq: nextSequence,
                clientSentMs,
                kind,
                ...fields
            };
            if (this.options.diagnosticsEnabled() && kind !== "state" && kind !== "ping") {
                packet.diagnosticId = this.nextDiagnosticId();
            }

            try {
                channel.send(JSON.stringify(packet));
            } catch {
                this.metrics.refusalCount++;
                this.enterBackpressure(false, "send_failed");
                return false;
            }
            this.lastClientSentMs = clientSentMs;
            if (packet.diagnosticId !== undefined) {
                this.diagnosticSentAt.set(packet.diagnosticId, clientSentMs);
                this.trimDiagnostics(clientSentMs);
            }
            this.sequence = nextSequence;
            this.metrics.lastSentSeq = nextSequence;
            if (kind === "reset") this.metrics.resetCount++;
            this.pending.set(nextSequence, {
                packet,
                sentAt: clientSentMs,
                nextRetryAt: clientSentMs + ACK_RETRY_MS,
                retries: 0,
                kind
            });
            return true;
        }

        tick() {
            if (!this.hasControl()) return;
            const now = global.performance.now();
            this.retryPending(now);
            if (this.pausedForBackpressure) {
                this.flushReset();
                return;
            }
            this.send("state", { x: this.pointerX, y: this.pointerY, keys: [...this.pressedKeys], buttons: this.buttons });
            if (now - this.lastPingAt >= PING_INTERVAL_MS) {
                if (this.send("ping", { keys: [...this.pressedKeys], buttons: this.buttons })) this.lastPingAt = now;
            }
        }

        retryPending(now) {
            if (!this.channel || this.channel.readyState !== "open") return;
            for (const [seq, entry] of this.pending) {
                if (now < entry.nextRetryAt) continue;
                if (entry.retries >= MAX_RETRIES || this.channel.bufferedAmount > MAX_BUFFERED_BYTES) {
                    this.enterBackpressure(true, entry.retries >= MAX_RETRIES ? "retry_exhausted" : "channel_buffered", { seq, retries: entry.retries });
                    return;
                }
                try {
                    this.channel.send(JSON.stringify(entry.packet));
                } catch {
                    this.enterBackpressure(true, "retry_send_failed", { seq, retries: entry.retries });
                    return;
                }
                entry.retries++;
                this.metrics.resendCount++;
                entry.nextRetryAt = now + ACK_RETRY_MS;
                this.pending.set(seq, entry);
            }
        }

        enterBackpressure(attemptReset = true, reason = "unknown", context = {}) {
            const safeReason = RECOVERY_REASONS.has(reason) ? reason : "unknown";
            this.metrics.recoveryCount++;
            this.countReason(this.metrics.recoveryReasons, safeReason);
            this.metrics.lastRecovery = this.recordDiagnostic({
                type: "recovery", reason: safeReason,
                seq: finiteOrNull(context.seq), retries: finiteOrNull(context.retries),
                pendingCount: this.pending.size,
                bufferedAmount: finiteOrNull(this.channel?.bufferedAmount)
            });
            this.pressedKeys.clear();
            this.buttons = 0;
            this.pending.clear();
            this.pausedForBackpressure = true;
            this.resetPending = true;
            this.options.onStatus("Input paused by channel backpressure. Held inputs will reset after queued data drains.");
            if (attemptReset) this.flushReset();
        }

        flushReset() {
            const channel = this.channel;
            if (!this.resetPending || !this.hasControl() || !channel || channel.readyState !== "open") return;
            if (channel.bufferedAmount > LOW_BUFFERED_BYTES) return;
            const accepted = this.send("reset", { keys: [], buttons: 0 }, { allowWhilePaused: true });
            if (accepted) {
                this.resetPending = false;
                this.pausedForBackpressure = false;
                this.options.onStatus("Input channel recovered. No stale input was replayed.");
            }
        }

        onMessage(event) {
            let message;
            try { message = JSON.parse(event.data); }
            catch { return; }
            if (!message || message.sessionId !== this.sessionId || message.instanceId !== this.instanceId) return;
            // Replies from a released lease may still be queued on the data channel.
            // They must never change the current lease's input state or counters.
            if (!this.hasControl() || message.leaseId !== this.lease.leaseId) return;
            if (message.type === "input.error") {
                this.metrics.runtimeErrorCount++;
                const reason = RUNTIME_ERROR_REASONS.has(message.reason) ? message.reason : "unknown";
                this.countReason(this.metrics.runtimeErrorReasons, reason);
                this.metrics.lastRuntimeError = this.recordDiagnostic({
                    type: "runtime-error", reason, seq: finiteOrNull(message.seq),
                    expectedSeq: finiteOrNull(message.expectedSeq), hasControl: this.hasControl()
                });
                this.enterBackpressure(true, "runtime_error");
                this.options.onInputError(message);
                return;
            }
            if (message.type !== "input.ack") return;
            const entry = this.pending.get(message.seq);
            // JsonUtility can round a parsed double by one ULP. Lease and sequence
            // identify the packet; allow only this tiny timestamp representation error.
            if (!entry || !Number.isFinite(message.clientSentMs)
                || Math.abs(message.clientSentMs - entry.packet.clientSentMs) > ACK_TIMESTAMP_TOLERANCE_MS) {
                const reason = !entry ? "no_pending_entry" : !Number.isFinite(message.clientSentMs)
                    ? "invalid_client_timestamp" : "client_timestamp_mismatch";
                const expectedClientSentMs = finiteOrNull(entry?.packet.clientSentMs);
                const echoedClientSentMs = finiteOrNull(message.clientSentMs);
                this.metrics.ackRejectedCount++;
                this.countReason(this.metrics.ackRejectionReasons, reason);
                this.metrics.lastRejectedAck = this.recordDiagnostic({
                    type: "ack-rejected", reason, seq: finiteOrNull(message.seq),
                    expectedClientSentMs, echoedClientSentMs,
                    deltaMs: expectedClientSentMs !== null && echoedClientSentMs !== null
                        ? finiteOrNull(echoedClientSentMs - expectedClientSentMs) : null,
                    pendingAgeMs: entry ? finiteOrNull(global.performance.now() - entry.sentAt) : null,
                    retries: finiteOrNull(entry?.retries), hasControl: this.hasControl()
                });
                return;
            }
            this.pending.delete(message.seq);
            if (message.seq > this.metrics.lastAckedSeq + 1) this.metrics.ackGaps += message.seq - this.metrics.lastAckedSeq - 1;
            if (message.seq > this.metrics.lastAckedSeq) this.metrics.lastAckedSeq = message.seq;
            this.metrics.ackedCount++;
            if (entry.kind === "ping") {
                this.latestRttMs = Math.max(0, global.performance.now() - entry.sentAt);
                this.pushSample(this.metrics.rttSamples, { seq: message.seq, ms: this.latestRttMs });
                this.options.onRtt(this.latestRttMs);
            }
        }

        readDiagnostic(id) {
            return this.diagnosticSentAt.get(id >>> 0) ?? null;
        }

        markDiagnosticPresented(id, presentedAt = global.performance.now(), timestampSource = "callback") {
            const key = id >>> 0;
            const sentAt = this.diagnosticSentAt.get(key);
            if (sentAt === undefined) return null;
            this.diagnosticSentAt.delete(key);
            for (const existingId of this.diagnosticSentAt.keys()) {
                if (((existingId - key) >>> 0) > 0x7fffffff) {
                    this.diagnosticSentAt.delete(existingId);
                    this.metrics.supersededMarkerCount++;
                }
            }
            const milliseconds = Math.max(0, presentedAt - sentAt);
            this.pushSample(this.metrics.frameSamples, { diagnosticId: key, ms: milliseconds, timestampSource });
            return milliseconds;
        }

        snapshot() {
            return {
                lastSentSeq: this.metrics.lastSentSeq,
                lastAckedSeq: this.metrics.lastAckedSeq,
                ackedCount: this.metrics.ackedCount,
                ackGaps: this.metrics.ackGaps,
                pendingCount: this.pending.size,
                resetCount: this.metrics.resetCount,
                refusalCount: this.metrics.refusalCount,
                resendCount: this.metrics.resendCount,
                runtimeErrorCount: this.metrics.runtimeErrorCount,
                ackRejectedCount: this.metrics.ackRejectedCount,
                ackRejectionReasons: { ...this.metrics.ackRejectionReasons },
                lastRejectedAck: this.metrics.lastRejectedAck ? { ...this.metrics.lastRejectedAck } : null,
                recoveryCount: this.metrics.recoveryCount,
                recoveryReasons: { ...this.metrics.recoveryReasons },
                lastRecovery: this.metrics.lastRecovery ? { ...this.metrics.lastRecovery } : null,
                runtimeErrorReasons: { ...this.metrics.runtimeErrorReasons },
                lastRuntimeError: this.metrics.lastRuntimeError ? { ...this.metrics.lastRuntimeError } : null,
                diagnosticEvents: this.metrics.diagnosticEvents.map(event => ({ ...event })),
                supersededMarkerCount: this.metrics.supersededMarkerCount,
                expiredMarkerCount: this.metrics.expiredMarkerCount,
                latestRttMs: this.latestRttMs,
                rttSamples: this.metrics.rttSamples.map(sample => ({ ...sample })),
                frameSamples: this.metrics.frameSamples.map(sample => ({ ...sample }))
            };
        }

        pushSample(collection, sample) {
            collection.push({ ...sample, atMs: global.performance.now() });
            if (collection.length > 100) collection.splice(0, collection.length - 100);
        }

        countReason(counts, reason) {
            counts[reason] = (counts[reason] || 0) + 1;
        }

        recordDiagnostic(fields) {
            const event = { ...fields, atMs: global.performance.now() };
            this.metrics.diagnosticEvents.push(event);
            if (this.metrics.diagnosticEvents.length > MAX_DIAGNOSTIC_EVENTS)
                this.metrics.diagnosticEvents.splice(0, this.metrics.diagnosticEvents.length - MAX_DIAGNOSTIC_EVENTS);
            return event;
        }

        nextDiagnosticId() {
            this.diagnosticId = (this.diagnosticId + 1) >>> 0;
            if (this.diagnosticId === 0) this.diagnosticId = 1;
            return this.diagnosticId;
        }

        randomDiagnosticSeed() {
            if (global.crypto?.getRandomValues) {
                const value = global.crypto.getRandomValues(new Uint32Array(1))[0];
                return value || 1;
            }
            return (Math.floor(global.performance.now() * 1000) ^ Math.floor(Math.random() * 0xFFFFFFFF)) >>> 0 || 1;
        }

        trimDiagnostics(now) {
            for (const [id, sentAt] of this.diagnosticSentAt) {
                if (now - sentAt > 10000) {
                    this.diagnosticSentAt.delete(id);
                    this.metrics.expiredMarkerCount++;
                }
            }
            while (this.diagnosticSentAt.size > 256) {
                this.diagnosticSentAt.delete(this.diagnosticSentAt.keys().next().value);
                this.metrics.expiredMarkerCount++;
            }
        }
    }

    global.StreamingInput = StreamingInput;
})(window);
