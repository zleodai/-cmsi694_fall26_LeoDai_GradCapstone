(function () {
    "use strict";
    const API = "/api/streaming";
    const ALLOWED_KEYS = new Set([
        "KeyW", "KeyA", "KeyS", "KeyD", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
        "Space", "ShiftLeft", "ShiftRight", "ControlLeft", "ControlRight", "KeyE", "KeyF",
        "KeyQ", "KeyR", "KeyC", "KeyX", "KeyZ", "Enter"
    ]);
    const $ = id => document.getElementById(id);
    const ui = {
        alert: $("streaming-alert"), build: $("streaming-build"), buildNote: $("streaming-build-note"),
        width: $("streaming-width"), height: $("streaming-height"), diagnostics: $("streaming-diagnostics"),
        instanceCount: $("streaming-instance-count"), profileField: $("streaming-profile-field"), profile: $("streaming-profile"),
        steamSandboxes: $("streaming-steam-sandboxes"), steamSandboxOptions: $("streaming-steam-sandbox-options"),
        steamSandboxNote: $("streaming-steam-sandbox-note"), start: $("streaming-start"), refresh: $("streaming-refresh"),
        latency: $("streaming-latency"), latencyMin: $("streaming-latency-min"), latencyMax: $("streaming-latency-max"),
        latencyApply: $("streaming-latency-apply"), latencyReset: $("streaming-latency-reset"),
        latencyStatus: $("streaming-latency-status"), latencyApplied: $("streaming-latency-applied"),
        latencySummary: $("streaming-latency-summary"), latencyLinks: $("streaming-latency-links"),
        connect: $("streaming-connect"), stop: $("streaming-stop"), sessionSummary: $("streaming-session-summary"),
        controlStatus: $("streaming-control-status"),
        takeover: $("streaming-manual-takeover"), log: $("streaming-log"), tiles: $("streaming-tiles"),
        noStreams: $("streaming-no-streams"), setup: $("streaming-setup")
    };
    const antiForgeryToken = document.querySelector('#streaming-antiforgery input[name="__RequestVerificationToken"]')?.value ?? "";
    const tiles = new Map();
    let state = { builds: [], sessions: [] };
    let networkLatency = null;
    let latencyGroupId = null;
    let latencyInputGroupId = null;
    let latencyBusy = false;
    let latencyRequestGeneration = 0;
    let latencyNotice = null;
    let steamEnvironments = { environments: [], available: false, reason: "Loading configured Sandboxie boxes…" };
    let steamEnvironmentsLoaded = false;
    let steamEnvironmentLoad = null;
    let steamEnvironmentRefreshQueued = false;
    let focusedInstanceId = null;
    let manualIntent = false;
    let focusRevision = 0;
    let controlWork = null;
    let busy = false;
    let stopping = false;
    let statePoll = 0;
    let refreshRequest = 0;
    let appliedRefresh = 0;
    let operationGeneration = 0;
    let pageClosed = false;
    let requestSequence = 0;

    function localOnly() {
        const host = location.hostname.toLowerCase();
        return host === "localhost" || host === "127.0.0.1" || host === "::1" || host === "[::1]";
    }

    function showAlert(message, kind = "error") {
        ui.alert.textContent = message;
        ui.alert.dataset.kind = kind;
        ui.alert.hidden = !message;
    }

    function clearAlert() {
        ui.alert.textContent = "";
        ui.alert.hidden = true;
        delete ui.alert.dataset.kind;
    }

    async function responseJson(response) {
        const text = await response.text();
        let body = {};
        if (text) {
            try { body = JSON.parse(text); }
            catch { body = { message: text }; }
        }
        if (!response.ok) {
            const detail = body.detail || body.message || body.error || `HTTP ${response.status}`;
            throw new Error(detail);
        }
        return body;
    }

    async function getJson(path) {
        if (!localOnly()) throw new Error("Streaming is available from this machine only. Open the dashboard through localhost.");
        return responseJson(await fetch(path, { method: "GET", cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } }));
    }

    async function postJson(path, body = {}) {
        if (!localOnly()) throw new Error("Streaming is available from this machine only. Open the dashboard through localhost.");
        if (!antiForgeryToken) throw new Error("The request verification token is missing. Reload the dashboard and retry.");
        return responseJson(await fetch(path, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "Content-Type": "application/json",
                "Accept": "application/json",
                "RequestVerificationToken": antiForgeryToken
            },
            body: JSON.stringify(body)
        }));
    }

    function fieldText(value, fallback = "—") {
        return value === null || value === undefined || value === "" ? fallback : String(value);
    }

    function buildId(build) {
        return build?.buildId ?? build?.id ?? "";
    }

    function buildLabel(build) {
        const project = build.projectName || "Unity project";
        const target = build.activeTarget || build.target || "Player";
        const id = buildId(build);
        const finished = build.finishedUtc ? window.PlaytestOpsTime.formatUtc(build.finishedUtc) : "completed build";
        return `${project} · ${target} · ${finished} · ${String(id).slice(0, 8)}`;
    }

    function selectedBuild() {
        return state.builds.find(build => String(buildId(build)) === ui.build.value) || null;
    }

    function supportsSteam(build) { return build?.supportsArenaSteam === true; }
    function supportsKcp(build) { return build?.supportsArenaKcp === true; }
    function usesSteamProfile() {
        const build = selectedBuild();
        if (!supportsSteam(build)) return false;
        return !supportsKcp(build) || ui.profile.value === "arena-steam";
    }

    function selectedSteamSandboxes() {
        return [...ui.steamSandboxOptions.querySelectorAll("input[type=checkbox]")]
            .filter(input => input.checked && !input.disabled).map(input => input.value);
    }

    function updatePlayerOptions(minimum) {
        for (const option of ui.instanceCount.options) option.disabled = Number(option.value) < minimum;
        if (Number(ui.instanceCount.value) < minimum) ui.instanceCount.value = String(minimum);
    }

    function populateSteamSandboxes() {
        const selected = new Set([...ui.steamSandboxOptions.querySelectorAll("input[type=checkbox]")]
            .filter(input => input.checked).map(input => input.value.toLowerCase()));
        ui.steamSandboxOptions.replaceChildren();
        for (const environment of steamEnvironments.environments) {
            if (!environment || typeof environment.name !== "string" || !environment.name) continue;
            const label = document.createElement("label");
            label.className = "streaming-steam-box";
            label.dataset.available = String(environment.available === true);
            const checkbox = document.createElement("input");
            checkbox.type = "checkbox";
            checkbox.value = environment.name;
            checkbox.dataset.available = String(environment.available === true);
            checkbox.disabled = environment.available !== true;
            checkbox.checked = !checkbox.disabled && selected.has(environment.name.toLowerCase());
            checkbox.addEventListener("change", syncSteamSelection);
            const name = document.createElement("span");
            name.textContent = environment.name + (environment.available === true ? "" : " — " + (environment.reason || "Unavailable"));
            label.append(checkbox, name);
            ui.steamSandboxOptions.append(label);
        }
        syncSteamSelection();
    }

    function configurePlayModes() {
        const build = selectedBuild();
        const steam = supportsSteam(build);
        const kcp = supportsKcp(build);
        const previous = ui.profile.value;
        ui.profile.replaceChildren();
        const addMode = (value, label) => {
            const option = document.createElement("option");
            option.value = value; option.textContent = label; ui.profile.append(option);
        };
        if (steam && kcp) {
            addMode("single", "Single player");
            addMode("arena-kcp", "Local multiplayer");
            addMode("arena-steam", "Steam multiplayer");
            ui.profileField.hidden = false;
            ui.profile.value = ["single", "arena-kcp", "arena-steam"].includes(previous)
                ? previous : Number(ui.instanceCount.value) === 1 ? "single" : "arena-kcp";
            if (ui.profile.value === "single") {
                updatePlayerOptions(1);
                ui.instanceCount.value = "1";
            } else updatePlayerOptions(2);
        } else if (steam) {
            addMode("arena-steam", "Steam multiplayer");
            ui.profileField.hidden = false;
            ui.profile.value = "arena-steam";
            updatePlayerOptions(2);
        } else {
            ui.profileField.hidden = true;
            updatePlayerOptions(1);
        }
        syncSteamSelection();
    }

    function syncSteamSelection() {
        const steam = usesSteamProfile();
        ui.steamSandboxes.hidden = !steam;
        const checkboxes = [...ui.steamSandboxOptions.querySelectorAll("input[type=checkbox]")];
        const required = Math.max(0, Number(ui.instanceCount.value) - 1);
        const checked = checkboxes.filter(input => input.checked && input.dataset.available !== "false");
        const available = checkboxes.filter(input => input.dataset.available !== "false").length;
        for (const input of checkboxes) {
            input.disabled = steam && (!steamEnvironmentsLoaded || !steamEnvironments.available || input.dataset.available === "false" ||
                (!input.checked && checked.length >= required));
        }
        if (!steam) {
            ui.steamSandboxNote.textContent = "";
            syncButtons();
            return;
        }
        if (!steamEnvironmentsLoaded) ui.steamSandboxNote.textContent = steamEnvironments.reason || "Loading configured Sandboxie boxes…";
        else if (!steamEnvironments.available) ui.steamSandboxNote.textContent = steamEnvironments.reason || "Steam sessions are unavailable.";
        else if (available < required) ui.steamSandboxNote.textContent = steamEnvironments.reason || `This session needs ${required} available Sandboxie box${required === 1 ? "" : "es"}; ${available} are available.`;
        else if (checked.length !== required) ui.steamSandboxNote.textContent = `Select exactly ${required} Sandboxie box${required === 1 ? "" : "es"} for the client${required === 1 ? "" : "s"}.`;
        else ui.steamSandboxNote.textContent = `The normal Steam session is the host. ${checked.length} selected sandbox client${checked.length === 1 ? "" : "s"} will launch.`;
        syncButtons();
    }

    function loadSteamEnvironments(forceRefresh = false) {
        if (steamEnvironmentLoad) {
            if (forceRefresh) steamEnvironmentRefreshQueued = true;
            return steamEnvironmentLoad;
        }
        steamEnvironmentLoad = (async () => {
            do {
                steamEnvironmentRefreshQueued = false;
                try {
                    const response = await getJson(API + "/steam-environments");
                    steamEnvironments = {
                        environments: Array.isArray(response.environments) ? response.environments : [],
                        available: response.available === true,
                        reason: typeof response.reason === "string" ? response.reason : ""
                    };
                } catch (error) {
                    steamEnvironments = { environments: [], available: false, reason: summarizeError(error) };
                }
                steamEnvironmentsLoaded = true;
                populateSteamSandboxes();
            } while (steamEnvironmentRefreshQueued && !pageClosed);
        })().finally(() => {
            steamEnvironmentLoad = null;
            if (steamEnvironmentRefreshQueued && !pageClosed) void loadSteamEnvironments();
        });
        return steamEnvironmentLoad;
    }

    function populateBuilds(builds) {
        const selected = ui.build.value;
        ui.build.replaceChildren();
        const placeholder = document.createElement("option");
        placeholder.value = "";
        placeholder.textContent = builds.length ? "Choose a successful build" : "No successful stream-capable builds";
        ui.build.append(placeholder);

        for (const build of builds) {
            const option = document.createElement("option");
            option.value = buildId(build);
            option.disabled = build.streamingCapable === false;
            option.dataset.reason = build.reason || "";
            option.textContent = option.disabled && option.dataset.reason
                ? `${buildLabel(build)} — ${option.dataset.reason}`
                : buildLabel(build);
            ui.build.append(option);
        }
        if (selected && [...ui.build.options].some(option => option.value === selected && !option.disabled)) ui.build.value = selected;
        updateBuildNote();
    }

    function updateBuildNote() {
        const option = ui.build.selectedOptions[0];
        ui.buildNote.textContent = option?.dataset.reason || (ui.build.value ? "Build artifact is available for local streaming." : "");
        configurePlayModes();
        syncButtons();
    }

    function terminalSession(session) {
        if (!session || typeof session.sessionId !== "string" || !session.sessionId) return false;
        const status = String(session.state || "").toLowerCase();
        return session.exited === true || status === "stopped" || status === "terminalstopped" || status === "exited";
    }

    function activeSession(session) {
        if (!session || typeof session.sessionId !== "string" || !session.sessionId) return false;
        return !terminalSession(session);
    }

    function hasSessionRecord(session) {
        return !!session && typeof session.sessionId === "string" && session.sessionId.length > 0;
    }

    function runtimeReady(session) {
        return activeSession(session) && String(session.state || "").toLowerCase() === "ready";
    }

    function latencySessions() {
        return [...tiles.values()].filter(tile => activeSession(tile.record) &&
            (tile.record.profile === "arena-kcp" || tile.record.launchProfile === "arena-kcp"));
    }

    function latencyGroupSessions(groupId) {
        return latencySessions().filter(tile => String(tile.record.groupId || "") === groupId);
    }

    function latencyRevision(snapshot) {
        return Number.isSafeInteger(snapshot?.revision) && snapshot.revision >= 0 ? snapshot.revision : null;
    }

    function commitLatencySnapshot(snapshot) {
        if (!snapshot || typeof snapshot !== "object") return false;
        const incomingGroup = String(snapshot.groupId || "");
        const currentRevision = networkLatency && String(networkLatency.groupId || "") === incomingGroup ? latencyRevision(networkLatency) : null;
        const incomingRevision = latencyRevision(snapshot);
        if (currentRevision !== null && incomingRevision !== null && incomingRevision < currentRevision) return false;
        networkLatency = snapshot;
        if (latencyNotice?.groupId === incomingGroup && Number.isSafeInteger(latencyNotice.appliedRevision)
            && incomingRevision !== null && incomingRevision > latencyNotice.appliedRevision) latencyNotice = null;
        return true;
    }

    function clearLatencyForState(sessions) {
        if (!networkLatency) return;
        const groupId = String(networkLatency.groupId || "");
        const group = (Array.isArray(sessions) ? sessions : []).filter(session =>
            String(session.groupId || "") === groupId && (session.profile === "arena-kcp" || session.launchProfile === "arena-kcp"));
        if (group.length && group.every(session => activeSession(session) && runtimeReady(session) && session.gameReady === true)) return;
        networkLatency = null;
        if (latencyNotice?.groupId === groupId) latencyNotice = null;
    }

    function renderLatencyLinks(links) {
        ui.latencyLinks.replaceChildren();
        for (const link of Array.isArray(links) ? links : []) {
            if (!link || typeof link !== "object") continue;
            const item = document.createElement("div"); item.className = "streaming-latency-link";
            const heading = document.createElement("strong");
            heading.textContent = fieldText(link.label, `Client ${fieldText(link.index)}`);
            const measured = document.createElement("span");
            if (Number.isFinite(link.gameRttMs)) {
                const deviation = Number.isFinite(link.gameRttStdDevMs) ? ` · σ ${link.gameRttStdDevMs.toFixed(1)} ms` : "";
                const sampleTime = typeof link.gameRttSampleUtc === "string" ? Date.parse(link.gameRttSampleUtc) : NaN;
                const sampled = Number.isFinite(sampleTime)
                    ? ` · sampled ${Math.max(0, Math.floor((Date.now() - sampleTime) / 1000))}s ago` : "";
                measured.textContent = `Measured game RTT ${link.gameRttMs.toFixed(1)} ms${deviation}${sampled}`;
            } else measured.textContent = "Measured game RTT N/A · no current sample";
            const queue = document.createElement("span");
            queue.textContent = ` · queued ${Number(link.queuedPackets) || 0} packets / ${Number(link.queuedBytes) || 0} B · forwarded ${Number(link.forwardedPackets) || 0} · dropped ${Number(link.droppedPackets) || 0}`;
            item.append(heading, measured, queue); ui.latencyLinks.append(item);
        }
        if (!ui.latencyLinks.children.length) {
            const empty = document.createElement("p"); empty.className = "streaming-footnote";
            empty.textContent = "No remote client relay links are available."; ui.latencyLinks.append(empty);
        }
    }

    function syncLatencyPanel() {
        if (!ui.latency) return;
        const kcp = latencySessions();
        if (!kcp.length) {
            if (latencyBusy) { latencyRequestGeneration++; latencyBusy = false; latencyNotice = null; }
            latencyGroupId = null;
            ui.latency.hidden = true;
            ui.latencyMin.disabled = ui.latencyMax.disabled = ui.latencyApply.disabled = ui.latencyReset.disabled = true;
            return;
        }
        const groupId = String(kcp[0].record.groupId || "");
        if (!groupId) {
            if (latencyBusy) { latencyRequestGeneration++; latencyBusy = false; latencyNotice = null; }
            latencyGroupId = null;
            ui.latency.hidden = true;
            ui.latencyMin.disabled = ui.latencyMax.disabled = ui.latencyApply.disabled = ui.latencyReset.disabled = true;
            return;
        }
        const group = latencyGroupSessions(groupId);
        const groupChanged = latencyGroupId !== groupId;
        if (groupChanged && latencyBusy) { latencyRequestGeneration++; latencyBusy = false; latencyNotice = null; }
        latencyGroupId = groupId;
        const matching = networkLatency && String(networkLatency.groupId || "") === groupId ? networkLatency : null;
        if (groupChanged) {
            latencyInputGroupId = groupId;
            ui.latencyMin.value = String(Number.isInteger(matching?.minRttMs) ? matching.minRttMs : 50);
            ui.latencyMax.value = String(Number.isInteger(matching?.maxRttMs) ? matching.maxRttMs : 100);
            latencyNotice = null;
        } else if (latencyInputGroupId !== groupId) {
            latencyInputGroupId = groupId;
        }
        ui.latency.hidden = false;
        const ready = group.length >= 2 && group.every(tile => runtimeReady(tile.record) && tile.record.gameReady === true);
        const available = !!matching && ready;
        const editable = available && !latencyBusy;
        ui.latencyMin.disabled = ui.latencyMax.disabled = !editable;
        ui.latencyApply.disabled = ui.latencyReset.disabled = !editable;
        ui.latencyApply.textContent = latencyBusy && latencyNotice?.action === "apply" ? "Applying…" : "Apply";
        ui.latencyReset.textContent = latencyBusy && latencyNotice?.action === "reset" ? "Resetting…" : "Reset";
        renderLatencyLinks(matching?.links);
        if (latencyBusy && latencyNotice?.groupId === groupId) {
            const range = latencyNotice.action === "apply" ? `${latencyNotice.minRttMs}–${latencyNotice.maxRttMs} ms` : "off";
            ui.latencyStatus.textContent = latencyNotice.action === "apply"
                ? `Pending target: ${range}; applying to all remote clients…` : "Pending target: off; resetting added RTT for all remote clients…";
            ui.latencyStatus.className = "streaming-field-note streaming-latency-status-pending";
        } else if (latencyNotice?.groupId === groupId) {
            ui.latencyStatus.textContent = latencyNotice.text;
            ui.latencyStatus.className = "streaming-field-note" + (latencyNotice.error ? " streaming-latency-status-error" : "");
        } else if (!ready) {
            ui.latencyStatus.textContent = "No pending update.";
            ui.latencyStatus.className = "streaming-field-note";
        } else if (!matching) {
            ui.latencyStatus.textContent = "No pending update.";
            ui.latencyStatus.className = "streaming-field-note";
        } else {
            ui.latencyStatus.textContent = "No pending update.";
            ui.latencyStatus.className = "streaming-field-note";
        }
        if (!ready) ui.latencyApplied.textContent = "Applied target: Waiting for all KCP players to be game-ready.";
        else if (!matching) ui.latencyApplied.textContent = "Applied target: Waiting for the KCP relay status.";
        else if (matching.enabled === true)
            ui.latencyApplied.textContent = `Applied target: ${matching.minRttMs}–${matching.maxRttMs} ms added game-network RTT for each remote client.`;
        else ui.latencyApplied.textContent = "Applied target: Off (no added game-network RTT).";
        const targetSummary = !ready ? "Waiting for ready KCP players" : !matching ? "Waiting for relay status"
            : matching.enabled === true ? `Applied: ${matching.minRttMs}–${matching.maxRttMs} ms` : "Applied: Off";
        ui.latencySummary.textContent = targetSummary + (latencyBusy ? " · update pending" : latencyNotice?.error ? " · update failed" : "");
    }

    function validLatencyRange() {
        if (!ui.latencyMin.value.trim() || !ui.latencyMax.value.trim()) return null;
        const min = Number(ui.latencyMin.value), max = Number(ui.latencyMax.value);
        return Number.isInteger(min) && Number.isInteger(max) && min >= 0 && max <= 1000 && min <= max
            ? { minRttMs: min, maxRttMs: max } : null;
    }

    async function setNetworkLatency(enabled) {
        if (latencyBusy || !latencyGroupId || !networkLatency || String(networkLatency.groupId || "") !== latencyGroupId) return;
        let range = validLatencyRange();
        if (!range && !enabled) {
            const confirmedMin = Number(networkLatency.minRttMs), confirmedMax = Number(networkLatency.maxRttMs);
            range = Number.isInteger(confirmedMin) && Number.isInteger(confirmedMax) && confirmedMin >= 0 && confirmedMax <= 1000 && confirmedMin <= confirmedMax
                ? { minRttMs: confirmedMin, maxRttMs: confirmedMax } : { minRttMs: 50, maxRttMs: 100 };
        }
        if (!range) {
            latencyNotice = { groupId: latencyGroupId, error: true, text: "Enter whole-number RTT values from 0 to 1000 ms, with minimum no greater than maximum." };
            syncLatencyPanel();
            return;
        }
        const groupId = latencyGroupId;
        const generation = ++latencyRequestGeneration;
        const baseRevision = latencyRevision(networkLatency);
        latencyBusy = true;
        latencyNotice = { groupId, baseRevision, action: enabled ? "apply" : "reset", minRttMs: range.minRttMs, maxRttMs: range.maxRttMs };
        syncLatencyPanel();
        try {
            const response = await postJson(`${API}/groups/${encodeURIComponent(groupId)}/latency`, { enabled, ...range });
            if (generation !== latencyRequestGeneration || pageClosed || latencyGroupId !== groupId || String(response.networkLatency?.groupId || "") !== groupId) return;
            const accepted = commitLatencySnapshot(response.networkLatency);
            const currentRevision = latencyRevision(networkLatency);
            if (accepted) latencyNotice = { groupId, appliedRevision: latencyRevision(networkLatency), text: enabled
                ? `Applied revision ${latencyRevision(networkLatency)}.`
                : `Reset confirmed at revision ${latencyRevision(networkLatency)}.` };
            else if (currentRevision !== null && (baseRevision === null || currentRevision > baseRevision)) latencyNotice = null;
        } catch (error) {
            if (generation === latencyRequestGeneration && !pageClosed && latencyGroupId === groupId)
                latencyNotice = { groupId, error: true, text: `Latency update failed: ${summarizeError(error)}` };
        } finally {
            if (generation === latencyRequestGeneration) {
                latencyBusy = false;
                syncLatencyPanel();
            }
        }
    }

    function validSocketUrl(path) {
        if (typeof path !== "string" || !path.startsWith("/api/streaming/viewer/")) throw new Error("The viewer endpoint path is invalid.");
        const socketUrl = new URL(path, location.href);
        if (socketUrl.origin !== location.origin || !socketUrl.pathname.startsWith("/api/streaming/viewer/") || socketUrl.search || socketUrl.hash || socketUrl.username || socketUrl.password) {
            throw new Error("The viewer endpoint must be a same-origin local path without URL credentials or query tokens.");
        }
        socketUrl.protocol = location.protocol === "https:" ? "wss:" : "ws:";
        return socketUrl.href;
    }

    function freezeSnapshot(value) {
        if (!value || typeof value !== "object" || Object.isFrozen(value)) return value;
        for (const child of Object.values(value)) freezeSnapshot(child);
        return Object.freeze(value);
    }


    function summarizeError(error) { return error instanceof Error ? error.message : String(error); }
    function focusedTile() { return tiles.get(focusedInstanceId) || null; }
    function syncButtons() {
        const all = [...tiles.values()];
        const active = all.some(tile => activeSession(tile.record));
        const focused = focusedTile();
        const connected = all.filter(tile => tile.context?.socket?.readyState === WebSocket.OPEN).length;
        const steam = usesSteamProfile();
        const requiredBoxes = Math.max(0, Number(ui.instanceCount.value) - 1);
        const chosenBoxes = selectedSteamSandboxes();
        const availableBoxes = steamEnvironments.environments.filter(environment => environment?.available === true).length;
        const steamReady = !steam || steamEnvironmentsLoaded && steamEnvironments.available &&
            availableBoxes >= requiredBoxes && chosenBoxes.length === requiredBoxes;
        ui.start.disabled = busy || active || !ui.build.value || !steamReady;
        for (const field of [ui.build, ui.width, ui.height, ui.diagnostics, ui.instanceCount, ui.profile]) field.disabled = busy || active;
        for (const checkbox of ui.steamSandboxOptions.querySelectorAll("input[type=checkbox]")) checkbox.disabled = busy || active ||
            (steam && (!steamEnvironmentsLoaded || !steamEnvironments.available || checkbox.dataset.available === "false" ||
                (!checkbox.checked && chosenBoxes.length >= requiredBoxes)));
        ui.stop.disabled = stopping || !active;
        ui.connect.disabled = busy || !all.some(tile => tile.canConnect());
        ui.connect.textContent = connected ? connected + "/" + all.filter(tile => activeSession(tile.record)).length + " viewers connected" : "Connect viewers";
        const controller = all.find(tile => tile.controlActive);
        ui.takeover.hidden = !controller;
        ui.takeover.textContent = controller ? "Controlling " + controller.label + " — press Escape to release." : "";
        ui.controlStatus.textContent = controlWork
            ? "Switching control safely; waiting for the previous runtime to release input."
            : focused?.ui.controlStatus.textContent || "Click a player to control it. Press Escape to release.";
        ui.noStreams.hidden = all.length !== 0;
        ui.tiles.dataset.count = String(all.length);
        for (const tile of all) tile.syncFocus();
        ui.sessionSummary.textContent = all.length
            ? all.length + " stream" + (all.length === 1 ? "" : "s") + " · " + connected + " viewers connected" +
              (focused ? " · Selected: " + focused.label : "")
            : "No active build session.";
        syncLatencyPanel();
    }

    function normalizeSessions(response) {
        if (Array.isArray(response.sessions)) return response.sessions.filter(hasSessionRecord).slice(0, 4);
        const single = response.session || response;
        return hasSessionRecord(single) ? [single] : [];
    }

    function applySessions(records) {
        const wanted = new Set(records.map(record => record.instanceId));
        for (const [id, tile] of tiles) {
            const replacement = records.find(record => record.instanceId === id);
            if (!wanted.has(id) || replacement?.sessionId !== tile.record.sessionId) {
                if (id === focusedInstanceId) { manualIntent = false; focusRevision++; focusedInstanceId = null; }
                tile.dispose("Session replaced or removed");
                tiles.delete(id);
            }
        }
        for (const [index, record] of records.entries()) {
            if (!record.instanceId || !record.sessionId) continue;
            let tile = tiles.get(record.instanceId);
            if (!tile) {
                tile = createTile(record, index);
                tiles.set(record.instanceId, tile);
                ui.tiles.append(tile.node);
            } else tile.update(record);
        }
        if (!tiles.has(focusedInstanceId)) focusedInstanceId = tiles.keys().next().value || null;
        if ([...tiles.values()].some(tile => activeSession(tile.record)) && ui.setup) ui.setup.open = false;
        syncButtons();
    }

    async function refreshState() {
        const request = ++refreshRequest;
        const latencyGenerationAtRequest = latencyRequestGeneration;
        const response = await getJson(API + "/state");
        if (pageClosed || request < appliedRefresh) return;
        appliedRefresh = request;
        const sessions = normalizeSessions(response);
        if (response.networkLatency && typeof response.networkLatency === "object") commitLatencySnapshot(response.networkLatency);
        else if (latencyGenerationAtRequest === latencyRequestGeneration) clearLatencyForState(sessions);
        state = { builds: Array.isArray(response.builds) ? response.builds : [], sessions };
        populateBuilds(state.builds);
        applySessions(state.sessions);
        const error = response.error || state.sessions.find(session => session.error)?.error;
        if (error) showAlert(error, "warning");
    }

    async function loadState(showFailure = true) {
        try { await refreshState(); }
        catch (error) { if (showFailure) showAlert(summarizeError(error)); }
    }

    async function startSession() {
        if (busy || [...tiles.values()].some(tile => activeSession(tile.record))) return;
        clearAlert();
        const selectedBuildId = ui.build.value;
        const width = Number(ui.width.value), height = Number(ui.height.value), instanceCount = Number(ui.instanceCount.value);
        if (!selectedBuildId) return showAlert("Choose a successful stream-capable build first.");
        if (![1, 2, 3, 4].includes(instanceCount)) return showAlert("Choose one to four player windows.");
        if (!Number.isInteger(width) || width < 320 || width > 1920 || width % 2 ||
            !Number.isInteger(height) || height < 180 || height > 1080 || height % 2)
            return showAlert("Capture width must be even and 320–1920; height must be even and 180–1080 pixels.");
        const build = selectedBuild();
        const launchProfile = supportsSteam(build)
            ? supportsKcp(build) ? ui.profile.value : "arena-steam"
            : instanceCount === 1 ? "single" : "arena-kcp";
        const steamSandboxes = launchProfile === "arena-steam" ? selectedSteamSandboxes() : [];
        if (launchProfile === "arena-steam" && (instanceCount < 2 || steamSandboxes.length !== instanceCount - 1))
            return showAlert(`Steam multiplayer needs exactly ${Math.max(1, instanceCount - 1)} selected Sandboxie box${instanceCount === 2 ? "" : "es"}.`);
        const generation = ++operationGeneration;
        busy = true; ui.start.textContent = "Starting…"; syncButtons();
        try {
            const request = {
                buildId: selectedBuildId, width, height, diagnostics: ui.diagnostics.checked,
                instanceCount, launchProfile
            };
            if (launchProfile === "arena-steam") request.steamSandboxes = steamSandboxes;
            const result = await postJson(API + "/start", request);
            if (generation !== operationGeneration || pageClosed) return;
            const initial = normalizeSessions(result);
            if (!initial.length) throw new Error("The start response did not include session identities.");
            applySessions(initial.map(record => ({ ...record, diagnostics: record.diagnostics ?? ui.diagnostics.checked })));
            const groupId = initial[0].groupId;
            const firstId = initial[0].sessionId;
            const deadline = performance.now() + 90000;
            while (generation === operationGeneration && !pageClosed && performance.now() < deadline) {
                await refreshState();
                const group = [...tiles.values()].filter(tile => groupId ? tile.record.groupId === groupId : tile.record.sessionId === firstId);
                if (!group.length || group.some(tile => !activeSession(tile.record) || tile.record.error))
                    throw new Error("The streaming group ended before all players became ready. Inspect its activity logs.");
                await Promise.all(group.filter(tile => tile.canConnect()).map(tile => tile.connect()));
                if (group.length === instanceCount && group.every(tile => runtimeReady(tile.record))) break;
                ui.start.textContent = "Waiting for players…";
                await new Promise(resolve => globalThis.setTimeout(resolve, 500));
            }
            if (generation !== operationGeneration || pageClosed) return;
            const group = [...tiles.values()].filter(tile => groupId ? tile.record.groupId === groupId : tile.record.sessionId === firstId);
            if (group.length !== instanceCount || !group.every(tile => runtimeReady(tile.record)))
                throw new Error("Not all players became ready within 90 seconds. The group remains available to inspect or stop.");
        } catch (error) {
            showAlert(summarizeError(error));
            await loadState(false);
        } finally { if (generation === operationGeneration) busy = false; ui.start.textContent = "Start streams"; syncButtons(); }
    }

    async function connectViewers() {
        clearAlert();
        await Promise.all([...tiles.values()].filter(tile => tile.canConnect()).map(tile => tile.connect()));
        syncButtons();
    }

    async function stopSession() {
        if (stopping || ![...tiles.values()].some(tile => activeSession(tile.record))) return;
        operationGeneration++; manualIntent = false; focusRevision++;
        for (const tile of tiles.values()) tile.neutralize("Stopping group");
        busy = true; stopping = true; ui.stop.textContent = "Stopping…"; syncButtons(); clearAlert();
        try {
            await postJson(API + "/stop", {});
            for (const tile of tiles.values()) tile.disconnect("Group stopped");
            await loadState(false);
        } catch (error) { showAlert(summarizeError(error)); }
        finally { busy = false; stopping = false; ui.stop.textContent = "Stop session"; syncButtons(); }
    }

    function selectTile(value) {
        const tile = typeof value === "string" ? tiles.get(value) : value;
        if (!tile || tiles.get(tile.record.instanceId) !== tile) return;
        if (focusedInstanceId !== tile.record.instanceId) {
            focusedTile()?.neutralize("Selected a different stream");
            focusedInstanceId = tile.record.instanceId;
            focusRevision++;
        }
        tile.ui.video.focus({ preventScroll: true });
        renderLogs(tile.record.logs || []);
        if (!tile.canControl() || document.hidden) {
            manualIntent = false; focusRevision++;
            tile.ui.controlStatus.textContent = "Input channel is not ready. Click this player after it connects.";
            reconcileControl(); syncButtons();
            return;
        }
        if (!manualIntent) { manualIntent = true; focusRevision++; }
        // Pointer lock needs the click's browser activation. Acquire it now, but
        // keep input gated until the runtime has granted the exclusive lease.
        tile.beginPointerLock(true);
        syncButtons();
        reconcileControl();
    }

    function releaseControl(reason = "Manual control released") {
        manualIntent = false; focusRevision++;
        for (const tile of tiles.values()) tile.neutralize(reason);
        reconcileControl();
    }

    function reconcileControl() {
        if (controlWork || pageClosed) return controlWork;
        controlWork = Promise.resolve().then(async () => {
            while (!pageClosed) {
                const revision = focusRevision;
                const target = manualIntent && !document.hidden ? focusedTile() : null;
                for (const tile of tiles.values()) {
                    if ((tile !== target || !tile.controlActive) && (tile.hasLease || tile.pendingControl)) await tile.releaseControl();
                }
                if (revision !== focusRevision) continue;
                if (target && !target.controlActive) {
                    if (target.canControl()) await target.requestControl(revision);
                    else {
                        manualIntent = false;
                        target.ui.controlStatus.textContent = "Input channel is not ready. Click this player after it connects.";
                    }
                }
                if (revision === focusRevision) break;
            }
        }).catch(error => {
            manualIntent = false; focusRevision++;
            for (const tile of tiles.values()) tile.neutralize("Control handoff failed");
            showAlert(summarizeError(error), "warning");
        }).finally(() => { controlWork = null; syncButtons(); });
        syncButtons();
        return controlWork;
    }

    function readDiagnosticSnapshot() {
        const snapshots = [...tiles.values()].map(tile => tile.snapshot());
        const selected = snapshots.find(tile => tile.instanceId === focusedInstanceId);
        return freezeSnapshot({
            ...(selected?.diagnosticsEnabled ? selected : {}),
            capturedAtMs: performance.now(),
            focusedInstanceId,
            controlledInstanceId: snapshots.find(tile => tile.controlActive)?.instanceId || null,
            switchingControl: !!controlWork,
            tiles: snapshots
        });
    }

    function initialize() {
        if (!Object.getOwnPropertyDescriptor(window, "playtestopsStreaming"))
            Object.defineProperty(window, "playtestopsStreaming", { configurable: false, enumerable: false, get: readDiagnosticSnapshot });
        ui.start.addEventListener("click", startSession);
        ui.refresh.addEventListener("click", () => { loadState(true); void loadSteamEnvironments(true); });
        ui.latencyApply.addEventListener("click", () => { void setNetworkLatency(true); });
        ui.latencyReset.addEventListener("click", () => { void setNetworkLatency(false); });
        ui.connect.addEventListener("click", () => connectViewers().catch(error => showAlert(summarizeError(error))));
        ui.stop.addEventListener("click", stopSession);
        ui.build.addEventListener("change", updateBuildNote);
        ui.profile.addEventListener("change", () => {
            if (ui.profile.value === "single") ui.instanceCount.value = "1";
            else if (Number(ui.instanceCount.value) < 2) ui.instanceCount.value = "2";
            configurePlayModes(); syncButtons();
        });
        ui.instanceCount.addEventListener("change", () => {
            if (ui.profileField.hidden && supportsSteam(selectedBuild()) && !supportsKcp(selectedBuild())) updatePlayerOptions(2);
            else if (!ui.profileField.hidden && ui.profile.value === "single" && Number(ui.instanceCount.value) > 1) ui.profile.value = "arena-kcp";
            else if (!ui.profileField.hidden && Number(ui.instanceCount.value) === 1) ui.profile.value = "single";
            configurePlayModes(); syncButtons();
        });
        document.addEventListener("keydown", event => {
            if (event.code === "Escape" || event.key === "Escape") {
                if (manualIntent || controlWork || [...tiles.values()].some(tile => tile.hasLease || tile.pendingControl)) {
                    event.preventDefault();
                    releaseControl("Control released with Escape");
                }
                return;
            }
            focusedTile()?.onKey(event, true);
        });
        document.addEventListener("keyup", event => focusedTile()?.onKey(event, false));
        document.addEventListener("pointerup", event => focusedTile()?.onPointerUp(event));
        document.addEventListener("visibilitychange", () => { if (document.hidden) releaseControl("Page became hidden"); });
        document.addEventListener("pointerlockchange", () => {
            for (const tile of tiles.values()) tile.pointerLockChanged();
            syncButtons();
        });
        document.addEventListener("pointerlockerror", () => focusedTile()?.pointerLockFailed());
        window.addEventListener("blur", () => releaseControl("Page lost focus"));
        window.addEventListener("pagehide", () => {
            pageClosed = true; manualIntent = false; focusRevision++; operationGeneration++;
            if (statePoll) globalThis.clearInterval(statePoll);
            for (const tile of tiles.values()) tile.dispose("Page closed");
        });
        loadState(true);
        loadSteamEnvironments();
        statePoll = globalThis.setInterval(() => loadState(false), 2000);
        syncButtons();
    }
    function renderLogs(entries) {
        const normalized = Array.isArray(entries) ? entries.slice(-12) : [];
        ui.log.replaceChildren();
        if (!normalized.length) {
            const empty = document.createElement("li");
            empty.className = "streaming-muted";
            empty.textContent = "No session activity yet.";
            ui.log.append(empty);
            return;
        }
        for (const item of normalized) {
            const line = document.createElement("li");
            const time = document.createElement("time");
            time.textContent = item.utc ? window.PlaytestOpsTime.formatTimeUtc(item.utc) : "";
            line.append(time, document.createTextNode(`${fieldText(item.level, "info")}: ${fieldText(item.message, "")}`));
            ui.log.append(line);
        }
    }


    const pageUi = ui;
    function createTile(record, position) {
        const page = pageUi;
        const node = document.createElement("article");
        node.className = "streaming-tile";
        node.dataset.sessionId = record.sessionId;
        node.dataset.instanceId = record.instanceId;
        node.dataset.index = String(record.index ?? position);
        node.dataset.role = record.role || "single";
        const heading = document.createElement("div"); heading.className = "streaming-tile-heading";
        const labelNode = document.createElement("h3"); labelNode.className = "streaming-tile-label";
        const focusNode = document.createElement("span"); focusNode.className = "streaming-tile-focus";
        heading.append(labelNode, focusNode);
        const stage = document.createElement("div"); stage.className = "streaming-stage";
        const video = document.createElement("video"); video.className = "streaming-video";
        video.id = position === 0 ? "streaming-video" : "streaming-video-" + position;
        video.dataset.instanceId = record.instanceId;
        video.autoplay = true; video.muted = true; video.playsInline = true; video.tabIndex = 0;
        video.setAttribute("aria-label", (record.label || "Player " + (position + 1)) + " live game video");
        const empty = document.createElement("div"); empty.className = "streaming-empty"; empty.textContent = "Waiting for this player's video";
        stage.append(video, empty);
        const metrics = document.createElement("dl"); metrics.className = "streaming-tile-metrics";
        const metric = (name, title, legacy) => {
            const row = document.createElement("div"), term = document.createElement("dt"), value = document.createElement("dd");
            term.textContent = title; value.textContent = "—"; value.dataset.metric = name;
            if (position === 0 && legacy) value.id = legacy;
            row.append(term, value); metrics.append(row); return value;
        };
        const status = document.createElement("p"); status.className = "streaming-tile-status";
        status.textContent = "Click to control. Press Escape to release.";
        const ui = {
            ...page, stage, video, empty, controlStatus: status, takeover: document.createElement("span"),
            connection: metric("connection", "Connection", "streaming-connection-state"),
            resolution: metric("resolution", "Resolution", "streaming-resolution"),
            fps: metric("fps", "Video", "streaming-fps"), rtt: metric("rtt", "Input RTT", "streaming-rtt"),
            videoLatency: metric("video-latency", "Input to video", "streaming-video-latency")
        };
        node.append(heading, stage, metrics, status);
        let currentSession = { ...record };
        let disposed = false;
        let viewerSocket = null, viewerGeneration = 0, viewerContext = null, peer = null, dataChannel = null;
        let input = null, controlLease = null, controlHeartbeat = 0, viewerHeartbeat = 0, statsPoll = 0;
        let hasVideo = false, pointerLockedBefore = false;
        let lastFramesDecoded = null, lastFramesAt = null, latestVideoFps = null;
        let latestVideoStats = null;
        let videoMarkerCanvas = null, videoMarkerContext = null, markerStartedAt = null, latestMarkerSampleAt = null;
        let markerDetected = false, markerStatusPoll = 0, markerReaderGeneration = 0, lastMarkerError = false, markerCallback = 0;
        let latestSessionLogs = [];
        let pointerFrame = 0, queuedPointer = null;
        let acquisition = null;
        const releases = new Map();
        let leaseEnabled = false;
        let intentionalUnlock = false;
        let pointerLockPending = false, pointerLockRequest = 0;
        let pointerLockAttemptRevision = -1, pointerLockFailure = "";
        const label = () => currentSession.label || (currentSession.role === "host" ? "Host" : "Player " + (position + 1));
        const canInput = () => !disposed && leaseEnabled && !!controlLease && manualIntent && focusedInstanceId === currentSession.instanceId && !document.hidden;
        const tile = {
            node, ui,
            get record() { return currentSession; }, get input() { return input; }, get context() { return viewerContext; },
            get label() { return label(); }, get controlActive() { return canInput(); },
            get hasLease() { return !!controlLease || releases.size > 0; }, get pendingControl() { return !!acquisition; },
            canControl: () => !disposed && activeSession(currentSession) && viewerSocket?.readyState === WebSocket.OPEN && dataChannel?.readyState === "open",
            canConnect: () => !disposed && runtimeReady(currentSession) && !viewerContext && !currentSession.viewerConnected,
            connect: connectViewer, update: updateRecord, snapshot: snapshot,
            requestControl: requestTileControl, releaseControl: releaseTileControl, neutralize,
            beginPointerLock,
            pointerLockFailed() {
                pointerLockPending = false;
                if (wantsPointerLock() && document.pointerLockElement !== video) {
                    pointerLockFailure = "Mouse lock was not granted. Click this player to retry, or press Escape to release.";
                    ui.controlStatus.textContent = pointerLockFailure;
                    syncButtons();
                }
            },
            onKey: onDocumentKey, onPointerUp: onDocumentPointerUp, syncFocus,
            pointerLockChanged() {
                const locked = document.pointerLockElement === video;
                if (locked) { pointerLockPending = false; pointerLockFailure = ""; }
                if (locked && !wantsPointerLock()) { intentionalUnlock = true; document.exitPointerLock(); return; }
                if (locked !== pointerLockedBefore) cancelQueuedPointer();
                if (pointerLockedBefore && !locked && !intentionalUnlock && manualIntent &&
                    focusedInstanceId === currentSession.instanceId) releaseControl("Pointer lock exited");
                pointerLockedBefore = locked; intentionalUnlock = false;
            },
            disconnect: reason => teardownViewer(reason, true),
            dispose(reason) {
                if (disposed) return;
                neutralize(reason);
                if (controlLease) sendViewer({ type: "control.release", leaseId: controlLease.leaseId, leaseToken: controlLease.leaseToken });
                teardownViewer(reason, true); disposed = true; input?.dispose(); node.remove();
            }
        };

        function createInput() {
            return new StreamingInput({
                diagnosticsEnabled: () => currentSession?.diagnostics === true,
                isInputActive: canInput,
                onStatus: text => { if (!disposed) { ui.controlStatus.textContent = text; syncButtons(); } },
                onRtt: value => { if (!disposed) ui.rtt.textContent = value === null ? "—" : Math.round(value) + " ms"; },
                onInputError: message => {
                    if (disposed) return;
                    const reason = fieldText(message.reason, "unknown input error");
                    appendLog("error", "Unity rejected input: " + reason);
                    if (canInput()) releaseControl("Unity rejected input");
                }
            });
        }

        function syncFocus() {
            const focused = focusedInstanceId === currentSession.instanceId;
            node.dataset.focused = String(focused);
            node.dataset.controlActive = String(canInput());
            labelNode.textContent = label();
            focusNode.textContent = canInput() ? "Controlling · Esc to release" : "Click to control";
        }

        function updateRecord(next) {
            const wasActive = activeSession(currentSession);
            currentSession = { ...next, diagnostics: next.diagnostics ?? currentSession.diagnostics,
                // Live cursor messages are ordered on the viewer socket. An older
                // HTTP snapshot must not replace a newer game cursor preference.
                mouseLockRequested: viewerContext ? currentSession.mouseLockRequested : next.mouseLockRequested === true };
            if (wasActive && !activeSession(currentSession)) {
                teardownViewer("Player session ended", true);
                if (focusedInstanceId === currentSession.instanceId) { manualIntent = false; focusRevision++; }
            }
            if (!viewerContext) ui.connection.textContent = currentSession.runtimeConnected ? "Runtime ready · viewer disconnected" : fieldText(currentSession.state, "Starting");
            if (Number.isFinite(currentSession.actualWidth)) updateResolution(currentSession.actualWidth, currentSession.actualHeight);
            updateMarkerStatus(); syncFocus();
        }

        function neutralize(reason) {
            cancelQueuedPointer();
            pointerLockRequest++; pointerLockPending = false;
            pointerLockAttemptRevision = -1; pointerLockFailure = "";
            leaseEnabled = false;
            input?.releaseAll(reason, false);
            input?.setLease(null);
            if (controlHeartbeat) globalThis.clearInterval(controlHeartbeat);
            controlHeartbeat = 0;
            if (document.pointerLockElement === video) { intentionalUnlock = true; document.exitPointerLock(); }
            syncFocus();
        }

        function requestId() {
            const values = new Uint32Array(4);
            window.crypto.getRandomValues(values);
            values[3] = ++requestSequence;
            return [...values].map(value => value.toString(16).padStart(8, "0")).join("");
        }

        function requestTileControl(revision) {
            if (canInput()) return Promise.resolve();
            if (acquisition) return acquisition.promise;
            if (!tile.canControl()) return Promise.reject(new Error(label() + ": input channel is not ready."));
            const id = requestId();
            let resolve, reject;
            const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
            const timer = globalThis.setTimeout(() => {
                if (acquisition?.requestId !== id) return;
                acquisition = null;
                reject(new Error(label() + ": control request timed out; no input is enabled."));
            }, 12000);
            acquisition = { requestId: id, revision, promise, resolve, reject, timer };
            ui.controlStatus.textContent = "Requesting manual control…";
            if (!sendViewer({ type: "control.request", requestId: id })) {
                globalThis.clearTimeout(timer); acquisition = null; reject(new Error(label() + ": viewer disconnected."));
            }
            return promise;
        }

        function finishAcquisition(error) {
            const pending = acquisition;
            if (!pending) return;
            acquisition = null; globalThis.clearTimeout(pending.timer);
            if (error) pending.reject(error); else pending.resolve();
        }

        function releaseCredentials(lease) {
            if (releases.has(lease.leaseId)) return releases.get(lease.leaseId).promise;
            let resolve, reject;
            const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
            const timer = globalThis.setTimeout(() => {
                const pending = releases.get(lease.leaseId);
                if (!pending) return;
                // Keep the unresolved barrier recorded. A late matching ACK may clear it,
                // but a user retry must not optimistically grant another controller.
                pending.reject(new Error(label() + ": runtime input release was not confirmed. Reconnect or stop the group."));
            }, 12000);
            releases.set(lease.leaseId, { promise, resolve, reject, timer });
            if (!sendViewer({ type: "control.release", leaseId: lease.leaseId, leaseToken: lease.leaseToken })) {
                globalThis.clearTimeout(timer);
                reject(new Error(label() + ": connection closed before release confirmation."));
            }
            return promise;
        }

        async function releaseTileControl() {
            neutralize("Releasing manual control");
            if (acquisition) await acquisition.promise;
            if (controlLease) {
                const lease = controlLease;
                clearControl(false);
                await releaseCredentials(lease);
            }
            if (releases.size) await Promise.all([...releases.values()].map(pending => pending.promise));
            ui.controlStatus.textContent = "Control released. Click a player to resume.";
        }

        function grantControl(message) {
            if (controlLease && message.leaseId === controlLease.leaseId && message.leaseToken === controlLease.leaseToken) return;
            const pending = acquisition;
            const valid = message.leaseId && message.leaseToken && Number.isFinite(message.timeoutMs);
            const expected = pending && message.requestId === pending.requestId;
            const wanted = expected && pending.revision === focusRevision && manualIntent &&
                focusedInstanceId === currentSession.instanceId && !document.hidden && !disposed;
            if (!valid) { if (expected) finishAcquisition(new Error(label() + ": incomplete control lease.")); return; }
            const lease = { leaseId: message.leaseId, leaseToken: message.leaseToken, expiresUtc: message.expiresUtc, timeoutMs: message.timeoutMs };
            if (!wanted) {
                // Delayed grants belong to the request that created them, never the newly selected tile.
                const releasing = releaseCredentials(lease);
                releasing.then(() => { if (expected && acquisition === pending) finishAcquisition(); })
                    .catch(error => { if (expected && acquisition === pending) finishAcquisition(error); else showAlert(summarizeError(error), "warning"); });
                return;
            }
            if (controlLease) {
                if (controlLease.leaseId === lease.leaseId) { finishAcquisition(); return; }
                releaseCredentials(lease).catch(error => showAlert(summarizeError(error), "warning"));
                finishAcquisition(new Error("An unexpected additional control lease was refused."));
                return;
            }
            controlLease = lease; leaseEnabled = true; input.setLease(lease);
            controlHeartbeat = globalThis.setInterval(() => {
                if (!controlLease || !leaseEnabled) return;
                sendViewer({ type: "control.heartbeat", leaseId: controlLease.leaseId, leaseToken: controlLease.leaseToken });
            }, 500);
            ui.controlStatus.textContent = pointerLockFailure || "Controlling " + label() + ". Press Escape to release.";
            beginPointerLock();
            finishAcquisition(); syncButtons();
        }

        function onRevoked(message) {
            if (controlLease && message.leaseId === controlLease.leaseId) {
                neutralize("Control lease revoked");
                clearControl(false);
                if (focusedInstanceId === currentSession.instanceId) { manualIntent = false; focusRevision++; }
                ui.controlStatus.textContent = "Control revoked: " + fieldText(message.reason, "lease ended") + ".";
            }
            syncButtons();
        }

        function onReleased(message) {
            const pending = releases.get(message.leaseId);
            if (!pending) return;
            releases.delete(message.leaseId); globalThis.clearTimeout(pending.timer); pending.resolve(); syncButtons();
        }

        function clearControl(releaseInput) {
            cancelQueuedPointer();
            if (releaseInput) input?.releaseAll("Control ended", true);
            if (controlHeartbeat) globalThis.clearInterval(controlHeartbeat);
            controlHeartbeat = 0; controlLease = null; leaseEnabled = false; input?.setLease(null);
            syncButtons();
        }

        function rejectControlRequests(reason) {
            finishAcquisition(new Error(reason));
            for (const pending of releases.values()) { globalThis.clearTimeout(pending.timer); pending.reject(new Error(reason)); }
            releases.clear();
        }

        function inputTransportEnded(reason) {
            if (controlLease || acquisition) { manualIntent = false; focusRevision++; }
            neutralize(reason);
            finishAcquisition(new Error(reason));
            const lease = controlLease;
            clearControl(false);
            // Signaling may still be alive after a data-channel/peer failure. Revoke
            // through it and keep the runtime release barrier before another grant.
            if (lease) releaseCredentials(lease).catch(error => showAlert(summarizeError(error), "warning"));
        }
    function isCurrentViewer(context) {
        return !disposed && tiles.get(currentSession.instanceId) === tile && !!context && context === viewerContext && context.generation === viewerGeneration
            && (context.pc === null || context.pc === peer)
            && (context.socket === null || context.socket === viewerSocket)
            && currentSession?.sessionId === context.sessionId
            && currentSession?.instanceId === context.instanceId;
    }

    async function connectViewer() {
        clearAlert();
        const session = currentSession;
        if (!activeSession(session)) throw new Error("There is no active build session to connect to.");
        if (!runtimeReady(session)) throw new Error("The Unity runtime is still starting. Wait for it to become ready before connecting a viewer.");
        if (session.viewerConnected && !viewerSocket) throw new Error("This session already has a connected viewer. Wait for it to disconnect before requesting another viewer grant.");
        if (!localOnly()) throw new Error("Streaming is available from this machine only. Open the dashboard through localhost.");
        if (viewerContext && viewerContext.generation === viewerGeneration) return;
        if (viewerSocket && viewerSocket.readyState <= WebSocket.OPEN) return;
        if (viewerHeartbeat) globalThis.clearInterval(viewerHeartbeat);
        viewerHeartbeat = 0;
        if (controlLease) {
            input.releaseAll("Reconnecting viewer", false);
            clearControl(false);
        }

        const context = {
            generation: ++viewerGeneration,
            sessionId: session.sessionId,
            instanceId: session.instanceId,
            session,
            pc: null,
            socket: null,
            pendingCandidates: []
        };
        viewerSocket = null;
        viewerContext = context;
        let socket = null;
        ui.connect.disabled = true;
        ui.connect.textContent = "Connecting…";
        try {
            const grant = await postJson(`${API}/${encodeURIComponent(session.sessionId)}/viewer-grant`, {});
            if (!isCurrentViewer(context)) return;
            if (grant.sessionId !== session.sessionId || grant.instanceId !== session.instanceId || !grant.token) {
                throw new Error("The viewer grant did not match the current session instance.");
            }
            setupPeer(session, grant.iceServers || [], context);
            if (!isCurrentViewer(context)) return;
            socket = new WebSocket(validSocketUrl(grant.viewerWebSocketPath));
            context.socket = socket;
            viewerSocket = socket;
            input.setIdentity(session.sessionId, session.instanceId);
            ui.connection.textContent = "Opening viewer socket";
            socket.addEventListener("open", () => {
                if (!isCurrentViewer(context)) return;
                socket.send(JSON.stringify({ type: "viewer.auth", sessionId: session.sessionId, instanceId: session.instanceId, token: grant.token }));
                viewerHeartbeat = globalThis.setInterval(() => sendViewer({ type: "viewer.heartbeat" }, context), 5000);
                ui.connection.textContent = "Viewer authenticated";
                appendLog("info", "Viewer authenticated; waiting for the Unity offer and video.");
                syncButtons();
            });
            socket.addEventListener("message", event => onViewerMessage(context, event));
            socket.addEventListener("close", () => {
                if (!isCurrentViewer(context)) return;
                rejectControlRequests("Viewer WebSocket closed");
                viewerGeneration++;
                viewerContext = null;
                viewerSocket = null;
                input.releaseAll("Viewer WebSocket closed", false);
                clearControl(false);
                if (viewerHeartbeat) globalThis.clearInterval(viewerHeartbeat);
                viewerHeartbeat = 0;
                ui.connection.textContent = "Viewer disconnected";
                appendLog("warning", "Viewer connection closed.");
                teardownPeer();
                syncButtons();
            });
            socket.addEventListener("error", () => {
                if (isCurrentViewer(context)) ui.connection.textContent = "Viewer connection error";
            });
        } catch (error) {
            if (isCurrentViewer(context)) {
                viewerGeneration++;
                viewerContext = null;
                rejectControlRequests("Viewer connection failed");
                if (socket && viewerSocket === socket) viewerSocket = null;
                if (viewerHeartbeat) globalThis.clearInterval(viewerHeartbeat);
                viewerHeartbeat = 0;
                teardownPeer();
                if (socket) closeSocket(socket);
                showAlert(summarizeError(error));
                syncButtons();
            }
        } finally {
            if (context.generation === viewerGeneration) {
                ui.connect.textContent = "Connect viewer";
                syncButtons();
            }
        }
    }

    function setupPeer(session, iceServers, context) {
        teardownPeer();
        const pc = new RTCPeerConnection({ iceServers: Array.isArray(iceServers) ? iceServers : [] });
        context.pc = pc;
        peer = pc;
        input.setIdentity(session.sessionId, session.instanceId);
        pc.addEventListener("icecandidate", event => {
            if (!isCurrentViewer(context) || !event.candidate || !viewerSocket || viewerSocket.readyState !== WebSocket.OPEN) return;
            const candidate = event.candidate;
            sendViewer({
                type: "signal.ice",
                candidate: candidate.candidate,
                sdpMid: candidate.sdpMid,
                sdpMLineIndex: candidate.sdpMLineIndex
            }, context);
        });
        pc.addEventListener("connectionstatechange", () => {
            if (!isCurrentViewer(context)) return;
            ui.connection.textContent = `WebRTC ${pc.connectionState}`;
            if (pc.connectionState === "failed" || pc.connectionState === "closed") {
                inputTransportEnded("WebRTC connection ended");
                stopMarkerReader();
                updateMarkerStatus();
            }
            syncButtons();
        });
        pc.addEventListener("iceconnectionstatechange", () => {
            if (isCurrentViewer(context) && pc.connectionState !== "connected") ui.connection.textContent = `ICE ${pc.iceConnectionState}`;
        });
        pc.addEventListener("track", event => {
            if (!isCurrentViewer(context) || event.track.kind !== "video") return;
            const stream = event.streams?.[0] || new MediaStream([event.track]);
            ui.video.srcObject = stream;
            hasVideo = true;
            ui.empty.hidden = true;
            event.track.addEventListener("ended", () => {
                if (isCurrentViewer(context) && ui.video.srcObject === stream) {
                    hasVideo = false;
                    ui.empty.hidden = false;
                    ui.connection.textContent = "Video track ended";
                    stopMarkerReader();
                    updateMarkerStatus();
                }
            });
            ui.video.play().catch(() => appendLog("warning", "Browser autoplay was blocked; click the video to start playback."));
            updateResolution();
            startStats(pc, context);
            startMarkerReader(context);
        });
        pc.addEventListener("datachannel", event => {
            if (!isCurrentViewer(context)) {
                event.channel.close();
                return;
            }
            if (event.channel.label !== "playtestops-input" || event.channel.ordered !== true || event.channel.maxRetransmits !== null || event.channel.maxPacketLifeTime !== null) {
                event.channel.close();
                showAlert("Unity opened an unsupported input channel. A reliable ordered `playtestops-input` channel is required.");
                return;
            }
            if (dataChannel && dataChannel !== event.channel) { event.channel.close(); return; }
            dataChannel = event.channel;
            input.setChannel(dataChannel);
            dataChannel.addEventListener("open", () => {
                if (!isCurrentViewer(context) || dataChannel !== event.channel) return;
                ui.connection.textContent = "WebRTC connected · input channel ready";
                syncButtons();
            });
            dataChannel.addEventListener("close", () => {
                if (!isCurrentViewer(context) || dataChannel !== event.channel) return;
                inputTransportEnded("Input channel closed");
                dataChannel = null;
                syncButtons();
            });
            syncButtons();
        });
    }

    async function onViewerMessage(context, event) {
        if (!isCurrentViewer(context)) return;
        let message;
        try { message = JSON.parse(event.data); }
        catch { appendLog("warning", "Ignored malformed viewer message."); return; }
        if (!message || message.sessionId !== context.sessionId || message.instanceId !== context.instanceId) return;

        switch (message.type) {
            case "signal.offer":
                await receiveOffer(message, context);
                break;
            case "signal.ice":
                await receiveIce(message, context);
                break;
            case "control.granted":
                grantControl(message);
                break;
            case "control.revoked":
                onRevoked(message);
                break;
            case "control.released":
                onReleased(message);
                break;
            case "control.denied":
                if (acquisition && message.requestId === acquisition.requestId)
                    finishAcquisition(new Error(label() + ": " + fieldText(message.reason, "control denied")));
                break;
            case "control.renewed":
                if (controlLease && message.leaseId === controlLease.leaseId) controlLease.expiresUtc = message.expiresUtc;
                break;
            case "runtime.ready":
                updateResolution(message.width, message.height);
                ui.connection.textContent = "Unity runtime ready";
                appendLog("info", `Unity runtime ready at ${fieldText(message.width)} × ${fieldText(message.height)}.`);
                break;
            case "runtime.heartbeat":
                if (Number.isFinite(message.width) && Number.isFinite(message.height)) updateResolution(message.width, message.height);
                break;
            case "runtime.cursor":
                if (typeof message.mouseLockRequested === "boolean") {
                    currentSession.mouseLockRequested = message.mouseLockRequested;
                    if (!message.mouseLockRequested) {
                        cancelQueuedPointer();
                        pointerLockRequest++; pointerLockPending = false;
                        pointerLockAttemptRevision = -1; pointerLockFailure = "";
                        if (document.pointerLockElement === video) { intentionalUnlock = true; document.exitPointerLock(); }
                    } else if (wantsPointerLock()) beginPointerLock();
                    syncButtons();
                }
                break;
            case "runtime.log":
                appendLog(message.level || "info", message.message || "");
                break;
            case "stream.error":
                showAlert(fieldText(message.message || message.error, "The stream reported an error."));
                appendLog("error", fieldText(message.message || message.error, "Stream error."));
                break;
            case "session.stop":
                appendLog("warning", "The backend stopped the session.");
                teardownViewer("The backend stopped the session.", true);
                await loadState(false);
                break;
            case "viewer.ready":
                ui.connection.textContent = "Viewer ready; waiting for video";
                break;
        }
    }

    async function receiveOffer(message, context) {
        const pc = context.pc;
        if (!isCurrentViewer(context) || !pc || typeof message.sdp !== "string") return;
        try {
            await pc.setRemoteDescription({ type: "offer", sdp: message.sdp });
            if (!isCurrentViewer(context)) return;
            const queuedCandidates = context.pendingCandidates;
            context.pendingCandidates = [];
            for (const candidate of queuedCandidates) {
                if (!isCurrentViewer(context)) return;
                await pc.addIceCandidate(candidate);
                if (!isCurrentViewer(context)) return;
            }
            if (!isCurrentViewer(context)) return;
            const answer = await pc.createAnswer();
            if (!isCurrentViewer(context)) return;
            await pc.setLocalDescription(answer);
            if (!isCurrentViewer(context) || !pc.localDescription?.sdp) return;
            sendViewer({ type: "signal.answer", sdp: pc.localDescription.sdp }, context);
            if (!isCurrentViewer(context)) return;
            ui.connection.textContent = "WebRTC answer sent";
        } catch (error) {
            if (isCurrentViewer(context)) showAlert(`Could not complete WebRTC negotiation: ${summarizeError(error)}`);
        }
    }

    async function receiveIce(message, context) {
        const pc = context.pc;
        if (!isCurrentViewer(context) || !pc || typeof message.candidate !== "string") return;
        const candidate = {
            candidate: message.candidate,
            sdpMid: message.sdpMid ?? null,
            sdpMLineIndex: message.sdpMLineIndex ?? null
        };
        if (!pc.remoteDescription) {
            if (context.pendingCandidates.length < 128) context.pendingCandidates.push(candidate);
            else if (isCurrentViewer(context)) showAlert("The Unity runtime sent too many ICE candidates before its offer.");
            return;
        }
        try {
            await pc.addIceCandidate(candidate);
            if (!isCurrentViewer(context)) return;
        } catch (error) {
            if (isCurrentViewer(context)) showAlert(`Could not add the Unity ICE candidate: ${summarizeError(error)}`);
        }
    }

    function sendViewer(message, context = viewerContext) {
        if (!isCurrentViewer(context) || !context.socket || context.socket.readyState !== WebSocket.OPEN) return false;
        if (context.socket.bufferedAmount > 262144) return false;
        try {
            context.socket.send(JSON.stringify({
                ...message,
                sessionId: context.sessionId,
                instanceId: context.instanceId
            }));
            return true;
        } catch { return false; }
    }

    function teardownViewer(reason, closeSocketToo) {
        rejectControlRequests(reason || "Viewer closed");
        cancelQueuedPointer();
        const socket = viewerSocket;
        viewerGeneration++;
        viewerContext = null;
        viewerSocket = null;
        if (viewerHeartbeat) globalThis.clearInterval(viewerHeartbeat);
        viewerHeartbeat = 0;
        if (input) input.releaseAll(reason || "Viewer closed", false);
        clearControl(false);
        teardownPeer();
        if (closeSocketToo && socket) closeSocket(socket);
        ui.connection.textContent = reason || "Not connected";
        syncButtons();
    }

    function teardownPeer() {
        if (statsPoll) globalThis.clearInterval(statsPoll);
        statsPoll = 0;
        stopMarkerReader();
        if (dataChannel) {
            try { dataChannel.close(); } catch { }
        }
        dataChannel = null;
        if (peer) {
            try { peer.close(); } catch { }
        }
        peer = null;
        if (ui.video.srcObject) {
            for (const track of ui.video.srcObject.getTracks()) track.stop();
        }
        ui.video.srcObject = null;
        hasVideo = false;
        ui.empty.hidden = false;
        ui.resolution.textContent = "—";
        ui.fps.textContent = "—";
        lastFramesDecoded = null;
        lastFramesAt = null;
        latestVideoFps = null;
        latestVideoStats = null;
        if (input) input.setChannel(null);
        updateMarkerStatus();
    }

    function closeSocket(socket) {
        try { if (socket.readyState < WebSocket.CLOSING) socket.close(1000, "viewer closed"); }
        catch { }
    }

    function exitPointerLock() {
        if (document.pointerLockElement === ui.video) document.exitPointerLock();
    }

    function updateResolution(width, height) {
        const actualWidth = Number(width) || ui.video.videoWidth;
        const actualHeight = Number(height) || ui.video.videoHeight;
        if (actualWidth > 0 && actualHeight > 0) ui.resolution.textContent = `${actualWidth} × ${actualHeight}`;
    }

    function startStats(pc, context) {
        if (statsPoll) globalThis.clearInterval(statsPoll);
        const readStats = async () => {
            if (!isCurrentViewer(context) || peer !== pc || pc.connectionState === "closed") return;
            try {
                const report = await pc.getStats();
                if (!isCurrentViewer(context) || peer !== pc) return;
                let rate = null;
                let inbound = null;
                let width = 0;
                let height = 0;
                let decoded = null;
                let timestamp = performance.now();
                report.forEach(item => {
                    if (item.type !== "inbound-rtp" || (item.kind && item.kind !== "video") || (item.mediaType && item.mediaType !== "video")) return;
                    inbound = item;
                    if (Number.isFinite(item.framesPerSecond)) rate = item.framesPerSecond;
                    if (Number.isFinite(item.frameWidth)) width = item.frameWidth;
                    if (Number.isFinite(item.frameHeight)) height = item.frameHeight;
                    if (Number.isFinite(item.framesDecoded)) decoded = item.framesDecoded;
                    if (Number.isFinite(item.timestamp)) timestamp = item.timestamp;
                });
                if (rate === null && decoded !== null && lastFramesDecoded !== null && lastFramesAt !== null && timestamp > lastFramesAt) {
                    rate = (decoded - lastFramesDecoded) * 1000 / (timestamp - lastFramesAt);
                }
                const quality = typeof ui.video.getVideoPlaybackQuality === "function" ? ui.video.getVideoPlaybackQuality() : null;
                const number = value => Number.isFinite(value) ? value : null;
                const codec = typeof inbound?.codecId === "string" ? report.get(inbound.codecId) : null;
                latestVideoStats = {
                    statsId: typeof inbound?.id === "string" ? inbound.id : null,
                    statsTimestamp: number(inbound?.timestamp), sampledAtMs: performance.now(),
                    codecMimeType: typeof codec?.mimeType === "string" ? codec.mimeType : null,
                    decoderImplementation: typeof inbound?.decoderImplementation === "string" ? inbound.decoderImplementation : null,
                    powerEfficientDecoder: typeof inbound?.powerEfficientDecoder === "boolean" ? inbound.powerEfficientDecoder : null,
                    packetsReceived: number(inbound?.packetsReceived), packetsLost: number(inbound?.packetsLost),
                    jitter: number(inbound?.jitter), // RTC inbound jitter is reported in seconds.
                    framesDecoded: number(inbound?.framesDecoded), framesDropped: number(inbound?.framesDropped),
                    bytesReceived: number(inbound?.bytesReceived), jitterBufferDelay: number(inbound?.jitterBufferDelay),
                    jitterBufferEmittedCount: number(inbound?.jitterBufferEmittedCount), totalDecodeTime: number(inbound?.totalDecodeTime),
                    totalVideoFrames: number(quality?.totalVideoFrames), droppedVideoFrames: number(quality?.droppedVideoFrames)
                };
                if (decoded !== null) { lastFramesDecoded = decoded; lastFramesAt = timestamp; }
                if (rate !== null && Number.isFinite(rate)) ui.fps.textContent = `${Math.round(rate)} fps`;
                latestVideoFps = rate !== null && Number.isFinite(rate) ? rate : null;
                if (width > 0 && height > 0) updateResolution(width, height);
                else updateResolution();
            } catch { }
        };
        readStats();
        statsPoll = globalThis.setInterval(readStats, 1000);
    }

    function wantsPointerLock() {
        return currentSession.mouseLockRequested === true && manualIntent && !document.hidden &&
            focusedInstanceId === currentSession.instanceId && tile.canControl();
    }

    function beginPointerLock(fromClick = false) {
        if (fromClick && !pointerLockPending) { pointerLockAttemptRevision = -1; pointerLockFailure = ""; }
        if (!wantsPointerLock() || pointerLockPending || document.pointerLockElement === ui.video) return;
        if (pointerLockAttemptRevision === focusRevision) return;
        const revision = focusRevision;
        const context = viewerContext;
        const request = ++pointerLockRequest;
        pointerLockAttemptRevision = revision;
        pointerLockPending = true;
        const denied = error => {
            if (request !== pointerLockRequest) return;
            pointerLockPending = false;
            if (revision !== focusRevision || context !== viewerContext || !wantsPointerLock()) return;
            pointerLockFailure = `Mouse lock was not granted: ${summarizeError(error)}. Click this player to retry, or press Escape to release.`;
            ui.controlStatus.textContent = pointerLockFailure;
            syncButtons();
        };
        try {
            ui.video.focus({ preventScroll: true });
            const result = ui.video.requestPointerLock();
            if (result && typeof result.then === "function") result.then(() => {
                if (request === pointerLockRequest) pointerLockPending = false;
            }, denied);
        } catch (error) { denied(error); }
    }

    function videoContentRect() {
        const width = ui.video.videoWidth;
        const height = ui.video.videoHeight;
        if (!width || !height) return null;
        const rect = ui.video.getBoundingClientRect();
        const scale = Math.min(rect.width / width, rect.height / height);
        const contentWidth = width * scale;
        const contentHeight = height * scale;
        return {
            width: contentWidth,
            height: contentHeight,
            left: rect.left + (rect.width - contentWidth) / 2,
            top: rect.top + (rect.height - contentHeight) / 2
        };
    }

    function videoPoint(event) {
        const content = videoContentRect();
        if (!content) return null;
        const x = (event.clientX - content.left) / content.width;
        const y = (event.clientY - content.top) / content.height;
        if (x < 0 || x > 1 || y < 0 || y > 1) return null;
        return { x, y };
    }

    function onVideoPointerDown(event) {
        ui.video.focus({ preventScroll: true });
        if (!canInput()) return;
        // A click used to enter/retry mouse lock is an activation gesture, not a shot.
        if (currentSession.mouseLockRequested === true && document.pointerLockElement !== ui.video) return;
        if (document.pointerLockElement === ui.video) {
            event.preventDefault();
            input.button(event.button, true, null);
            return;
        }
        cancelQueuedPointer();
        const point = videoPoint(event);
        if (!point) return;
        event.preventDefault();
        ui.video.setPointerCapture?.(event.pointerId);
        input.pointer(point);
        input.button(event.button, true, point);
    }

    function onDocumentPointerUp(event) {
        if (!canInput()) return;
        const point = videoPoint(event);
        input.button(event.button, false, point);
    }

    function onVideoPointerMove(event) {
        if (!canInput()) return;
        if (document.pointerLockElement === ui.video) {
            const content = videoContentRect();
            const scaleX = ui.video.videoWidth / Math.max(1, content?.width || ui.video.clientWidth);
            const scaleY = ui.video.videoHeight / Math.max(1, content?.height || ui.video.clientHeight);
            queuePointer({ x: 0.5, y: 0.5, dx: event.movementX * scaleX, dy: event.movementY * scaleY });
            return;
        }
        const point = videoPoint(event);
        if (point) queuePointer(point);
    }

    function queuePointer(fields) {
        if (Number.isFinite(fields.dx) || Number.isFinite(fields.dy)) {
            if (!queuedPointer || !("dx" in queuedPointer)) queuedPointer = { x: 0.5, y: 0.5, dx: 0, dy: 0 };
            queuedPointer.dx = (queuedPointer.dx || 0) + (fields.dx || 0);
            queuedPointer.dy = (queuedPointer.dy || 0) + (fields.dy || 0);
        } else {
            queuedPointer = { x: fields.x, y: fields.y };
        }
        if (pointerFrame) return;
        pointerFrame = requestAnimationFrame(() => {
            pointerFrame = 0;
            const latest = queuedPointer;
            queuedPointer = null;
            if (canInput() && latest) input.pointer(latest);
        });
    }

    function cancelQueuedPointer() {
        if (pointerFrame) cancelAnimationFrame(pointerFrame);
        pointerFrame = 0;
        queuedPointer = null;
    }

    function onVideoWheel(event) {
        if (!canInput()) return;
        const point = videoPoint(event);
        if (!point) return;
        event.preventDefault();
        input.pointer({ ...point, wheelX: event.deltaX, wheelY: event.deltaY });
    }

    function onDocumentKey(event, down) {
        if (!canInput() || !ALLOWED_KEYS.has(event.code)) return;
        const inGameFocus = document.activeElement === ui.video || document.pointerLockElement === ui.video;
        if (!inGameFocus) return;
        event.preventDefault();
        if (down && event.repeat) return;
        input.key(event.code, down);
    }

    function markerIdleStatus() {
        if (!activeSession(currentSession)) return ui.diagnostics.checked
            ? "Not measuring — start a stream" : "N/A — diagnostics disabled";
        if (currentSession.diagnostics !== true) return "N/A — diagnostics disabled for this session";
        if (!isCurrentViewer(viewerContext)) return "Connect the viewer to measure latency";
        if (peer && ["failed", "closed", "disconnected"].includes(peer.connectionState)) return "N/A — viewer disconnected";
        if (!hasVideo) return "Waiting for video…";
        return null;
    }

    function updateMarkerStatus() {
        const idle = markerIdleStatus();
        if (idle) {
            ui.videoLatency.textContent = idle;
            return;
        }
        // Keep unsupported-browser / unreadable-pixel errors until a new reader starts.
        if (lastMarkerError) return;
        if (latestMarkerSampleAt !== null) {
            if (performance.now() - latestMarkerSampleAt > 10000) ui.videoLatency.textContent = "N/A — no recent marker sample; send input";
        } else if (markerDetected) {
            ui.videoLatency.textContent = controlLease
                ? "Marker detected — send input in the video"
                : "Marker detected — click this player and send input";
        } else if (markerStartedAt !== null && performance.now() - markerStartedAt > 8000) {
            ui.videoLatency.textContent = "N/A — runtime marker unavailable";
        } else {
            ui.videoLatency.textContent = "Waiting for the runtime marker…";
        }
    }

    function startMarkerReader(context = viewerContext) {
        stopMarkerReader();
        if (markerIdleStatus() || !isCurrentViewer(context)) {
            updateMarkerStatus();
            return;
        }
        if (typeof ui.video.requestVideoFrameCallback !== "function") {
            lastMarkerError = true;
            ui.videoLatency.textContent = "N/A — video frame callbacks unsupported";
            return;
        }
        videoMarkerCanvas = document.createElement("canvas");
        videoMarkerCanvas.width = 80;
        videoMarkerCanvas.height = 80;
        videoMarkerContext = videoMarkerCanvas.getContext("2d", { willReadFrequently: true });
        if (!videoMarkerContext) {
            lastMarkerError = true;
            ui.videoLatency.textContent = "N/A — frame pixels unavailable";
            return;
        }
        const generation = markerReaderGeneration;
        markerStartedAt = performance.now();
        updateMarkerStatus();
        // Video-frame callbacks stop when frames stop; a wall-clock poll keeps
        // missing-marker and stale-sample statuses honest even on a frozen stream.
        markerStatusPoll = globalThis.setInterval(updateMarkerStatus, 1000);
        const frame = (_now, metadata) => {
            if (generation !== markerReaderGeneration || !isCurrentViewer(context) || !hasVideo
                || currentSession?.diagnostics !== true || !videoMarkerContext) return;
            if (ui.video.readyState < HTMLMediaElement.HAVE_CURRENT_DATA) {
                markerCallback = ui.video.requestVideoFrameCallback(frame);
                return;
            }
            try {
                videoMarkerContext.drawImage(ui.video, 0, 0, 80, 80, 0, 0, 80, 80);
                const pixels = videoMarkerContext.getImageData(0, 0, 80, 80).data;
                const diagnosticId = decodeMarker(pixels);
                if (diagnosticId !== null) markerDetected = true;
                if (diagnosticId !== null && input) {
                    const presentedAt = Number.isFinite(metadata?.expectedDisplayTime) ? metadata.expectedDisplayTime : performance.now();
                    const elapsed = input.markDiagnosticPresented(diagnosticId, presentedAt, Number.isFinite(metadata?.expectedDisplayTime) ? "expectedDisplayTime" : "callback");
                    if (elapsed !== null) {
                        latestMarkerSampleAt = performance.now();
                        ui.videoLatency.textContent = `${Math.round(elapsed)} ms · last sample`;
                    }
                }
            } catch {
                if (!lastMarkerError) {
                    ui.videoLatency.textContent = "N/A — browser cannot read the video marker pixels";
                    lastMarkerError = true;
                }
            }
            updateMarkerStatus();
            markerCallback = ui.video.requestVideoFrameCallback(frame);
        };
        markerCallback = ui.video.requestVideoFrameCallback(frame);
    }

    function stopMarkerReader() {
        markerReaderGeneration++;
        if (markerCallback && typeof ui.video.cancelVideoFrameCallback === "function") ui.video.cancelVideoFrameCallback(markerCallback);
        if (markerStatusPoll) globalThis.clearInterval(markerStatusPoll);
        markerStatusPoll = 0;
        markerCallback = 0;
        videoMarkerCanvas = null;
        videoMarkerContext = null;
        markerStartedAt = null;
        latestMarkerSampleAt = null;
        markerDetected = false;
        lastMarkerError = false;
    }

    function decodeMarker(pixels) {
        const cells = [];
        for (let index = 0; index < 64; index++) {
            const row = Math.floor(index / 8);
            const column = index % 8;
            const offset = ((8 + row * 8 + 4) * 80 + (8 + column * 8 + 4)) * 4;
            const luminance = pixels[offset] * 0.2126 + pixels[offset + 1] * 0.7152 + pixels[offset + 2] * 0.0722;
            cells.push(luminance >= 128 ? 1 : 0);
        }
        if (cells[0] !== 1 || cells[7] !== 0 || cells[56] !== 0 || cells[63] !== 1) return null;
        const dataCells = [];
        for (let index = 0; index < 64; index++) if (![0, 7, 56, 63].includes(index)) dataCells.push(index);
        let diagnosticId = 0;
        for (let bit = 0; bit < 32; bit++) diagnosticId |= cells[dataCells[bit]] << bit;
        diagnosticId >>>= 0;
        let checksum = 0;
        for (let bit = 0; bit < 16; bit++) checksum |= cells[dataCells[32 + bit]] << bit;
        checksum >>>= 0;
        const expectedChecksum = (diagnosticId ^ (diagnosticId >>> 16) ^ 0xA5A5) & 0xFFFF;
        if (checksum !== expectedChecksum) return null;
        for (let bit = 48; bit < 60; bit++) {
            const rasterIndex = dataCells[bit];
            if (cells[rasterIndex] !== (rasterIndex % 2)) return null;
        }
        return diagnosticId;
    }


        function appendLog(level, message) {
            latestSessionLogs.push({ level, message: label() + ": " + message, utc: new Date().toISOString() });
            if (latestSessionLogs.length > 12) latestSessionLogs.shift();
            if (focusedInstanceId === currentSession.instanceId) renderLogs(latestSessionLogs);
        }

        function snapshot() {
            const enabled = currentSession.diagnostics === true;
            const inputSnapshot = enabled ? input?.snapshot() : null;
            return {
                sessionId: currentSession.sessionId, instanceId: currentSession.instanceId,
                groupId: currentSession.groupId || null, index: currentSession.index ?? position,
                role: currentSession.role || "single", label: label(), state: currentSession.state,
                focused: focusedInstanceId === currentSession.instanceId, controlActive: canInput(),
                mouseLockRequested: currentSession.mouseLockRequested === true,
                mouseLocked: document.pointerLockElement === video,
                diagnosticsEnabled: enabled, capturedAtMs: performance.now(),
                viewer: { generation: viewerGeneration, socketState: viewerSocket ? viewerSocket.readyState : WebSocket.CLOSED,
                    peerState: peer?.connectionState || "closed", dataChannelState: dataChannel?.readyState || "closed" },
                video: { width: ui.video.videoWidth || 0, height: ui.video.videoHeight || 0, fps: latestVideoFps,
                    ...(enabled && latestVideoStats ? latestVideoStats : {}) },
                input: inputSnapshot ? { ...inputSnapshot, sentSeq: inputSnapshot.lastSentSeq, ackedSeq: inputSnapshot.lastAckedSeq,
                    pending: inputSnapshot.pendingCount, gaps: inputSnapshot.ackGaps, resets: inputSnapshot.resetCount,
                    inputToVideoSamples: inputSnapshot.frameSamples } : null,
                marker: { enabled, detected: enabled && markerDetected, status: ui.videoLatency.textContent,
                    latestSampleAtMs: enabled ? latestMarkerSampleAt : null,
                    timestampSources: ["expectedDisplayTime", "callback"], measurement: "browser input dispatch to presented video frame; not network-only latency" }
            };
        }

        input = createInput();
        node.addEventListener("click", () => selectTile(tile));
        ui.video.addEventListener("pointerdown", onVideoPointerDown);
        ui.video.addEventListener("pointermove", onVideoPointerMove);
        ui.video.addEventListener("wheel", onVideoWheel, { passive: false });
        ui.video.addEventListener("contextmenu", event => { if (canInput()) event.preventDefault(); });
        ui.video.addEventListener("blur", () => {
            if (canInput() && document.pointerLockElement !== ui.video) {
                cancelQueuedPointer();
                input.releaseAll("Video lost keyboard focus", true);
            }
        });
        updateRecord(record);
        return tile;
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", initialize, { once: true });
    else initialize();
})();
