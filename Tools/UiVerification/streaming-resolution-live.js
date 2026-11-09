async (page) => {
    if (new URL(page.url()).origin !== "http://localhost:5286") throw new Error("Only the isolated fixture dashboard is allowed");
    const state = await (await page.request.get("http://localhost:5286/api/streaming/state")).json();
    if (state.session && !state.session.exited) throw new Error("Stop the current native player first");
    const build=state.builds.find(item=>item.projectName==="PlaytestOps Streaming Probe"&&item.streamingCapable);
    await page.getByLabel("Successful build",{exact:true}).selectOption(build.buildId);
    await page.getByLabel("Capture width",{exact:true}).fill("960");
    await page.getByLabel("Capture height",{exact:true}).fill("540");
    await page.getByRole("button",{name:"Start streams",exact:true}).click();
    await page.waitForFunction(()=>document.querySelector("#streaming-video").videoWidth===960&&document.querySelector("#streaming-video").videoHeight===540);
    await page.locator("#streaming-video").click();
    await page.waitForFunction(()=>window.playtestopsStreaming?.controlActive);
    await page.locator("#streaming-video").focus();
    for(let index=0;index<10;index++){
        await page.keyboard.down("Space");await page.waitForTimeout(80);
        await page.keyboard.up("Space");await page.waitForTimeout(80);
    }
    await page.waitForTimeout(300);
    const proof=await page.evaluate(()=>window.playtestopsStreaming);
    await page.keyboard.press("Escape");
    if(proof.input.pendingCount||proof.input.ackGaps||proof.input.runtimeErrorCount)throw new Error("Alternate-size input failed");
    return {buildId:build.buildId,sessionId:proof.sessionId,video:proof.video,
        lastSent:proof.input.lastSentSeq,lastAcked:proof.input.lastAckedSeq,gaps:proof.input.ackGaps,
        visualLatencySamples:proof.input.frameSamples.length};
}
