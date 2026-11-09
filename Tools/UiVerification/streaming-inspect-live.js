async (page) => {
    if (new URL(page.url()).origin !== "http://localhost:5286") throw new Error("Only the isolated fixture dashboard is allowed");
    return await page.evaluate(() => {
        const video = document.querySelector("#streaming-video");
        const proof = window.playtestopsStreaming;
        let pixels = null;
        if (video.videoWidth && video.videoHeight) {
            const canvas = document.createElement("canvas");
            canvas.width = video.videoWidth; canvas.height = video.videoHeight;
            const context = canvas.getContext("2d", { willReadFrequently: true });
            context.drawImage(video, 0, 0);
            const color = (x,y) => [...context.getImageData(x,y,1,1).data];
            pixels = { topLeft: color(12,12), center: color(canvas.width/2,canvas.height/2),
                spaceIndicator: color(canvas.width-140,canvas.height-88),
                header: color(104,84), corners: [color(2,2),color(canvas.width-3,2),color(2,canvas.height-3)] };
        }
        return { width: video.videoWidth, height: video.videoHeight, readyState: video.readyState,
            currentTime: video.currentTime, pixels, proof,
            control: document.querySelector("#streaming-control-status").textContent,
            alert: document.querySelector("#streaming-alert").textContent,
            latency: document.querySelector("#streaming-video-latency").textContent };
    });
}
