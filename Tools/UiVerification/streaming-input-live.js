async (page) => {
    if (new URL(page.url()).origin !== "http://localhost:5286") throw new Error("Only the isolated fixture dashboard is allowed");
    const before = await page.evaluate(() => window.playtestopsStreaming);
    if (!before?.controlActive || before.viewer?.dataChannelState !== "open") throw new Error("Acquire the real control lease before testing");
    await page.locator("#streaming-video").focus();
    for (let index = 0; index < 20; index++) {
        await page.keyboard.down("Space");
        await page.waitForTimeout(30);
        await page.keyboard.up("Space");
        await page.waitForTimeout(30);
    }
    await page.keyboard.down("w");
    await page.waitForTimeout(300);
    await page.keyboard.up("w");
    await page.waitForTimeout(300);
    const after = await page.evaluate(() => window.playtestopsStreaming);
    if (after.input.pendingCount !== 0 || after.input.ackGaps !== before.input.ackGaps || after.input.runtimeErrorCount !== before.input.runtimeErrorCount)
        throw new Error("Input did not settle with complete acknowledgments: " + JSON.stringify(after.input));
    const summarize = value => ({lastSentSeq:value.lastSentSeq,lastAckedSeq:value.lastAckedSeq,
        ackedCount:value.ackedCount,pending:value.pendingCount,gaps:value.ackGaps,resets:value.resetCount,
        errors:value.runtimeErrorCount,resends:value.resendCount});
    return { taps:20, expectedSpaceDown:20, expectedSpaceUp:20, expectedWDown:1, expectedWUp:1,
        before:summarize(before.input), after:summarize(after.input) };
}
