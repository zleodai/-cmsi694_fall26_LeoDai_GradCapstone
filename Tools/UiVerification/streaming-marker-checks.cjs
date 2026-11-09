"use strict";

// Shared multistream VM, production input encoder, and independent marker pixels.
// No browser, backend, Unity, native player, listener, or persistent files.
const fixture = require("./streaming-multistream-checks.cjs");
const { setup, session, markerPixels, settle, check } = fixture;
const status = (h, tile) => h.metric(tile, "video-latency");
const samples = tile => tile.input.snapshot().frameSamples;

async function main() {
    const initialCount = fixture.checks;
    const idle = await setup();
    check(idle.frames.size === 0, "idle dashboard has no marker frame callbacks");
    for (const preference of [true, false]) {
        idle.node("diagnostics").checked = preference;
        idle.node("diagnostics").emit("change");
        for (let index = 0; index < 3; index++) await idle.refresh([]);
        check(idle.node("diagnostics").checked === preference, `idle refresh preserves the ${preference ? "checked" : "unchecked"} next-launch marker choice`);
        check(idle.frames.size === 0, "idle marker preference starts no reader");
    }
    await idle.close();

    for (const preference of [true, false]) {
        const h = await setup();
        h.node("diagnostics").checked = preference;
        h.node("diagnostics").emit("change");
        await h.refresh([session("stopped", { state: "Stopped", diagnostics: !preference, runtimeConnected: false })]);
        check(h.node("diagnostics").checked === preference, `terminal record preserves the ${preference ? "checked" : "unchecked"} next-launch marker choice`);
        check(h.frames.size === 0, "terminal session starts no marker callbacks");
        h.node("build").value = "test-build";
        h.node("build").emit("change");
        const record = session("started", { diagnostics: preference });
        h.state({ builds: [], sessions: [record] });
        h.startReply({ sessions: [record], session: record });
        h.node("start").click(); await settle();
        const request = h.requests.find(item => item.method === "POST" && item.url.endsWith("/start"));
        check(!!request && JSON.parse(request.body).diagnostics === preference, `start sends the ${preference ? "checked" : "unchecked"} marker preference`);
        await h.close();
    }

    const off = await setup(); off.apply([session("off", { diagnostics: false })]);
    const offViewer = await off.connect("off");
    check(/disabled/i.test(status(off, offViewer.tile)), "diagnostics-off tile reports disabled measurement");
    check(off.callbacks(offViewer.video).length === 0, "diagnostics-off video schedules no marker reader");
    off.node("diagnostics").checked = true; off.node("diagnostics").emit("change");
    check(off.callbacks(offViewer.video).length === 0 && /disabled/i.test(status(off, offViewer.tile)), "setup checkbox cannot enable markers on a running diagnostics-off tile");
    await off.close();

    const waiting = await setup(); waiting.apply([session("waiting")]);
    check(/connect.*viewer/i.test(status(waiting, waiting.tile("waiting"))), "diagnostic tile without viewer asks to connect");
    const waitingViewer = await waiting.connect("waiting", false);
    check(/waiting for video/i.test(status(waiting, waitingViewer.tile)), "connected diagnostic viewer without track waits for video");
    check(waiting.callbacks(waitingViewer.video).length === 0, "viewer without video schedules no marker frames");
    await waiting.close();

    const marker = await setup(); marker.apply([session("marker")]);
    const viewer = await marker.connect("marker");
    check(marker.callbacks(viewer.video).length === 1, "video track starts exactly one tile marker reader");
    marker.frame(viewer.video);
    check(/marker detected.*click this player.*send input/i.test(status(marker, viewer.tile)), "independently encoded marker zero prompts clicking the player without claiming latency");
    check(samples(viewer.tile).length === 0, "unmatched visible marker creates no latency measurement");
    marker.pixels(viewer.video, markerPixels(0xFEDCBA98)); marker.frame(viewer.video);
    check(/marker detected/i.test(status(marker, viewer.tile)) && samples(viewer.tile).length === 0, "high-bit unsigned marker decodes without inventing a sample");
    marker.now(9001); marker.frame(viewer.video);
    check(/marker detected/i.test(status(marker, viewer.tile)) && !/unavailable/i.test(status(marker, viewer.tile)), "visible idle marker remains detected past discovery timeout");
    await marker.take(viewer); await marker.grant(viewer); await marker.advance(1000);
    check(/marker detected.*send input/i.test(status(marker, viewer.tile)) && !/take control|click.*player/i.test(status(marker, viewer.tile)), "controlled tile with detected marker asks for input without requesting another control click");
    marker.now(11000); viewer.tile.input.diagnosticId = 0xFEDCBA97; viewer.tile.input.key("KeyW", true);
    const packet = viewer.channel.sent.find(item => item.kind === "key" && item.code === "KeyW");
    check(Number.isInteger(packet?.diagnosticId), "real input encoder attaches a diagnostic ID to a sent key packet");
    check(packet.diagnosticId === 0xFEDCBA98, "input encoder preserves a high-bit unsigned diagnostic ID for the matched frame");
    marker.pixels(viewer.video, markerPixels(packet.diagnosticId)); marker.now(11045);
    marker.frame(viewer.video, { expectedDisplayTime: 11050 });
    check(/^50 ms.*last sample/i.test(status(marker, viewer.tile)), "matching sent marker reports exactly 50 ms on the browser clock");
    check(samples(viewer.tile).length === 1 && samples(viewer.tile)[0].ms === 50, "one matched frame records one accurate latency sample");
    marker.now(11200); marker.frame(viewer.video);
    check(samples(viewer.tile).length === 1, "repeated presentation does not duplicate a marker sample");
    marker.now(22000); await marker.advance(1000);
    check(/no recent marker sample.*send input/i.test(status(marker, viewer.tile)), "stale latency status updates without new video frames");
    const oldCallback = marker.callbacks(viewer.video)[0];
    viewer.track.readyState = "ended"; viewer.track.emit("ended");
    const stoppedStatus = status(marker, viewer.tile);
    check(marker.callbacks(viewer.video).length === 0, "ending a track cancels its marker callback");
    check(/waiting for video/i.test(stoppedStatus), "ended video clears numeric and discovery statuses");
    oldCallback(23001, { expectedDisplayTime: 23001 });
    check(marker.callbacks(viewer.video).length === 0 && status(marker, viewer.tile) === stoppedStatus, "cancelled frame cannot restart its marker reader");
    viewer.track.readyState = "live";
    viewer.peer.emit("track", { track: viewer.track, streams: [{ getTracks: () => [viewer.track] }] });
    check(marker.callbacks(viewer.video).length === 1, "replacement track starts exactly one new marker reader");
    const restartedStatus = status(marker, viewer.tile);
    oldCallback(23002, { expectedDisplayTime: 23002 });
    check(marker.callbacks(viewer.video).length === 1 && status(marker, viewer.tile) === restartedStatus, "previous reader generation cannot overwrite a restarted reader");
    await marker.refresh([session("marker", { state: "Stopped", runtimeConnected: false })]);
    check(marker.callbacks(viewer.video).length === 0, "terminal session cancels marker callbacks");
    await marker.close();

    const replacement = await setup(); replacement.apply([session("same")]);
    const original = await replacement.connect("same"); replacement.frame(original.video);
    const priorCallback = replacement.callbacks(original.video)[0];
    await replacement.refresh([session("same", { sessionId: "replacement-session" })]);
    check(replacement.callbacks(original.video).length === 0, "session replacement cancels the old marker callback");
    const next = await replacement.connect("same"); const nextStatus = status(replacement, next.tile);
    priorCallback(100, { expectedDisplayTime: 100 });
    check(status(replacement, next.tile) === nextStatus && replacement.callbacks(next.video).length === 1, "stale session frame cannot mutate or duplicate the replacement reader");
    await replacement.close();

    const absent = await setup(); absent.apply([session("absent")]);
    const absentViewer = await absent.connect("absent");
    absent.pixels(absentViewer.video, new Uint8ClampedArray(80 * 80 * 4)); absent.frame(absentViewer.video);
    await absent.advance(9001);
    check(/runtime marker unavailable/i.test(status(absent, absentViewer.tile)), "missing-marker deadline updates on frozen video");
    absent.frame(absentViewer.video);
    check(/runtime marker unavailable/i.test(status(absent, absentViewer.tile)), "invalid pixels cannot recover marker availability");
    absent.pixels(absentViewer.video, markerPixels(0)); absent.frame(absentViewer.video);
    check(/marker detected.*click this player.*send input/i.test(status(absent, absentViewer.tile)), "valid marker recovers availability and the click-to-control prompt without reconnecting");
    await absent.close();

    const unsupported = await setup({ unsupported: true }); unsupported.apply([session("unsupported")]);
    const unsupportedViewer = await unsupported.connect("unsupported");
    check(/callbacks unsupported/i.test(status(unsupported, unsupportedViewer.tile)), "unsupported browser explains missing frame callbacks");
    check(unsupported.callbacks(unsupportedViewer.video).length === 0, "unsupported browser schedules no marker callback");
    await unsupported.close();

    const unreadable = await setup(); unreadable.apply([session("unreadable")]);
    const unreadableViewer = await unreadable.connect("unreadable");
    unreadable.pixelError(unreadableViewer.video, true); unreadable.frame(unreadableViewer.video);
    check(/cannot read.*marker pixels/i.test(status(unreadable, unreadableViewer.tile)), "pixel-read failure reports the browser limitation");
    await unreadable.advance(10000);
    check(/cannot read.*marker pixels/i.test(status(unreadable, unreadableViewer.tile)), "discovery timeout does not overwrite a pixel-read failure");
    await unreadable.close();

    const noCanvas = await setup({ canvasUnavailable: true }); noCanvas.apply([session("canvas")]);
    const noCanvasViewer = await noCanvas.connect("canvas");
    check(/frame pixels unavailable/i.test(status(noCanvas, noCanvasViewer.tile)) && noCanvas.callbacks(noCanvasViewer.video).length === 0,
        "unavailable canvas reports limitation without scheduling frames");
    await noCanvas.close();
    process.stdout.write(`SUCCESS: ${fixture.checks - initialCount} streaming marker lifecycle checks\n`);
}

main().catch(error => { process.stderr.write(`${error.stack || error}\n`); process.exitCode = 1; });
