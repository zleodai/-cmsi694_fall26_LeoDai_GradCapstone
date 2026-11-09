async (page) => {
    if (new URL(page.url()).origin !== "http://localhost:5286") throw new Error("Only the isolated streaming fixture dashboard is allowed");
    const response = await page.request.get("http://localhost:5286/api/streaming/state");
    const state = await response.json();
    if (state.session && !state.session.exited) throw new Error("An active session already exists; inspect it before starting another");
    const build = state.builds.find(item => item.projectName === "PlaytestOps Streaming Probe" && item.streamingCapable);
    if (!build) throw new Error("No approved streaming fixture build is available");
    await page.getByLabel("Successful build", { exact: true }).selectOption(build.buildId);
    await page.getByLabel("Capture width", { exact: true }).fill("1280");
    await page.getByLabel("Capture height", { exact: true }).fill("720");
    await page.getByLabel("Enable the visible input-to-video marker", { exact: false }).check();
    await page.getByRole("button", { name: "Start stream", exact: true }).click();
    return { buildId: build.buildId, requestedWidth: 1280, requestedHeight: 720, diagnostics: true,
        summary: await page.locator("#streaming-session-summary").innerText() };
}
