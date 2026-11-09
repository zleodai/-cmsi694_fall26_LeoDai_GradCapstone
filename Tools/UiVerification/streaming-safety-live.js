async (page) => {
    if (new URL(page.url()).origin !== "http://localhost:5286") throw new Error("Only the isolated fixture dashboard is allowed");
    const identity = await page.evaluate(() => window.playtestopsStreaming);
    if (!identity?.controlActive) throw new Error("Acquire control first");
    const csrf = await page.locator('#streaming-antiforgery input[name="__RequestVerificationToken"]').inputValue();
    const headers = {Origin:"http://localhost:5286",RequestVerificationToken:csrf};
    const current = (await (await page.request.get("http://localhost:5286/api/streaming/state")).json()).session;
    const secondStart = await page.request.post("http://localhost:5286/api/streaming/start", {headers,data:{buildId:current.buildId,width:1280,height:720,diagnostics:true}});
    if (secondStart.status() !== 409) throw new Error("Concurrent start was not rejected: " + secondStart.status());
    const secondViewer = await page.request.post(`http://localhost:5286/api/streaming/${identity.sessionId}/viewer-grant`,{headers,data:{}});
    if (secondViewer.status() !== 409) throw new Error("Second viewer could steal the session: " + secondViewer.status());
    const isGreen = () => {
        const v=document.querySelector("#streaming-video"),c=document.createElement("canvas");c.width=v.videoWidth;c.height=v.videoHeight;
        if(!c.width)return false;const x=c.getContext("2d");x.drawImage(v,0,0);const p=x.getImageData(c.width-140,c.height-88,1,1).data;
        return p[1]>200 && p[0]<40;
    };
    const isRed = () => {
        const v=document.querySelector("#streaming-video"),c=document.createElement("canvas");c.width=v.videoWidth;c.height=v.videoHeight;
        if(!c.width)return false;const x=c.getContext("2d");x.drawImage(v,0,0);const p=x.getImageData(c.width-140,c.height-88,1,1).data;
        return p[0]>200 && p[1]<40;
    };
    // Clear the automation client's physical-key bookkeeping after a prior interrupted run.
    await page.keyboard.up("Space");
    await page.locator("#streaming-video").focus();
    await page.keyboard.down("Space"); await page.waitForFunction(isGreen);
    await page.getByRole("button",{name:"Refresh state",exact:true}).focus();
    await page.waitForFunction(isRed);
    await page.keyboard.up("Space");
    const afterBlur = await page.evaluate(() => window.playtestopsStreaming);
    await page.locator("#streaming-video").focus();
    await page.keyboard.down("Space"); await page.waitForFunction(isGreen);
    await page.reload();
    await page.getByRole("button",{name:"Connect viewer",exact:true}).click();
    await page.waitForFunction(() => window.playtestopsStreaming?.viewer?.dataChannelState === "open");
    await page.waitForFunction(isRed);
    await page.keyboard.up("Space");
    const afterReload = await page.evaluate(() => window.playtestopsStreaming);
    if (afterReload.controlActive || afterReload.sessionId !== identity.sessionId) throw new Error("Reload retained a controller or changed the native session");
    return {secondStart:secondStart.status(),secondViewer:secondViewer.status(),blurReleased:afterBlur.input.resetCount>0,
        reloadReleased:true,sameNativeSession:true,controlRequiresFreshTakeover:!afterReload.controlActive};
}
