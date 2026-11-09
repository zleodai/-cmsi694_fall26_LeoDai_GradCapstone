async (page) => {
    if (new URL(page.url()).origin !== "http://localhost:5286") throw new Error("Only the isolated fixture dashboard is allowed");
    await page.locator("#streaming-video").focus();
    const read = () => page.evaluate(() => {
        const video = document.querySelector("#streaming-video");
        const canvas = document.createElement("canvas");
        canvas.width = video.videoWidth; canvas.height = video.videoHeight;
        const context = canvas.getContext("2d"); context.drawImage(video, 0, 0);
        const sample = (x,y) => [...context.getImageData(x,y,1,1).data].slice(0,3);
        return {width:canvas.width,height:canvas.height,indicator:sample(canvas.width-140,canvas.height-88),
            header:sample(104,84),markerCorner:sample(12,12),markerBorder:sample(5,5)};
    });
    const before = await read();
    await page.keyboard.down("Space");
    await page.waitForFunction(() => {
        const v=document.querySelector("#streaming-video"), c=document.createElement("canvas");c.width=v.videoWidth;c.height=v.videoHeight;
        const x=c.getContext("2d");x.drawImage(v,0,0);const p=x.getImageData(c.width-140,c.height-88,1,1).data;
        return p[1]>200 && p[0]<40;
    });
    const held = await read();
    await page.locator("#streaming-video").screenshot({path:"output/playwright/streaming-space-held.png"});
    await page.keyboard.up("Space");
    await page.waitForFunction(() => {
        const v=document.querySelector("#streaming-video"), c=document.createElement("canvas");c.width=v.videoWidth;c.height=v.videoHeight;
        const x=c.getContext("2d");x.drawImage(v,0,0);const p=x.getImageData(c.width-140,c.height-88,1,1).data;
        return p[0]>200 && p[1]<40;
    });
    return {before,held,released:await read()};
}
