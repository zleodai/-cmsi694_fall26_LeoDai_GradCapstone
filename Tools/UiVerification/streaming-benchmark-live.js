async (page) => {
    if (new URL(page.url()).origin !== "http://localhost:5286") throw new Error("Only the isolated fixture dashboard is allowed");
    const before = await page.evaluate(() => window.playtestopsStreaming);
    if (!before?.controlActive || before.viewer?.dataChannelState !== "open") throw new Error("Acquire the real control lease first");
    const video = page.locator("#streaming-video");
    await video.focus();
    for (let index = 0; index < 100; index++) {
        await page.keyboard.down("Space");
        await page.waitForTimeout(80);
        await page.keyboard.up("Space");
        await page.waitForTimeout(80);
    }
    for (const key of ["w", "a", "s", "d"]) {
        await page.keyboard.down(key);
        await page.waitForTimeout(250);
        await page.keyboard.up(key);
        await page.waitForTimeout(80);
    }
    const box = await video.boundingBox();
    await page.mouse.move(box.x + box.width * 0.4, box.y + box.height * 0.4);
    for (const button of ["left", "right", "middle"]) {
        for (let index = 0; index < 20; index++) {
            await page.mouse.down({ button });
            await page.waitForTimeout(50);
            await page.mouse.up({ button });
            await page.waitForTimeout(50);
        }
    }
    await page.waitForTimeout(400);
    const after = await page.evaluate(() => window.playtestopsStreaming);
    if (after.input.pendingCount || after.input.ackGaps !== before.input.ackGaps || after.input.runtimeErrorCount !== before.input.runtimeErrorCount)
        throw new Error("Incomplete input delivery: " + JSON.stringify(after.input));
    const stats = values => {
        const sorted = values.map(value => value.ms).filter(Number.isFinite).sort((a,b) => a-b);
        const at = q => sorted[Math.min(sorted.length-1, Math.ceil(sorted.length*q)-1)] ?? null;
        return { count: sorted.length, min: sorted[0] ?? null, p50: at(.5), p95: at(.95), max: sorted.at(-1) ?? null };
    };
    return { expected: {spaceDown:100,spaceUp:100,keyDown:4,keyUp:4,mouseDown:20,mouseUp:20,rightDown:20,rightUp:20,middleDown:20,middleUp:20},
        ackRttMs:stats(after.input.rttSamples), inputToPresentedFrameMs:stats(after.input.frameSamples), proof:after };
}
