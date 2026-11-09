"use strict";

// Real dashboard/input scripts in a deterministic, dependency-free Node VM.
// No browser, HTTP listener, Unity/player process, backend, or files are created.
// This module also provides the shared environment for streaming-marker-checks.cjs.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const webRoot = path.resolve(__dirname, "../../PlaytestOps.Web/wwwroot/js");
let checks = 0;

function check(condition, label) {
    assert.ok(condition, label);
    checks++;
    process.stdout.write(`PASS ${label}\n`);
}

function session(instanceId = "a", fields = {}) {
    return { sessionId: `session-${instanceId}`, instanceId, buildId: "test-build",
        state: "Ready", diagnostics: true, runtimeConnected: true, viewerConnected: false,
        width: 1280, height: 720, index: 0, role: "client", mouseLockRequested: false, logs: [], ...fields };
}

function markerPixels(id) {
    const pixels = new Uint8ClampedArray(80 * 80 * 4);
    const cells = new Array(64).fill(0);
    cells[0] = cells[63] = 1;
    const dataCells = [...Array(64).keys()].filter(index => ![0, 7, 56, 63].includes(index));
    const unsignedId = id >>> 0;
    const checksum = (unsignedId ^ (unsignedId >>> 16) ^ 0xA5A5) & 0xFFFF;
    for (let bit = 0; bit < 32; bit++) cells[dataCells[bit]] = (unsignedId >>> bit) & 1;
    for (let bit = 0; bit < 16; bit++) cells[dataCells[32 + bit]] = (checksum >>> bit) & 1;
    for (let bit = 48; bit < 60; bit++) cells[dataCells[bit]] = dataCells[bit] % 2;
    for (let index = 0; index < 64; index++) {
        for (let y = 8 + Math.floor(index / 8) * 8; y < 16 + Math.floor(index / 8) * 8; y++) {
            for (let x = 8 + index % 8 * 8; x < 16 + index % 8 * 8; x++) {
                const offset = (y * 80 + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = cells[index] * 255;
                pixels[offset + 3] = 255;
            }
        }
    }
    return pixels;
}

async function settle() {
    for (let index = 0; index < 30; index++) await Promise.resolve();
}

function deferred() {
    let resolve, reject;
    const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
    return { promise, resolve, reject };
}

class Target {
    constructor() { this.events = new Map(); }
    addEventListener(name, callback, options = {}) {
        const listeners = this.events.get(name) || [];
        listeners.push({ callback, once: options === true ? false : !!options.once });
        this.events.set(name, listeners);
    }
    removeEventListener(name, callback) {
        this.events.set(name, (this.events.get(name) || []).filter(item => item.callback !== callback));
    }
    emit(name, fields = {}) {
        const event = { type: name, target: this, currentTarget: this, defaultPrevented: false,
            preventDefault() { this.defaultPrevented = true; }, stopPropagation() { this.cancelBubble = true; }, ...fields };
        for (const item of [...(this.events.get(name) || [])]) {
            if (item.once) this.removeEventListener(name, item.callback);
            const result = item.callback(event);
            if (result && typeof result.then === "function") result.catch(error => this.reportError?.(error));
        }
        if (typeof this[`on${name}`] === "function") this[`on${name}`](event);
        return event;
    }
    dispatchEvent(event) { return !this.emit(event.type, event).defaultPrevented; }
}

async function setup(options = {}) {
    let now = 0, nextId = 1, random = 123456;
    let nextState = options.state || { builds: [], sessions: [] };
    let nextSteamEnvironments = options.steamEnvironments || { environments: [], available: false, reason: "No configured boxes" };
    let latencyFailure = null;
    let startReply = null;
    const timers = new Map(), frames = new Map(), pixels = new Map(), pixelErrors = new Set();
    const requests = [], timeline = [], sockets = [], peers = [], errors = [], deferredFetches = [], pointerLockRequests = [];
    const document = new Target();
    document.reportError = error => errors.push(error);
    const dataKey = name => name.slice(5).replace(/-([a-z])/g, (_, character) => character.toUpperCase());
    const decode = text => text.replace(/&quot;/g, '"').replace(/&#39;|&apos;/g, "'").replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&");

    class Node extends Target {
        constructor(tag = "div", text = "") {
            super();
            this.reportError = error => errors.push(error);
            Object.assign(this, { tagName: tag.toUpperCase(), nodeType: tag === "#text" ? 3 : 1,
                ownerDocument: document, parentNode: null, children: [], dataset: {}, style: {}, attributes: new Map(),
                id: "", className: "", value: "", checked: false, hidden: false, disabled: false, _text: text });
            this.classList = {
                contains: name => this.className.split(/\s+/).includes(name),
                add: (...names) => { this.className = [...new Set([...this.className.split(/\s+/).filter(Boolean), ...names])].join(" "); },
                remove: (...names) => { this.className = this.className.split(/\s+/).filter(name => !names.includes(name)).join(" "); },
                toggle: (name, force) => {
                    const enabled = force === undefined ? !this.classList.contains(name) : !!force;
                    this.classList[enabled ? "add" : "remove"](name); return enabled;
                }
            };
            if (tag === "template") this.content = new Node("#fragment");
            if (tag === "video") {
                Object.assign(this, { srcObject: null, readyState: 2, videoWidth: 1280, videoHeight: 720,
                    clientWidth: 1280, clientHeight: 720, play: async () => {}, pause() {},
                    requestVideoFrameCallback: callback => { const id = nextId++; frames.set(id, { video: this, callback }); return id; },
                    cancelVideoFrameCallback: id => frames.delete(id),
                    requestPointerLock: lockOptions => {
                        pointerLockRequests.push({ video: this, options: lockOptions, atMs: now });
                        timeline.push({ kind: "pointer-lock-request", video: this, at: now });
                        if (options.pointerLock) return options.pointerLock(this, lockOptions, document);
                        document.pointerLockElement = this; document.emit("pointerlockchange");
                        return Promise.resolve();
                    } });
                if (options.unsupported) delete this.requestVideoFrameCallback;
            }
            if (tag === "canvas") {
                let sourceVideo;
                this.getContext = () => options.canvasUnavailable ? null : {
                    drawImage(video) { sourceVideo = video; if (pixelErrors.has(video)) throw new Error("isolated pixel read failure"); },
                    getImageData() { return { data: pixels.get(sourceVideo) || markerPixels(0) }; }
                };
            }
        }
        get textContent() { return this._text + this.children.map(child => child.textContent).join(""); }
        set textContent(value) { this.replaceChildren(); this._text = String(value ?? ""); }
        get childNodes() { return this.children; }
        get parentElement() { return this.parentNode; }
        get firstChild() { return this.children[0] || null; }
        get firstElementChild() { return this.children.find(child => child.nodeType === 1) || null; }
        get options() { return this.children; }
        get selectedOptions() { return this.children.filter(child => child.value === this.value); }
        get isConnected() { return document.body.contains(this); }
        append(...items) {
            for (let item of items) {
                if (typeof item === "string") item = new Node("#text", item);
                if (item.tagName === "#FRAGMENT") { this.append(...[...item.children]); continue; }
                item.remove(); item.parentNode = this; this.children.push(item);
            }
        }
        appendChild(item) { this.append(item); return item; }
        replaceChildren(...items) { for (const child of this.children) child.parentNode = null; this.children = []; this._text = ""; this.append(...items); }
        remove() { if (this.parentNode) this.parentNode.children = this.parentNode.children.filter(item => item !== this); this.parentNode = null; }
        removeChild(item) { item.remove(); return item; }
        contains(item) { return item === this || this.children.some(child => child.contains(item)); }
        setAttribute(name, value) {
            value = String(value); this.attributes.set(name, value);
            if (name.startsWith("data-")) this.dataset[dataKey(name)] = value;
            else if (name === "class") this.className = value;
            else if (["hidden", "disabled", "checked"].includes(name)) this[name] = true;
            else this[name] = value;
        }
        getAttribute(name) { return name.startsWith("data-") ? this.dataset[dataKey(name)] ?? null : name === "class" ? this.className : this.attributes.get(name) ?? this[name] ?? null; }
        removeAttribute(name) { this.attributes.delete(name); if (name.startsWith("data-")) delete this.dataset[dataKey(name)]; else if (["hidden", "disabled", "checked"].includes(name)) this[name] = false; }
        matches(selector) {
            return selector.split(",").some(part => {
                part = part.trim();
                const attributes = [...part.matchAll(/\[([^\]=\s]+)(?:\s*=\s*["']?([^\]"']*)["']?)?\]/g)];
                part = part.replace(/\[[^\]]*\]/g, "");
                if (attributes.some(([, name, value]) => this.getAttribute(name) === null || (value !== undefined && String(this.getAttribute(name)) !== value))) return false;
                const id = part.match(/#([\w-]+)/)?.[1];
                if (id && this.id !== id) return false;
                if ([...part.matchAll(/\.([\w-]+)/g)].some(([, name]) => !this.classList.contains(name))) return false;
                const tag = part.match(/^[\w-]+/)?.[0];
                return !tag || this.tagName === tag.toUpperCase();
            });
        }
        querySelectorAll(selector) {
            const result = [];
            const parts = selector.trim().split(/\s+(?![^\[]*\])/);
            const visit = node => {
                for (const child of node.children) {
                    if (child.matches(parts[parts.length - 1])) {
                        let ancestor = child.parentNode, index = parts.length - 2;
                        while (ancestor && index >= 0) { if (ancestor.matches(parts[index])) index--; ancestor = ancestor.parentNode; }
                        if (index < 0) result.push(child);
                    }
                    visit(child);
                }
            };
            visit(this); return result;
        }
        querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
        closest(selector) { return this.matches(selector) ? this : this.parentNode?.closest(selector) || null; }
        focus() {
            if (document.activeElement && document.activeElement !== this) document.activeElement.emit("blur", { relatedTarget: this });
            document.activeElement = this; this.emit("focus");
        }
        click() {
            if (this.disabled) return;
            let event = this.emit("click", { bubbles: true, button: 0 });
            for (let parent = this.parentNode; parent && !event.cancelBubble; parent = parent.parentNode)
                event = parent.emit("click", { bubbles: true, button: 0, target: this, defaultPrevented: event.defaultPrevented });
        }
        setPointerCapture() {}
        releasePointerCapture() {}
        getBoundingClientRect() { return { left: 0, top: 0, width: this.clientWidth || 1280, height: this.clientHeight || 720 }; }
        set innerHTML(html) {
            const root = this.content || this; root.replaceChildren(); const stack = [root];
            for (const token of String(html).match(/<!--[\s\S]*?-->|<[^>]+>|[^<]+/g) || []) {
                if (token.startsWith("<!--")) continue;
                if (token.startsWith("</")) { if (stack.length > 1) stack.pop(); continue; }
                if (!token.startsWith("<")) { stack[stack.length - 1].append(new Node("#text", decode(token))); continue; }
                const tag = token.match(/^<([\w-]+)/)?.[1]; if (!tag) continue;
                const child = new Node(tag);
                const attributes = token.slice(tag.length + 1).replace(/\/?\s*>$/, "");
                for (const match of attributes.matchAll(/([^\s=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s]+)))?/g)) child.setAttribute(match[1], decode(match[2] ?? match[3] ?? match[4] ?? ""));
                stack[stack.length - 1].append(child);
                if (!/\/>$/.test(token) && !["input", "br", "hr", "img", "meta", "link"].includes(tag)) stack.push(child);
            }
        }
        cloneNode(deep = false) {
            const copy = new Node(this.tagName.toLowerCase(), this._text);
            for (const [key, value] of this.attributes) copy.setAttribute(key, value);
            copy.id = this.id; copy.className = this.className; copy.dataset = { ...this.dataset };
            if (deep) for (const child of this.children) copy.append(child.cloneNode(true));
            return copy;
        }
    }

    Object.assign(document, { readyState: "loading", hidden: false, visibilityState: "visible", activeElement: null, pointerLockElement: null,
        body: new Node("body"), createElement: tag => new Node(tag), createDocumentFragment: () => new Node("#fragment"),
        createTextNode: text => new Node("#text", text), getElementById: id => document.body.querySelector(`#${id}`),
        querySelector: selector => document.body.querySelector(selector), querySelectorAll: selector => document.body.querySelectorAll(selector),
        exitPointerLock() { document.pointerLockElement = null; document.emit("pointerlockchange"); } });
    document.documentElement = document.body;
    for (const name of ["alert", "build", "build-note", "width", "height", "diagnostics", "instance-count", "profile-field", "profile",
        "steam-sandboxes", "steam-sandbox-options", "steam-sandbox-note", "start", "refresh", "connect", "stop",
        "session-summary", "tiles", "no-streams", "setup", "empty", "control-status", "manual-takeover", "log", "diagnostics-help",
        "latency", "latency-min", "latency-max", "latency-apply", "latency-reset", "latency-status", "latency-applied", "latency-summary", "latency-links"]) {
        const node = new Node(["build", "instance-count", "profile"].includes(name) ? "select"
            : ["width", "height", "diagnostics", "latency-min", "latency-max"].includes(name) ? "input"
                : ["latency-apply", "latency-reset"].includes(name) ? "button" : "div");
        node.id = `streaming-${name}`; document.body.append(node);
    }
    for (const [value, text] of [["1", "Single"], ["2", "Host + 1 client"], ["3", "Host + 2 clients"], ["4", "Host + 3 clients"]]) {
        const option = new Node("option", text); option.value = value; document.getElementById("streaming-instance-count").append(option);
    }
    const antiForgery = new Node("form"); antiForgery.id = "streaming-antiforgery";
    const antiForgeryInput = new Node("input"); antiForgeryInput.setAttribute("name", "__RequestVerificationToken"); antiForgeryInput.value = "fake-antiforgery-secret";
    antiForgery.append(antiForgeryInput); document.body.append(antiForgery);
    document.getElementById("streaming-width").value = "1280";
    document.getElementById("streaming-height").value = "720";
    document.getElementById("streaming-instance-count").value = "1";
    document.getElementById("streaming-latency-min").value = "50";
    document.getElementById("streaming-latency-max").value = "100";
    document.getElementById("streaming-latency").hidden = true;

    class Socket extends Target {
        static CONNECTING = 0; static OPEN = 1; static CLOSING = 2; static CLOSED = 3;
        constructor(url) { super(); this.reportError = error => errors.push(error); this.url = String(url); this.readyState = Socket.CONNECTING; this.sent = []; sockets.push(this); }
        open() { this.readyState = Socket.OPEN; this.emit("open"); }
        send(value) { if (this.readyState !== Socket.OPEN) throw new Error("Send on a non-open fake socket"); const packet = JSON.parse(value); this.sent.push(packet); timeline.push({ kind: "socket", socket: this, packet, at: now }); }
        close() { if (this.readyState === Socket.CLOSED) return; this.readyState = Socket.CLOSED; Promise.resolve().then(() => this.emit("close", { code: 1000 })); }
    }
    class Peer extends Target {
        constructor(configuration) { super(); this.reportError = error => errors.push(error); this.configuration = configuration; this.connectionState = "new"; this.iceConnectionState = "new"; this.localDescription = null; this.remoteDescription = null; this.candidates = []; peers.push(this); }
        async getStats() { return this.stats || new Map(); }
        async setRemoteDescription(value) { this.remoteDescription = value; }
        async createAnswer() { return { type: "answer", sdp: "fake-answer" }; }
        async setLocalDescription(value) { this.localDescription = value; }
        async addIceCandidate(value) { this.candidates.push(value); }
        close() { this.connectionState = "closed"; }
    }
    function timer(callback, delay, repeat) { const id = nextId++; const milliseconds = Math.max(1, Number(delay) || 0); timers.set(id, { callback, delay: milliseconds, due: now + milliseconds, repeat }); return id; }
    const sandbox = new Target(); sandbox.reportError = error => errors.push(error);
    Object.assign(sandbox, { document, console, URL, Uint8Array, Uint32Array, Uint8ClampedArray, HTMLElement: Node, HTMLVideoElement: Node,
        performance: { now: () => now },
        crypto: { getRandomValues(array) { for (let index = 0; index < array.length; index++) { random ^= random << 13; random ^= random >>> 17; random ^= random << 5; array[index] = random >>> 0; } return array; } },
        location: { hostname: "localhost", origin: "http://localhost:5286", protocol: "http:", href: "http://localhost:5286/Streaming" },
        HTMLMediaElement: { HAVE_CURRENT_DATA: 2 }, WebSocket: Socket, RTCPeerConnection: Peer,
        MediaStream: class { constructor(tracks) { this.tracks = tracks; } getTracks() { return this.tracks; } },
        PlaytestOpsTime: { formatUtc: value => value, formatTimeUtc: value => value },
        setInterval: (callback, delay) => timer(callback, delay, true), clearInterval: id => timers.delete(id),
        setTimeout: (callback, delay) => timer(callback, delay, false), clearTimeout: id => timers.delete(id),
        requestAnimationFrame(callback) { const id = nextId++; frames.set(id, { video: null, callback }); return id; }, cancelAnimationFrame: id => frames.delete(id),
        async fetch(url, init = {}) {
            url = String(url); requests.push({ url, ...init });
            let body;
            const deferredIndex = deferredFetches.findIndex(item => url.includes(item.match));
            if (deferredIndex >= 0) body = await deferredFetches.splice(deferredIndex, 1)[0].promise;
            else if (init.method === "POST" && url.endsWith("/viewer-grant")) {
                const id = decodeURIComponent(url.split("/").at(-2));
                const record = nextState.sessions.find(item => item.sessionId === id);
                if (!record) throw new Error(`No fixture session for viewer grant ${id}`);
                body = { sessionId: id, instanceId: record.instanceId, token: `fake-viewer-secret-${id}`, viewerWebSocketPath: `/api/streaming/viewer/${id}`, iceServers: [] };
            } else if (init.method === "POST" && url.endsWith("/start")) body = startReply || { sessions: nextState.sessions };
            else if (init.method === "POST" && url.endsWith("/stop")) body = {};
            else if (init.method === "POST" && url.endsWith("/latency")) {
                if (latencyFailure) {
                    const failure = latencyFailure; latencyFailure = null;
                    return { ok: false, status: failure.status, async text() { return JSON.stringify({ detail: failure.message }); } };
                }
                const request = JSON.parse(init.body || "{}");
                if (!nextState.networkLatency) throw new Error("Latency fixture has no active group state");
                nextState = { ...nextState, networkLatency: { ...nextState.networkLatency, ...request,
                    revision: (nextState.networkLatency.revision || 0) + 1 } };
                body = { networkLatency: nextState.networkLatency };
            }
            else if (url.endsWith("/steam-environments")) body = nextSteamEnvironments;
            else if (url.endsWith("/state")) body = nextState;
            else throw new Error(`Unexpected fixture fetch ${init.method || "GET"} ${url}`);
            return { ok: true, status: 200, async text() { return JSON.stringify(body); } };
        } });
    sandbox.window = sandbox;
    const sourcePath = path.join(webRoot, "streaming.js"), inputPath = path.join(webRoot, "streaming-input.js");
    const source = fs.readFileSync(sourcePath, "utf8"), closing = source.lastIndexOf("})();");
    assert.ok(closing >= 0, "Dashboard closure exists for VM-only hooks");
    const hooks = "\nwindow.__multistreamTests = { initialize, refreshState, applySessions, selectTile, startSession, tiles, get networkLatency() { return networkLatency; } };\n";
    vm.createContext(sandbox);
    vm.runInContext(fs.readFileSync(inputPath, "utf8"), sandbox, { filename: inputPath, timeout: 1000 });
    vm.runInContext(source.slice(0, closing) + hooks + source.slice(closing), sandbox, { filename: sourcePath, timeout: 1000 });
    const api = sandbox.__multistreamTests;
    api.initialize(); await settle();

    const harness = { api, sandbox, document, requests, timeline, timers, frames, sockets, peers, pointerLockRequests,
        node: id => document.getElementById(id.startsWith("streaming-") ? id : `streaming-${id}`),
        tile: id => api.tiles.get(id),
        apply(records) { nextState = { ...nextState, sessions: records }; api.applySessions(records); },
        async refresh(records) { nextState = { ...nextState, sessions: records }; await api.refreshState(); await settle(); },
        async load(value) { nextState = value; await api.refreshState(); await settle(); },
        state(value) { nextState = value; }, startReply(value) { startReply = value; },
        steamEnvironments(value) { nextSteamEnvironments = value; },
        failLatency(status, message) { latencyFailure = { status, message }; },
        deferFetch(match) { const pending = { match, ...deferred() }; deferredFetches.push(pending); return pending; },
        now(value) { const delta = value - now; for (const item of timers.values()) item.due += delta; now = value; },
        checkErrors() { if (errors.length) throw errors.shift(); },
        pixels(video, value) { pixels.set(video, value); }, pixelError(video, value) { if (value) pixelErrors.add(video); else pixelErrors.delete(video); },
        callbacks(video) { return [...frames.values()].filter(item => item.video === video).map(item => item.callback); },
        frame(video, metadata = {}) { const pending = [...frames.entries()].filter(([, item]) => item.video === video); for (const [id, item] of pending) { frames.delete(id); item.callback(now, metadata); } },
        animationFrame() { this.frame(null); },
        async advance(milliseconds) {
            const target = now + milliseconds; let iterations = 0;
            while (true) {
                const pending = [...timers.entries()].filter(([, item]) => item.due <= target).sort((a, b) => a[1].due - b[1].due)[0];
                if (!pending) break;
                if (++iterations > 10000) throw new Error("Fake timer loop exceeded its bound");
                const [id, item] = pending; now = item.due;
                if (item.repeat) item.due += item.delay; else timers.delete(id);
                const result = item.callback(); if (result?.catch) result.catch(error => errors.push(error));
                await settle(); this.checkErrors();
            }
            now = target; await settle(); this.checkErrors();
        },
        async message(viewer, packet) { viewer.socket.emit("message", { data: JSON.stringify({ sessionId: viewer.record.sessionId, instanceId: viewer.record.instanceId, ...packet }) }); await settle(); this.checkErrors(); },
        async connect(id, withVideo = true) {
            const tile = this.tile(id); assert.ok(tile, `Fixture tile ${id} exists`);
            const oldSockets = sockets.length;
            const connecting = tile.connect(); await settle();
            const socket = tile.context?.socket || sockets[oldSockets]; assert.ok(socket, `Tile ${id} opens its own viewer socket`);
            socket.open(); await settle(); await connecting;
            const viewer = { tile, record: { ...tile.record }, socket, context: tile.context, peer: tile.context?.pc };
            assert.ok(viewer.peer, `Tile ${id} owns a peer connection`);
            const channel = new Target(); channel.reportError = error => errors.push(error);
            Object.assign(channel, { label: "playtestops-input", ordered: true, maxRetransmits: null, maxPacketLifeTime: null,
                readyState: "open", bufferedAmount: 0, sent: [],
                send(value) { if (this.readyState !== "open") throw new Error("Send on a closed fake channel"); const packet = JSON.parse(value); this.sent.push(packet); timeline.push({ kind: "input", channel: this, packet, at: now }); },
                close() { this.readyState = "closed"; Promise.resolve().then(() => this.emit("close")); } });
            viewer.channel = channel;
            viewer.peer.connectionState = "connected"; viewer.peer.iceConnectionState = "connected";
            viewer.peer.emit("datachannel", { channel }); viewer.peer.emit("connectionstatechange");
            const track = new Target(); track.reportError = error => errors.push(error);
            Object.assign(track, { kind: "video", readyState: "live", stop() { this.readyState = "ended"; } });
            viewer.track = track; viewer.video = tile.ui.video;
            if (withVideo) viewer.peer.emit("track", { track, streams: [{ getTracks: () => [track] }] });
            await settle(); this.checkErrors(); return viewer;
        },
        async take(viewer) { viewer.video.click(); await settle(); this.checkErrors(); },
        async grant(viewer, leaseId = `lease-${viewer.record.instanceId}`, requestId = null) {
            const request = viewer.socket.sent.filter(packet => packet.type === "control.request").at(-1);
            assert.ok(request || requestId, "A control request precedes its fixture grant");
            const packet = { type: "control.granted", requestId: requestId || request.requestId,
                leaseId, leaseToken: `fake-lease-secret-${leaseId}`, timeoutMs: 2000, expiresUtc: "2099-01-01T00:00:00Z" };
            await this.message(viewer, packet); return packet;
        },
        metric(tile, name) { const element = tile.node.querySelector(`[data-metric="${name}"]`); assert.ok(element, `Tile metric ${name} exists`); return element.textContent; },
        key(code, down = true, fields = {}) { return document.emit(down ? "keydown" : "keyup", { code, repeat: false, ...fields }); },
        async close() { sandbox.emit("pagehide"); await settle(); this.checkErrors(); }
    };
    harness.checkErrors(); return harness;
}

