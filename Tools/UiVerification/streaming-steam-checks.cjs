"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { setup, check, settle } = require("./streaming-multistream-checks.cjs");

async function selectBuild(h, id) {
    h.node("build").value = id;
    h.node("build").emit("change");
    await settle();
}

async function main() {
    const build = { buildId: "steam-build", streamingCapable: true, supportsArenaKcp: true, supportsArenaSteam: true };
    const environments = { available: true, environments: [
        { name: "DefaultBox", available: true },
        { name: "SteamClient2", available: true },
        { name: "DisabledBox", available: false, reason: "Sandboxie box is disabled" }
    ] };
    const h = await setup({ state: { builds: [build], sessions: [] }, steamEnvironments: environments });
    await selectBuild(h, build.buildId);

    check(!h.node("profile-field").hidden && h.node("profile").options.map(option => option.textContent).join("|") ===
        "Single player|Local multiplayer|Steam multiplayer", "multi-capability build exposes the three supported play modes");
    check(h.node("profile").value === "single", "dual-capability build retains the single-player default");

    h.node("profile").value = "arena-steam";
    h.node("profile").emit("change");
    await settle();
    check(h.node("instance-count").value === "2" && h.node("steam-sandboxes").hidden === false,
        "Steam multiplayer selects the minimum host plus one sandbox client and reveals box selection");
    const boxes = h.node("steam-sandbox-options").querySelectorAll("input[type=checkbox]");
    check(boxes.length === 3 && boxes[2].disabled, "only configured boxes are listed and unavailable boxes stay disabled");
    check(h.node("start").disabled, "Steam start stays disabled until its exact sandbox selection is made");

    boxes[0].checked = true;
    boxes[0].emit("change");
    await settle();
    check(!h.node("start").disabled && boxes[1].disabled,
        "one selected box enables host plus one client and prevents selecting an extra box");

    h.node("instance-count").value = "3";
    h.node("instance-count").emit("change");
    await settle();
    check(h.node("start").disabled && !boxes[1].disabled,
        "increasing player count requires one more explicitly selected sandbox box");
    boxes[1].checked = true;
    boxes[1].emit("change");
    await settle();

    const records = [
        { sessionId: "host-session", instanceId: "host-instance", groupId: "steam-group", state: "Ready", runtimeConnected: true, logs: [] },
        { sessionId: "client-1-session", instanceId: "client-1", groupId: "steam-group", state: "Ready", runtimeConnected: true, logs: [] },
        { sessionId: "client-2-session", instanceId: "client-2", groupId: "steam-group", state: "Ready", runtimeConnected: true, logs: [] }
    ];
    h.state({ builds: [build], sessions: records });
    h.startReply({ sessions: records });
    h.node("start").click();
    await settle();
    const request = h.requests.find(item => item.method === "POST" && item.url.endsWith("/start"));
    const body = request && JSON.parse(request.body);
    check(body?.launchProfile === "arena-steam" && body.instanceCount === 3 &&
        JSON.stringify(body.steamSandboxes) === JSON.stringify(["DefaultBox", "SteamClient2"]),
        "Steam start posts the profile, total player count, and exactly the selected sandbox names");
    check(request.credentials === "same-origin" && request.headers.RequestVerificationToken === "fake-antiforgery-secret",
        "Steam start preserves existing same-origin antiforgery protection");
    await h.close();

    const steamOnlyBuild = { buildId: "steam-only", streamingCapable: true, supportsArenaKcp: false, supportsArenaSteam: true };
    const only = await setup({ state: { builds: [steamOnlyBuild], sessions: [] }, steamEnvironments: environments });
    await selectBuild(only, steamOnlyBuild.buildId);
    check(only.node("profile").value === "arena-steam" && only.node("instance-count").value === "2" &&
        only.node("instance-count").options[0].disabled,
        "Steam-only build defaults to Steam multiplayer and requires at least two players");
    await only.close();

    const refresh = await setup({ state: { builds: [build], sessions: [] }, steamEnvironments: environments });
    await selectBuild(refresh, build.buildId);
    refresh.node("profile").value = "arena-steam";
    refresh.node("profile").emit("change");
    await settle();
    let defaultBox = refresh.node("steam-sandbox-options").querySelectorAll("input[type=checkbox]").find(box => box.value === "DefaultBox");
    defaultBox.checked = true;
    defaultBox.emit("change");
    await settle();
    check(!refresh.node("start").disabled, "a ready box enables Steam multiplayer before refresh");

    refresh.steamEnvironments({ available: true, environments: [
        { name: "SteamClient2", available: true },
        { name: "DefaultBox", available: true }
    ] });
    const environmentRequests = () => refresh.requests.filter(item => item.url.endsWith("/steam-environments") && item.method === "GET").length;
    const beforeRefresh = environmentRequests();
    refresh.node("refresh").click();
    await settle();
    defaultBox = refresh.node("steam-sandbox-options").querySelectorAll("input[type=checkbox]").find(box => box.value === "DefaultBox");
    check(environmentRequests() === beforeRefresh + 1 && defaultBox.checked,
        "Refresh state rechecks environments and preserves an available selection by name after reordering");

    refresh.steamEnvironments({ available: true, environments: [
        { name: "SteamClient2", available: true },
        { name: "DefaultBox", available: false, reason: "Steam exited" }
    ] });
    refresh.node("refresh").click();
    await settle();
    defaultBox = refresh.node("steam-sandbox-options").querySelectorAll("input[type=checkbox]").find(box => box.value === "DefaultBox");
    check(defaultBox.disabled && !defaultBox.checked && refresh.node("start").disabled,
        "Refresh state clears a selected box when it becomes unavailable and blocks Start");

    refresh.steamEnvironments({ available: true, environments: [
        { name: "SteamClient2", available: true },
        { name: "DefaultBox", available: true }
    ] });
    refresh.node("refresh").click();
    await settle();
    defaultBox = refresh.node("steam-sandbox-options").querySelectorAll("input[type=checkbox]").find(box => box.value === "DefaultBox");
    check(!defaultBox.disabled && !defaultBox.checked && refresh.node("start").disabled,
        "a box becoming available again requires explicit reselection instead of restoring an old choice");

    const pendingEnvironmentRefresh = refresh.deferFetch("/steam-environments");
    const beforeCoalescing = environmentRequests();
    refresh.node("refresh").click();
    await settle();
    refresh.node("refresh").click();
    refresh.node("refresh").click();
    await settle();
    check(environmentRequests() === beforeCoalescing + 1,
        "multiple Refresh clicks share the in-flight environment request");
    pendingEnvironmentRefresh.resolve({ available: true, environments: [
        { name: "SteamClient2", available: true },
        { name: "DefaultBox", available: false, reason: "Steam exited" }
    ] });
    await settle();
    check(environmentRequests() === beforeCoalescing + 2,
        "Refresh clicks during a pending query coalesce into one follow-up environment request");
    await refresh.close();

    const razor = fs.readFileSync(path.resolve(__dirname, "../../PlaytestOps.Web/Pages/Streaming.cshtml"), "utf8");
    check(razor.includes('id="streaming-profile"') && razor.includes('id="streaming-steam-sandboxes"') &&
        razor.includes("does not sign in to Steam or change sandbox settings"),
        "Streaming page contains the profile and sandbox selection without a sign-in/configuration flow");
    check(!razor.includes('id="streaming-take-control"') && !razor.includes('id="streaming-release-control"'),
        "Steam setup does not restore deleted control buttons");
    process.stdout.write("SUCCESS: focused Steam streaming UI checks\n");
}

main().catch(error => { process.stderr.write(`${error.stack || error}\n`); process.exitCode = 1; });