async function main() {
    const sent = (viewer, type) => viewer.socket.sent.filter(packet => packet.type === type);
    const controlled = h => [...h.api.tiles.values()].filter(tile => tile.controlActive);
    async function group(count = 3, fields = {}) {
        const h = await setup();
        h.apply(["a", "b", "c", "d"].slice(0, count).map((id, index) => session(id, { index, role: index === 0 ? "host" : "client", ...fields })));
        const viewers = [];
        for (const id of h.api.tiles.keys()) viewers.push(await h.connect(id));
        return { h, viewers, a: viewers[0], b: viewers[1], c: viewers[2] };
    }
    async function own(h, viewer, leaseId) { await h.take(viewer); return h.grant(viewer, leaseId); }
    async function released(h, viewer, leaseId) { await h.message(viewer, { type: "control.released", leaseId }); }

    const empty = await setup();
    check(empty.api.tiles.size === 0, "dashboard starts without synthetic viewer tiles");
    const razor = fs.readFileSync(path.resolve(webRoot, "../../Pages/Streaming.cshtml"), "utf8");
    check(["take-control", "release-control", "mouse-look"].every(id => !empty.node(id)
        && !razor.includes(`id="streaming-${id}"`)), "removed control buttons are absent from the page and test DOM");
    const latencyDetails = razor.match(/<details id="streaming-latency"([^>]*)>/)?.[0] || "";
    const streamingCss = fs.readFileSync(path.resolve(webRoot, "../css/streaming.css"), "utf8");
    check(!!latencyDetails && !/\sopen(?:\s|>)/.test(latencyDetails)
        && razor.includes("id=\"streaming-latency-summary\"")
        && /Applied target: Waiting for a ready KCP group/.test(razor),
        "latency controls default collapsed with the applied target visible in their summary");
    const scopedLatencyGrid = streamingCss.match(/\.streaming-session:has\(#streaming-latency:not\(\[hidden\]\)\) \.streaming-tiles\s*\{([^}]*)\}/)?.[1] || "";
    check(/min-height:\s*min-content/.test(scopedLatencyGrid)
        && /grid-auto-rows:\s*minmax\(min-content,\s*1fr\)/.test(scopedLatencyGrid)
        && /\.streaming-session:has\(#streaming-latency:not\(\[hidden\]\)\) \.streaming-stage\s*\{\s*min-height:\s*120px;/.test(streamingCss),
        "KCP live tiles preserve 120 px video stages when latency controls expand");
    await empty.close();

    {
        const latencyState = (groupId, enabled = false, gameReady = true) => ({
            builds: [], sessions: [
                session("latency-host", { groupId, profile: "arena-kcp", role: "host", index: 0, gameReady }),
                session("latency-client", { groupId, profile: "arena-kcp", role: "client", index: 1, gameReady })
            ], networkLatency: !gameReady ? null : { groupId, enabled, minRttMs: 50, maxRttMs: 100, revision: 1,
                links: [{ instanceId: "latency-client", index: 1, label: "Client 1", queuedPackets: 2, queuedBytes: 128,
                    droppedPackets: 0, forwardedPackets: 40, gameRttMs: 67.5, gameRttStdDevMs: 3.2, gameRttSampleUtc: new Date().toISOString() }] }
        });
        const h = await setup({ state: latencyState("latency-group-a") });
        const panel = h.node("latency");
        check(!panel.hidden && !h.node("latency-apply").disabled && h.node("latency-min").value === "50"
            && h.node("latency-max").value === "100", "ready KCP group exposes enabled RTT range controls with the planned 50–100 ms defaults");
        check(/Measured game RTT 67\.5 ms/.test(h.node("latency-links").textContent)
            && /queued 2 packets/.test(h.node("latency-links").textContent)
            && h.metric(h.tile("latency-client"), "rtt") === "—",
            "relay game RTT and queue counters are separate from the existing per-viewer Input RTT metric");

        h.node("latency-min").value = "1001";
        h.node("latency-apply").click(); await settle();
        check(!h.requests.some(request => request.method === "POST" && request.url.endsWith("/latency"))
            && /whole-number RTT values/.test(h.node("latency-status").textContent),
            "latency input rejects values outside 0–1000 ms before sending a request");
        h.node("latency-min").value = "101";
        h.node("latency-max").value = "100";
        h.node("latency-apply").click(); await settle();
        check(!h.requests.some(request => request.method === "POST" && request.url.endsWith("/latency")),
            "latency input rejects a minimum greater than its maximum");
        h.node("latency-min").value = "";
        h.node("latency-apply").click(); await settle();
        check(!h.requests.some(request => request.method === "POST" && request.url.endsWith("/latency")),
            "blank latency values are not coerced to zero");

        h.node("latency-min").value = "50"; h.node("latency-max").value = "100";
        const applying = h.deferFetch("/latency");
        h.node("latency-apply").click(); await settle();
        const applyRequest = h.requests.filter(request => request.method === "POST" && request.url.endsWith("/latency")).at(-1);
        check(h.node("latency-apply").disabled && /Pending target: 50–100 ms/.test(h.node("latency-status").textContent)
            && /Applied target: Off/.test(h.node("latency-applied").textContent)
            && applyRequest.url === "/api/streaming/groups/latency-group-a/latency"
            && JSON.parse(applyRequest.body).enabled === true && JSON.parse(applyRequest.body).minRttMs === 50
            && JSON.parse(applyRequest.body).maxRttMs === 100
            && applyRequest.headers.RequestVerificationToken === "fake-antiforgery-secret",
            "Apply is visibly pending and posts the typed, antiforgery-protected group range while disabling edits");
        const applied = { ...latencyState("latency-group-a").networkLatency, enabled: true };
        applying.resolve({ networkLatency: applied });
        await settle();
        check(/Applied revision/.test(h.node("latency-status").textContent)
            && /Applied target: 50–100 ms/.test(h.node("latency-applied").textContent)
            && !h.node("latency-apply").disabled, "Apply displays the server-confirmed range after the response");

        const newerState = latencyState("latency-group-a", false);
        newerState.networkLatency.revision = 3;
        newerState.networkLatency.minRttMs = 20;
        newerState.networkLatency.maxRttMs = 30;
        await h.load(newerState);
        check(/No pending update/.test(h.node("latency-status").textContent)
            && /Applied target: Off/.test(h.node("latency-applied").textContent)
            && h.node("latency-min").value === "50" && h.node("latency-max").value === "100",
            "newer authoritative revision clears an obsolete Apply notice without changing the operator's edited range");

        h.node("latency-reset").click(); await settle();
        const resetRequest = h.requests.filter(request => request.method === "POST" && request.url.endsWith("/latency")).at(-1);
        check(JSON.parse(resetRequest.body).enabled === false && /Reset confirmed/.test(h.node("latency-status").textContent)
            && /Applied target: Off/.test(h.node("latency-applied").textContent),
            "Reset disables injection and reports off only after the synchronous server confirmation");

        h.failLatency(409, "The KCP group is stopping.");
        h.node("latency-apply").click(); await settle();
        check(/Latency update failed: The KCP group is stopping/.test(h.node("latency-status").textContent)
            && /Applied target: Off/.test(h.node("latency-applied").textContent)
            && h.node("latency-links").textContent.includes("Measured game RTT 67.5 ms"),
            "server conflict remains visible without replacing the last relay metrics with a requested value");

        const stale = h.deferFetch("/latency"); h.node("latency-apply").click(); await settle();
        await h.load(latencyState("latency-group-b", true));
        stale.resolve({ networkLatency: { ...latencyState("latency-group-a").networkLatency, enabled: true } });
        await settle();
        check(/Applied target: 50–100 ms/.test(h.node("latency-applied").textContent)
            && h.node("latency").hidden === false,
            "late response from a replaced group cannot overwrite the current group latency state");
        await h.close();

        const revisionRace = await setup({ state: latencyState("latency-revision-race") });
        const olderPost = revisionRace.deferFetch("/latency");
        revisionRace.node("latency-apply").click(); await settle();
        const latestConfig = latencyState("latency-revision-race", false);
        latestConfig.networkLatency.revision = 5;
        latestConfig.networkLatency.minRttMs = 30;
        latestConfig.networkLatency.maxRttMs = 40;
        await revisionRace.load(latestConfig);
        olderPost.resolve({ networkLatency: { ...latencyState("latency-revision-race", true).networkLatency, revision: 2, enabled: true } });
        await settle();
        check(/Applied target: Off/.test(revisionRace.node("latency-applied").textContent),
            "older in-flight Apply response cannot overwrite a newer same-group server revision");
        await revisionRace.close();

        const stateRace = await setup({ state: latencyState("latency-state-race", true) });
        const oldPoll = stateRace.deferFetch("/state");
        const oldPollRequest = stateRace.api.refreshState(); await settle();
        stateRace.node("latency-reset").click(); await settle();
        check(stateRace.api.networkLatency.revision === 2 && stateRace.api.networkLatency.enabled === false,
            "confirmed Reset advances the authoritative group revision");
        oldPoll.resolve(latencyState("latency-state-race", true));
        await oldPollRequest; await settle();
        check(stateRace.api.networkLatency.revision === 2 && stateRace.api.networkLatency.enabled === false,
            "older in-flight state poll cannot replace a newer POST latency revision");
        await stateRace.close();

        const waiting = await setup({ state: latencyState("latency-waiting", false, false) });
        check(!waiting.node("latency").hidden && waiting.node("latency-apply").disabled
            && /Waiting for all KCP players/.test(waiting.node("latency-applied").textContent),
            "KCP latency controls remain visible but unavailable until every game runtime is ready");
        await waiting.close();

        const steam = latencyState("steam-group");
        steam.sessions.forEach(record => { record.profile = "arena-steam"; });
        steam.networkLatency = null;
        const nonKcp = await setup({ state: steam });
        check(nonKcp.node("latency").hidden, "Steam and non-KCP groups never show the UDP latency inducer");
        await nonKcp.close();

        const malformed = latencyState("", true);
        malformed.sessions.forEach(record => { record.groupId = ""; });
        const missingGroup = await setup({ state: malformed });
        check(missingGroup.node("latency").hidden && missingGroup.node("latency-apply").disabled,
            "KCP records without a group identity cannot enable live latency controls");
        await missingGroup.close();

        const resetInvalidDraft = await setup({ state: latencyState("latency-reset-invalid", true) });
        resetInvalidDraft.node("latency-min").value = "1001";
        const resetReply = resetInvalidDraft.deferFetch("/latency");
        resetInvalidDraft.node("latency-reset").click(); await settle();
        const resetInvalidRequest = resetInvalidDraft.requests.filter(request => request.method === "POST" && request.url.endsWith("/latency")).at(-1);
        const resetInvalidBody = JSON.parse(resetInvalidRequest.body);
        check(resetInvalidBody.enabled === false && resetInvalidBody.minRttMs === 50 && resetInvalidBody.maxRttMs === 100
            && resetInvalidDraft.node("latency-min").value === "1001"
            && /Applied target: 50–100 ms/.test(resetInvalidDraft.node("latency-applied").textContent)
            && /Pending target: off/.test(resetInvalidDraft.node("latency-status").textContent),
            "Reset uses the last confirmed range despite an invalid draft and keeps the confirmed target visible while pending");
        resetReply.resolve({ networkLatency: { ...latencyState("latency-reset-invalid", false).networkLatency, revision: 2 } });
        await settle();
        check(/Applied target: Off/.test(resetInvalidDraft.node("latency-applied").textContent),
            "Reset shows latency off only after the successful server response");
        await resetInvalidDraft.close();
    }

    for (const mouseLockRequested of [false, true]) {
        const { h, a } = await group(1, { mouseLockRequested });
        a.video.emit("pointerdown", { button: 0, pointerId: 1, clientX: 50, clientY: 50 });
        h.document.emit("pointerup", { button: 0, clientX: 50, clientY: 50, target: a.video });
        a.video.click();
        check(h.pointerLockRequests.length === (mouseLockRequested ? 1 : 0)
            && (h.document.pointerLockElement === a.video) === mouseLockRequested,
            `initial mouseLockRequested=${mouseLockRequested} controls the synchronous click lock request`);
        check(a.channel.sent.length === 0 && !a.tile.controlActive,
            `activation click with mouseLockRequested=${mouseLockRequested} sends no gameplay input before the lease grant`);
        await settle();
        const lease = await h.grant(a, `click-${mouseLockRequested}`);
        check(a.tile.controlActive && a.channel.sent.every(packet => packet.kind === "state" && packet.buttons === 0),
            `activation click with mouseLockRequested=${mouseLockRequested} never becomes a shot after grant`);
        const requestsBefore = sent(a, "control.request").length, locksBefore = h.pointerLockRequests.length;
        a.video.click(); await settle();
        check(sent(a, "control.request").length === requestsBefore && h.pointerLockRequests.length === locksBefore,
            `repeated active click with mouseLockRequested=${mouseLockRequested} preserves the existing lease and lock`);
        a.video.emit("pointerdown", { button: 0, pointerId: 2, clientX: 50, clientY: 50 });
        h.document.emit("pointerup", { button: 0, clientX: 50, clientY: 50, target: a.video });
        h.key("KeyW");
        check(a.channel.sent.some(packet => packet.kind === "pointer" && packet.buttons === 1)
            && a.tile.input.pressedKeys.has("KeyW"), `controlled mouseLockRequested=${mouseLockRequested} forwards subsequent mouse and keyboard input`);
        const form = h.document.createElement("input"); h.document.activeElement = form;
        const escape = h.key("Unidentified", true, { key: "Escape", target: form });
        await settle();
        check(escape.defaultPrevented && !a.tile.controlActive && a.tile.input.pressedKeys.size === 0
            && a.tile.input.buttons === 0 && h.document.pointerLockElement === null
            && !a.channel.sent.some(packet => packet.code === "Escape"),
            `Escape key fallback releases mouseLockRequested=${mouseLockRequested} before form-focus and gameplay key gates`);
        await released(h, a, lease.leaseId);
        const afterEscape = a.channel.sent.length; h.key("KeyS");
        check(a.channel.sent.length === afterEscape, `released mouseLockRequested=${mouseLockRequested} remains inert until a fresh click`);
        await h.take(a); await h.grant(a, `click-again-${mouseLockRequested}`);
        check(a.tile.controlActive && (h.document.pointerLockElement === a.video) === mouseLockRequested,
            `fresh click after Escape reacquires the lease and requested lock for mouseLockRequested=${mouseLockRequested}`);
        await h.close();
    }

    {
        const lock = deferred();
        const h = await setup({ pointerLock: () => lock.promise }); h.apply([session("a", { mouseLockRequested: true })]);
        const a = await h.connect("a"); a.video.click();
        check(h.pointerLockRequests.length === 1 && a.channel.sent.length === 0,
            "deferred pointer-lock permission is requested during the activation click without forwarding input");
        await settle(); const request = sent(a, "control.request").at(-1);
        const escape = h.key("Escape"); await settle();
        h.document.pointerLockElement = a.video; h.document.emit("pointerlockchange"); lock.resolve(); await settle();
        check(escape.defaultPrevented && h.document.pointerLockElement === null && a.channel.sent.length === 0,
            "Escape during acquisition rejects a later pointer-lock success before any lease exists");
        const late = await h.grant(a, "escaped-late", request.requestId);
        check(!a.tile.controlActive && a.channel.sent.length === 0 && sent(a, "control.heartbeat").length === 0
            && sent(a, "control.release").at(-1)?.leaseId === late.leaseId,
            "late grant after pending Escape is released using its own lease without input or heartbeat");
        await released(h, a, late.leaseId);
        check(!h.sandbox.playtestopsStreaming.switchingControl && sent(a, "control.request").length === 1,
            "settling the canceled grant does not silently reacquire control");
        await h.take(a); await h.grant(a, "after-pending-escape");
        check(a.tile.controlActive && sent(a, "control.request").length === 2,
            "fresh click after a canceled pending grant acquires a new lease");
        await h.close();
    }

    {
        const { h, a } = await group(1, { mouseLockRequested: true });
        await h.take(a); const request = sent(a, "control.request").at(-1);
        h.document.exitPointerLock(); await settle();
        await h.grant(a, "unlock-before-grant", request.requestId);
        check(!a.tile.controlActive && a.channel.sent.length === 0 && sent(a, "control.release").length === 1,
            "browser default pointer-lock exit cancels control even while the lease grant is pending");
        await h.close();
    }

    {
        const { h, a, b } = await group(2, { mouseLockRequested: false });
        const lease = await own(h, a, "cursor-live"); h.key("KeyW");
        await h.message(b, { type: "runtime.cursor", mouseLockRequested: true });
        check(h.pointerLockRequests.length === 0 && b.tile.snapshot().mouseLockRequested && !b.tile.controlActive,
            "unfocused runtime cursor intent cannot capture the browser mouse or acquire control");
        await h.message(a, { type: "runtime.cursor", mouseLockRequested: true });
        check(a.tile.snapshot().mouseLockRequested && h.document.pointerLockElement === a.video && h.pointerLockRequests.length === 1,
            "current runtime cursor intent enables mouse lock for the already controlled player");
        await h.refresh([session("a", { mouseLockRequested: false }), session("b", { index: 1, mouseLockRequested: false })]);
        check(a.tile.snapshot().mouseLockRequested && h.document.pointerLockElement === a.video && b.tile.snapshot().mouseLockRequested,
            "stale HTTP cursor snapshots cannot overwrite the live viewer cursor intent");
        await h.message(a, { type: "runtime.cursor", mouseLockRequested: "false" });
        check(a.tile.snapshot().mouseLockRequested && h.document.pointerLockElement === a.video,
            "cursor strings cannot masquerade as a boolean unlock request");
        await h.message(a, { type: "runtime.cursor", mouseLockRequested: false });
        check(!a.tile.snapshot().mouseLockRequested && h.document.pointerLockElement === null && a.tile.controlActive
            && a.tile.input.pressedKeys.has("KeyW") && sent(a, "control.release").length === 0,
            "game menu cursor unlock retains the lease and held keyboard input without triggering Escape release");
        h.key("Escape"); await settle(); await released(h, a, lease.leaseId);
        const count = h.pointerLockRequests.length;
        await h.message(a, { type: "runtime.cursor", mouseLockRequested: true });
        check(h.pointerLockRequests.length === count && !a.tile.controlActive,
            "runtime cursor updates after Escape cannot restore control or capture the mouse");
        await h.close();
    }

    {
        const locks = new Map();
        const h = await setup({ pointerLock: video => {
            const pending = deferred(); locks.set(video, pending); return pending.promise;
        } });
        h.apply(["a", "b", "c"].map((id, index) => session(id, { index, mouseLockRequested: true })));
        const a = await h.connect("a"), b = await h.connect("b"), c = await h.connect("c");
        await h.take(a);
        h.document.pointerLockElement = a.video; h.document.emit("pointerlockchange"); locks.get(a.video).resolve();
        const leaseA = await h.grant(a, "lock-handoff-a"); h.key("KeyW");
        b.video.click(); c.video.click(); await settle();
        check(h.pointerLockRequests.length === 3 && h.pointerLockRequests.every((item, index) => item.video === [a.video, b.video, c.video][index])
            && !a.tile.controlActive && a.tile.input.pressedKeys.size === 0 && sent(c, "control.request").length === 0,
            "rapid A to B to C clicks request each intended lock synchronously while retaining the A release barrier");
        h.document.pointerLockElement = b.video; h.document.emit("pointerlockchange"); locks.get(b.video).resolve(); await settle();
        check(h.document.pointerLockElement === null && !b.tile.controlActive && b.channel.sent.length === 0,
            "late B pointer-lock success cannot activate or retain mouse capture after focus moved to C");
        h.document.pointerLockElement = c.video; h.document.emit("pointerlockchange"); locks.get(c.video).resolve(); await settle();
        check(h.document.pointerLockElement === c.video && c.channel.sent.length === 0,
            "latest focused C may retain provisional mouse lock while all gameplay input waits for the lease");
        await released(h, a, leaseA.leaseId); await h.grant(c, "lock-handoff-c");
        check(c.tile.controlActive && h.document.pointerLockElement === c.video && sent(b, "control.request").length === 0,
            "confirmed release grants only C control after overlapping lock requests");
        await h.close();
    }

    for (const cancellation of ["Escape", "disconnect", "session replacement"]) {
        const lock = deferred(); const h = await setup({ pointerLock: () => lock.promise });
        h.apply([session("a", { mouseLockRequested: true })]); const a = await h.connect("a");
        await h.take(a);
        if (cancellation === "Escape") h.key("Escape");
        else if (cancellation === "disconnect") a.tile.disconnect("fixture disconnected");
        else await h.refresh([session("a", { sessionId: "replacement-a", mouseLockRequested: true })]);
        await settle(); const count = h.pointerLockRequests.length, oldStatus = a.tile.ui.controlStatus.textContent;
        lock.reject(new Error("late permission denied")); await settle();
        check(h.pointerLockRequests.length === count && a.tile.ui.controlStatus.textContent === oldStatus && !a.tile.controlActive,
            `late pointer-lock rejection after ${cancellation} cannot restart capture or replace the current status`);
        if (cancellation !== "session replacement") {
            h.document.pointerLockElement = a.video; h.document.emit("pointerlockchange");
            check(h.document.pointerLockElement === null,
                `late pointer-lock success after ${cancellation} is immediately released`);
        }
        await h.close();
    }

    {
        const pending = [];
        const h = await setup({ pointerLock: () => { const next = deferred(); pending.push(next); return next.promise; } });
        h.apply([session("a", { mouseLockRequested: true })]); const a = await h.connect("a");
        await h.take(a); const oldRequest = sent(a, "control.request").at(-1);
        h.key("Escape"); await settle();
        const oldGrant = await h.grant(a, "old-permission-generation", oldRequest.requestId);
        await released(h, a, oldGrant.leaseId); await h.take(a);
        const beforeStaleDenial = a.tile.ui.controlStatus.textContent;
        pending[0].reject(new Error("old click denied")); await settle();
        check(pending.length === 2 && a.tile.ui.controlStatus.textContent === beforeStaleDenial,
            "old permission rejection cannot overwrite status or disturb a fresh same-tile activation");
        await h.grant(a, "new-permission-generation");
        check(pending.length === 2 && a.tile.controlActive,
            "old permission completion cannot clear the new pending-request guard before its lease grant");
        h.document.pointerLockElement = a.video; h.document.emit("pointerlockchange"); pending[1].resolve(); await settle();
        check(a.tile.controlActive && h.document.pointerLockElement === a.video,
            "fresh same-tile permission completion locks only the newly acquired controller");
        await h.close();
    }

    for (const failure of ["promise rejection", "promise rejection before grant", "synchronous exception"]) {
        const pending = deferred();
        const h = await setup({ pointerLock: () => { if (failure === "synchronous exception") throw new Error("fixture denied"); return pending.promise; } });
        h.apply([session("a", { mouseLockRequested: true })]); const a = await h.connect("a");
        await h.take(a);
        if (failure === "promise rejection before grant") { pending.reject(new Error("fixture denied early")); await settle(); }
        await h.grant(a, `denied-${failure}`);
        if (failure === "promise rejection") { pending.reject(new Error("fixture denied")); await settle(); }
        h.document.emit("pointerlockerror");
        check(a.tile.controlActive && h.document.pointerLockElement === null && h.pointerLockRequests.length === 1
            && /click.*retry.*escape/i.test(a.tile.ui.controlStatus.textContent),
            `${failure} leaves keyboard control available and explains a fresh click without an automatic request loop`);
        await h.message(a, { type: "runtime.cursor", mouseLockRequested: true });
        check(h.pointerLockRequests.length === 1,
            `${failure} cannot be retried by a duplicate runtime cursor notification without a fresh activation`);
        const beforeActivation = a.channel.sent.length;
        a.video.emit("pointerdown", { button: 0, pointerId: 1, clientX: 40, clientY: 40 });
        h.document.emit("pointerup", { button: 0, clientX: 40, clientY: 40 });
        a.video.click(); await settle();
        check(a.channel.sent.length === beforeActivation && h.pointerLockRequests.length === 2
            && sent(a, "control.request").length === 1, `${failure} retry click requests mouse lock without shooting or replacing the lease`);
        h.key("KeyW"); check(a.tile.input.pressedKeys.has("KeyW"), `${failure} does not disable granted keyboard input`);
        h.key("Escape"); await settle(); await h.close();
    }

    {
        const { h, a } = await group(1, { mouseLockRequested: true });
        await h.take(a); const request = sent(a, "control.request").at(-1);
        await h.message(a, { type: "control.denied", requestId: request.requestId, reason: "fixture busy" });
        check(!a.tile.controlActive && h.document.pointerLockElement === null && a.channel.sent.length === 0,
            "denied control grant releases the provisional mouse lock without forwarding input");
        await h.close();
    }

    for (const instanceCount of [1, 2, 3, 4]) {
        const h = await setup();
        h.node("build").value = "test-build"; h.node("build").emit("change");
        h.node("instance-count").value = String(instanceCount);
        const records = Array.from({ length: instanceCount }, (_, index) => session(`start-${index}`, { index }));
        h.state({ builds: [], sessions: records }); h.startReply({ sessions: records });
        h.node("start").click(); await settle();
        const request = h.requests.find(item => item.method === "POST" && item.url.endsWith("/start"));
        const body = request && JSON.parse(request.body);
        check(body?.instanceCount === instanceCount && body.launchProfile === (instanceCount === 1 ? "single" : "arena-kcp"), `start ${instanceCount} posts the explicit instance count and matching launch profile`);
        check(request.credentials === "same-origin" && request.headers.RequestVerificationToken === "fake-antiforgery-secret", "start retains same-origin antiforgery protection");
        check(h.api.tiles.size === instanceCount, `start response creates ${instanceCount} independently identified tiles`);
        await h.close();
    }

    {
        const { h, viewers, a, b, c } = await group(4);
        check(new Set(viewers.map(viewer => viewer.socket)).size === 4 && new Set(viewers.map(viewer => viewer.peer)).size === 4 && new Set(viewers.map(viewer => viewer.tile.input)).size === 4,
            "four tiles own separate sockets, peers, and input state");
        check(h.document.querySelectorAll(".streaming-tile").length === 4 && h.document.querySelectorAll(".streaming-video").length === 4, "four DOM tiles contain four independent video elements");
        check(h.document.getElementById("streaming-video") === a.video && viewers.every(viewer => viewer.tile.node.dataset.instanceId === viewer.record.instanceId), "first-video compatibility ID and tile identities are retained");
        await h.take(a);
        check(h.sandbox.playtestopsStreaming.focusedInstanceId === "a" && sent(a, "control.request").length === 1
            && viewers.slice(1).every(viewer => sent(viewer, "control.request").length === 0),
            "first video click automatically requests control only for its focused tile");
        const aRequest = sent(a, "control.request")[0];
        check(/^[0-9a-f]{32}$/.test(aRequest.requestId), "control request carries a 32-character hexadecimal correlation ID");
        await h.message(a, { type: "control.granted", sessionId: b.record.sessionId, instanceId: b.record.instanceId,
            requestId: aRequest.requestId, leaseId: "foreign", leaseToken: "fake-foreign-secret", timeoutMs: 2000 });
        check(controlled(h).length === 0 && a.channel.sent.length === 0, "foreign session and instance cannot grant a tile control");
        const lease = await h.grant(a, "lease-a");
        check(controlled(h).length === 1 && a.tile.controlActive, "only the requested tile enables its lease");
        h.key("KeyW");
        a.video.emit("pointerdown", { button: 0, pointerId: 1, clientX: 640, clientY: 360 });
        check(a.tile.input.pressedKeys.has("KeyW") && a.tile.input.buttons !== 0 && b.channel.sent.length === 0, "keyboard and pointer input reach only the controlled tile");
        a.video.emit("pointermove", { clientX: 300, clientY: 200 });
        b.tile.node.click(); c.tile.node.click(); await settle();
        const countAfterSwitch = a.channel.sent.length;
        h.animationFrame(); h.key("KeyS");
        check(a.channel.sent.length === countAfterSwitch && !a.tile.input.hasControl() && a.tile.input.pressedKeys.size === 0 && a.tile.input.buttons === 0,
            "focus change clears old held state and cancels queued pointer input immediately");
        check(sent(a, "control.release").length === 1 && sent(b, "control.request").length === 0 && sent(c, "control.request").length === 0,
            "A to B to C emits one A release and requests no new lease before acknowledgement");
        await h.message(a, { type: "control.revoked", leaseId: lease.leaseId, reason: "release pending" });
        await released(h, a, "wrong-lease");
        check(sent(c, "control.request").length === 0 && controlled(h).length === 0, "revocation and an unrelated release ACK cannot open the handoff barrier");
        await released(h, a, lease.leaseId);
        check(sent(b, "control.request").length === 0 && sent(c, "control.request").length === 1, "matching runtime release ACK selects only the latest destination C");
        const leaseC = await h.grant(c, "lease-c");
        h.key("KeyS");
        check(controlled(h).length === 1 && c.tile.controlActive && c.channel.sent.some(packet => packet.kind === "key" && packet.code === "KeyS"), "C alone accepts input after the confirmed handoff");
        const releaseIndex = h.timeline.findIndex(item => item.packet.type === "control.release" && item.packet.leaseId === lease.leaseId);
        const requestIndex = h.timeline.findIndex(item => item.socket === c.socket && item.packet.type === "control.request");
        check(releaseIndex >= 0 && requestIndex > releaseIndex, "old lease release precedes the new control request in the actual signaling trace");
        await h.advance(500);
        check(sent(a, "control.heartbeat").length === 0 && sent(b, "control.heartbeat").length === 0 && sent(c, "control.heartbeat").length === 1,
            "only the active tile renews control");
        await h.message(c, { type: "control.granted", ...leaseC });
        check(c.tile.controlActive && sent(c, "control.release").length === 0, "duplicate current grant neither replaces nor releases the active lease");
        h.key("Escape"); await settle();
        check(controlled(h).length === 0 && sent(c, "control.release").length === 1, "Escape neutralizes local input before runtime acknowledgement");
        await released(h, c, leaseC.leaseId);
        await h.close();
        check(h.timers.size === 0 && h.frames.size === 0 && viewers.every(viewer => viewer.socket.readyState === 3 && viewer.peer.connectionState === "closed"),
            "pagehide disposes every tile socket, peer, timer, and frame callback");
    }

    {
        const { h, a, b, c } = await group();
        const leaseA = await own(h, a, "pending-a");
        b.tile.node.click(); await settle(); await released(h, a, leaseA.leaseId);
        const bRequest = sent(b, "control.request").at(-1);
        check(!!bRequest, "B may request after A's release is confirmed");
        c.tile.node.click(); await settle();
        await h.message(b, { type: "control.denied", requestId: "0".repeat(32), reason: "unrelated response" });
        check(sent(c, "control.request").length === 0, "unrelated denial cannot complete B's pending acquisition");
        const stale = await h.grant(b, "stale-b", bRequest.requestId);
        const release = sent(b, "control.release").at(-1);
        check(release?.leaseId === stale.leaseId && release.leaseToken === stale.leaseToken, "late B grant is released using its own credentials");
        check(!b.tile.input.hasControl() && b.channel.sent.length === 0 && sent(b, "control.heartbeat").length === 0,
            "late B grant never enables input, sends initial state, or starts lease renewal");
        check(sent(c, "control.request").length === 0, "C waits while stale B credentials await runtime release");
        await h.message(b, { type: "control.revoked", leaseId: stale.leaseId });
        check(sent(c, "control.request").length === 0, "immediate stale-grant revocation is not a runtime-neutral acknowledgement");
        await released(h, b, stale.leaseId);
        check(sent(c, "control.request").length === 1, "C requests only after stale B grant release is acknowledged");
        await h.grant(c, "latest-c");
        check(controlled(h).length === 1 && c.tile.controlActive, "pending-grant A to B to C race converges on C alone");
        await h.close();
    }

    {
        const { h, a, b } = await group(2);
        const firstLease = await own(h, a, "return-a-first");
        b.tile.node.click(); a.tile.node.click(); await settle();
        check(sent(a, "control.release").length === 1 && sent(a, "control.request").length === 1 && !a.tile.controlActive,
            "A to B to A cannot reactivate A's already-neutralized lease");
        await released(h, a, firstLease.leaseId);
        check(sent(a, "control.request").length === 2 && sent(b, "control.request").length === 0, "returning to A reacquires only after the old A lease is released");
        await h.grant(a, "return-a-second");
        check(a.tile.controlActive && controlled(h).length === 1, "fresh A lease restores exactly one controller");
        const freshState = a.channel.sent.at(-1);
        h.key("KeyW");
        const beforeLateReply = JSON.stringify(a.tile.input.snapshot());
        for (const leaseId of [firstLease.leaseId, null, undefined]) {
            a.channel.emit("message", { data: JSON.stringify({ type: "input.error", sessionId: a.record.sessionId,
                instanceId: "a", leaseId, reason: "unauthorized_input" }) });
            a.channel.emit("message", { data: JSON.stringify({ type: "input.ack", sessionId: a.record.sessionId,
                instanceId: "a", leaseId, seq: freshState.seq, clientSentMs: freshState.clientSentMs }) });
        }
        await settle();
        check(a.tile.controlActive && a.tile.input.pressedKeys.has("KeyW") && sent(a, "control.release").length === 1
            && JSON.stringify(a.tile.input.snapshot()) === beforeLateReply,
            "old or missing lease replies cannot release a reacquired tile or alter its counters and held input");
        a.channel.emit("message", { data: JSON.stringify({ type: "input.ack", sessionId: a.record.sessionId,
            instanceId: "a", leaseId: freshState.leaseId, seq: freshState.seq, clientSentMs: freshState.clientSentMs + 1e-10 }) });
        check(a.tile.input.snapshot().ackedCount === 1 && a.tile.input.snapshot().ackRejectedCount === 0,
            "current lease ACK accepts a tiny timestamp representation difference through the real viewer channel");
        a.channel.emit("message", { data: JSON.stringify({ type: "input.error", sessionId: a.record.sessionId,
            instanceId: "a", leaseId: freshState.leaseId, reason: "invalid_input" }) });
        await settle();
        check(!a.tile.controlActive && !a.tile.input.pressedKeys.has("KeyW") && a.tile.input.snapshot().runtimeErrorCount === 1
            && sent(a, "control.release").at(-1).leaseId === freshState.leaseId,
            "current lease runtime error still counts and triggers the viewer's fail-safe release");
        await h.close();
    }

    {
        const { h, a, b } = await group(2);
        const lease = await own(h, a, "timeout-a");
        b.tile.node.click(); await settle(); await h.advance(12001);
        check(sent(b, "control.request").length === 0 && controlled(h).length === 0, "missing release acknowledgement times out without granting B input");
        await h.take(b);
        check(sent(b, "control.request").length === 0, "retry cannot bypass an unresolved runtime release barrier");
        await released(h, a, lease.leaseId); await h.take(b);
        check(sent(b, "control.request").length === 1, "late matching release acknowledgement permits an explicit retry");
        await h.grant(b, "timeout-recovered-b"); await h.close();
    }

    {
        const h = await setup(); h.apply([session("a")]);
        const delayed = h.deferFetch("/session-a/viewer-grant");
        const oldTile = h.tile("a"), connecting = oldTile.connect(); await settle();
        await h.refresh([session("a", { sessionId: "new-session-a" })]);
        delayed.resolve({ sessionId: "session-a", instanceId: "a", token: "fake-obsolete-viewer-secret", viewerWebSocketPath: "/api/streaming/viewer/session-a" });
        await connecting; await settle();
        check(h.sockets.length === 0 && h.tile("a") !== oldTile, "late viewer grant cannot create a socket for a replaced session");
        const viewer = await h.connect("a");
        check(viewer.socket.url.endsWith("new-session-a"), "replacement viewer authenticates only against its new session");
        await h.close();
    }

    {
        const { h, a, b } = await group(2);
        const pendingStats = deferred(); a.peer.getStats = () => pendingStats.promise;
        await h.advance(1000);
        const oldFrame = h.callbacks(a.video)[0], oldInput = a.tile.input;
        await h.refresh([session("a", { sessionId: "fresh-a" }), session("b", { index: 1 })]);
        const replacement = await h.connect("a");
        await own(h, replacement, "replacement-a");
        pendingStats.resolve(new Map([["old-video", { type: "inbound-rtp", kind: "video", framesPerSecond: 999, frameWidth: 9999, frameHeight: 9999 }]]));
        a.channel.emit("close"); a.channel.emit("message", { data: JSON.stringify({ type: "input.error", sessionId: "session-a", instanceId: "a", reason: "unauthorized_input" }) });
        a.track.emit("ended"); oldFrame(5000, { expectedDisplayTime: 5000 });
        await h.message(a, { type: "runtime.ready", width: 8888, height: 8888 });
        let orphanClosed = false;
        a.peer.emit("datachannel", { channel: { close() { orphanClosed = true; } } });
        check(orphanClosed && replacement.tile.controlActive && replacement.tile.input !== oldInput,
            "stale channel, track, and peer events cannot disturb the replacement tile's lease");
        check(!h.metric(replacement.tile, "fps").includes("999") && !h.metric(replacement.tile, "resolution").includes("8888"),
            "late stats and signaling cannot overwrite replacement metrics");
        check(b.peer.connectionState === "connected" && b.socket.readyState === 1 && h.callbacks(b.video).length === 1,
            "replacing A preserves B's peer, socket, and marker reader");
        replacement.tile.disconnect("fixture reconnect"); await settle();
        const reconnected = await h.connect("a");
        await h.take(reconnected); await h.grant(reconnected, "reconnected-a");
        replacement.channel.emit("close"); replacement.channel.emit("error");
        await settle();
        check(reconnected.tile.controlActive && reconnected.tile.input.hasControl(), "old channel events cannot clear a same-tile reconnected input lease");
        await h.close();
    }

    {
        const { h, a, b } = await group(2);
        const stats = (id, fps, width, decoded, telemetry, mimeType) => new Map([
            [`${id}-decoy-before`, { type: "codec", mimeType: "video/AV1" }],
            [`${id}-codec`, { type: "codec", mimeType }],
            [id, { id, type: "inbound-rtp", kind: "video", timestamp: 1000, codecId: `${id}-codec`,
            framesPerSecond: fps, frameWidth: width, frameHeight: 720, framesDecoded: decoded, framesDropped: decoded / 10,
            bytesReceived: decoded * 100, jitterBufferDelay: 0.2, jitterBufferEmittedCount: decoded, totalDecodeTime: 0.1, ...telemetry }],
            [`${id}-decoy-after`, { type: "codec", mimeType: "video/VP9" }]]);
        a.peer.stats = stats("stats-a", 30, 1280, 90, { decoderImplementation: "D3D11VideoDecoder", powerEfficientDecoder: false,
            packetsReceived: 0, packetsLost: 0, jitter: 0 }, "video/H264");
        b.peer.stats = stats("stats-b", 60, 1920, 180, { decoderImplementation: "libvpx", powerEfficientDecoder: true,
            packetsReceived: 180, packetsLost: 3, jitter: 0.012 }, "video/VP8");
        a.video.getVideoPlaybackQuality = () => ({ totalVideoFrames: 95, droppedVideoFrames: 5 });
        b.video.getVideoPlaybackQuality = () => ({ totalVideoFrames: 181, droppedVideoFrames: 1 });
        await h.advance(1000);
        const leaseA = await own(h, a, "metrics-a");
        h.now(2000); h.key("KeyW"); const keyA = a.channel.sent.find(packet => packet.kind === "key");
        h.now(2030); a.channel.emit("message", { data: JSON.stringify({ type: "input.ack", sessionId: a.record.sessionId, instanceId: "a", leaseId: keyA.leaseId, seq: keyA.seq, clientSentMs: keyA.clientSentMs }) });
        h.pixels(b.video, markerPixels(keyA.diagnosticId)); h.frame(b.video, { expectedDisplayTime: 2050 });
        check(b.tile.input.snapshot().frameSamples.length === 0, "A's marker ID displayed on B cannot create a B latency sample");
        h.pixels(a.video, markerPixels(keyA.diagnosticId)); h.frame(a.video, { expectedDisplayTime: 2050 });
        check(a.tile.input.snapshot().frameSamples.length === 1 && a.tile.input.snapshot().frameSamples[0].ms === 50,
            "only A's own presented marker matches A's actual input dispatch");
        const frozen = h.sandbox.playtestopsStreaming;
        const snapA = frozen.tiles.find(tile => tile.instanceId === "a"), snapB = frozen.tiles.find(tile => tile.instanceId === "b");
        check(snapA.video.fps === 30 && snapB.video.fps === 60 && snapA.video.framesDecoded === 90 && snapB.video.framesDecoded === 180,
            "tile FPS and decode counters remain isolated");
        check(snapA.video.framesDropped === 9 && snapB.video.bytesReceived === 18000 && snapA.video.totalVideoFrames === 95 && snapB.video.droppedVideoFrames === 1,
            "diagnostics preserve per-tile RTC and presentation counters");
        check(snapA.video.codecMimeType === "video/H264" && snapB.video.codecMimeType === "video/VP8",
            "each tile resolves the codec referenced by its inbound codecId instead of guessing from other codec reports");
        check(snapA.video.decoderImplementation === "D3D11VideoDecoder" && snapB.video.decoderImplementation === "libvpx",
            "decoder implementation strings remain isolated per tile");
        check(snapA.video.powerEfficientDecoder === false && snapB.video.powerEfficientDecoder === true,
            "decoder power-efficiency telemetry preserves both false and true");
        check(snapA.video.packetsReceived === 0 && snapA.video.packetsLost === 0 && snapA.video.jitter === 0,
            "packet and jitter telemetry preserves valid zero values");
        check(snapB.video.packetsReceived === 180 && snapB.video.packetsLost === 3 && snapB.video.jitter === 0.012,
            "packet and jitter telemetry preserves independent finite values");
        check(snapA.input.ackedCount === 1 && snapB.input.ackedCount === 0 && snapA.input.frameSamples.length === 1 && snapB.input.frameSamples.length === 0,
            "ACK RTT and input-to-video samples are not shared between tiles");
        check(Object.isFrozen(frozen) && Object.isFrozen(frozen.tiles) && Object.isFrozen(snapA.input.frameSamples) && Object.isFrozen(snapA.video),
            "public multistream diagnostics are deeply immutable");
        const serialized = JSON.stringify(frozen);
        check(!/fake-(?:lease|viewer|antiforgery).*secret|leaseToken|runtimeToken|viewerToken|antiForgeryToken/.test(serialized), "public diagnostics contain no credentials or credential fields");
        const descriptor = Object.getOwnPropertyDescriptor(h.sandbox, "playtestopsStreaming");
        check(typeof descriptor.get === "function" && !descriptor.set && !descriptor.configurable && !descriptor.enumerable,
            "public diagnostics expose a non-replaceable read-only getter");
        b.tile.node.click(); await settle(); await released(h, a, leaseA.leaseId); await h.grant(b, "metrics-b");
        check(frozen.controlledInstanceId === "a" && h.sandbox.playtestopsStreaming.controlledInstanceId === "b", "captured diagnostics remain detached after control switches");
        b.socket.close(); await settle();
        check(!b.tile.controlActive && a.peer.connectionState === "connected" && a.socket.readyState === 1,
            "disconnecting controlled B neutralizes B without closing A's viewer");
        const inboundA = a.peer.stats.get("stats-a");
        Object.assign(inboundA, { codecId: "missing-codec", decoderImplementation: 123, powerEfficientDecoder: "false",
            packetsReceived: Infinity, packetsLost: NaN, jitter: "0" });
        await h.advance(1000);
        const invalidVideo = a.tile.snapshot().video;
        check(invalidVideo.codecMimeType === null, "an unresolved codecId remains null despite other valid codec reports");
        check(invalidVideo.decoderImplementation === null && invalidVideo.powerEfficientDecoder === null,
            "wrongly typed decoder telemetry becomes null without coercion");
        check(invalidVideo.packetsReceived === null && invalidVideo.packetsLost === null && invalidVideo.jitter === null,
            "nonfinite and wrongly typed packet and jitter telemetry becomes null");
        inboundA.codecId = "stats-a-codec"; a.peer.stats.get("stats-a-codec").mimeType = 123;
        for (const field of ["decoderImplementation", "powerEfficientDecoder", "packetsReceived", "packetsLost", "jitter"]) delete inboundA[field];
        await h.advance(1000);
        const missingVideo = a.tile.snapshot().video;
        check(missingVideo.codecMimeType === null, "a referenced codec with a nonstring MIME type remains null");
        check(["decoderImplementation", "powerEfficientDecoder", "packetsReceived", "packetsLost", "jitter"].every(field => missingVideo[field] === null),
            "missing decoder, packet, and jitter telemetry is explicitly null");
        delete inboundA.codecId; a.peer.stats.get("stats-a-codec").mimeType = "video/H264";
        await h.advance(1000);
        check(a.tile.snapshot().video.codecMimeType === null, "a missing codecId cannot infer a codec from available reports");
        await h.close();
    }

    for (const failure of ["data channel", "peer"]) {
        const { h, a, b } = await group(2);
        const lease = await own(h, a, `transport-${failure}`);
        if (failure === "data channel") a.channel.close();
        else { a.peer.connectionState = "failed"; a.peer.emit("connectionstatechange"); }
        await settle();
        const release = sent(a, "control.release").at(-1);
        check(!a.tile.controlActive && release?.leaseId === lease.leaseId && release.leaseToken === lease.leaseToken,
            `${failure} failure neutralizes input and releases the old credentials through its live signaling socket`);
        await h.take(b);
        check(sent(b, "control.request").length === 0, `${failure} failure preserves the runtime release barrier before B can request control`);
        await released(h, a, lease.leaseId);
        check(sent(b, "control.request").length === 1, `${failure} release acknowledgement permits B's explicit control request`);
        await h.grant(b, `transport-recovered-${failure}`); await h.close();
    }

    {
        const h = await setup(); h.apply([session("a"), session("b", { index: 1 })]);
        const a = await h.connect("a"); const lease = await own(h, a, "not-ready-a");
        h.tile("b").node.click(); await settle(); await released(h, a, lease.leaseId);
        check(controlled(h).length === 0 && /not ready.*click.*connect/i.test(h.node("control-status").textContent), "clicking an unready tile releases old input and explains clicking again after connection");
        const b = await h.connect("b");
        check(sent(b, "control.request").length === 0 && b.tile.canControl(), "newly connected tile waits for a fresh activation click");
        await h.take(b); await h.grant(b, "now-ready-b");
        check(b.tile.controlActive, "a fresh click acquires control after the selected tile becomes ready");
        await h.close();
    }

    {
        const { h, a, b } = await group(2);
        await h.take(a); a.socket.close(); await settle();
        check(!h.sandbox.playtestopsStreaming.switchingControl && !a.tile.pendingControl && controlled(h).length === 0,
            "socket close rejects an outstanding acquisition without leaving control switching stuck");
        check(b.socket.readyState === 1 && b.peer.connectionState === "connected", "closing A during acquisition preserves B's independent viewer");
        await h.close();
    }

    for (const trigger of ["window blur", "hidden document", "pointer-lock exit"]) {
        const { h, a, b } = await group(2, { mouseLockRequested: trigger === "pointer-lock exit" });
        const lease = await own(h, a, `fail-safe-${trigger}`);
        h.key("KeyW"); a.video.emit("pointerdown", { button: 0, pointerId: 1, clientX: 100, clientY: 100 });
        if (trigger === "window blur") h.sandbox.emit("blur");
        else if (trigger === "hidden document") { h.document.hidden = true; h.document.visibilityState = "hidden"; h.document.emit("visibilitychange"); }
        else {
            h.document.pointerLockElement = a.video; h.document.emit("pointerlockchange");
            h.document.pointerLockElement = null; h.document.emit("pointerlockchange");
        }
        await settle();
        check(!a.tile.controlActive && a.tile.input.pressedKeys.size === 0 && a.tile.input.buttons === 0,
            `${trigger} DOM event immediately clears held keyboard and mouse input`);
        const afterRelease = a.channel.sent.length; h.key("KeyS");
        check(a.channel.sent.length === afterRelease && sent(a, "control.release").at(-1)?.leaseId === lease.leaseId,
            `${trigger} releases the correct lease and blocks further game input`);
        check(b.socket.readyState === 1 && b.peer.connectionState === "connected", `${trigger} preserves other tile viewers`);
        await released(h, a, lease.leaseId); await h.close();
    }

    {
        const { h, a, viewers } = await group(3);
        await own(h, a, "group-stop-a"); h.key("KeyW");
        h.state({ builds: [], sessions: viewers.map(viewer => ({ ...viewer.record, state: "Stopped", runtimeConnected: false })) });
        h.node("stop").click();
        check(!a.tile.controlActive && a.tile.input.pressedKeys.size === 0, "group Stop neutralizes held input before awaiting HTTP");
        await settle(); h.checkErrors();
        const stops = h.requests.filter(request => request.method === "POST" && request.url.endsWith("/stop"));
        check(stops.length === 1 && stops[0].url === "/api/streaming/stop" && stops[0].headers.RequestVerificationToken === "fake-antiforgery-secret",
            "group Stop uses one authenticated group-stop route");
        check(viewers.every(viewer => viewer.socket.readyState === 3 && viewer.peer.connectionState === "closed") && h.frames.size === 0,
            "successful group Stop closes all tile viewers and marker callbacks");
        await h.close();
    }

    {
        const h = await setup();
        const old = h.deferFetch("/state"); const oldRefresh = h.api.refreshState();
        await h.refresh([session("new")]);
        old.resolve({ builds: [], sessions: [session("old")] }); await oldRefresh; await settle();
        check(h.api.tiles.has("new") && !h.api.tiles.has("old"), "out-of-order state response cannot restore an obsolete session");
        await h.close();
    }
    process.stdout.write(`SUCCESS: ${checks} streaming multistream checks\n`);
}

module.exports = { setup, session, markerPixels, settle, deferred, Target, check, get checks() { return checks; } };
if (require.main === module) main().catch(error => { process.stderr.write(`${error.stack || error}\n`); process.exitCode = 1; });
