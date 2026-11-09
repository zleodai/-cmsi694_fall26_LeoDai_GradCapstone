async (page) => {
    if (new URL(page.url()).origin !== "http://localhost:5286") throw new Error("Only the isolated fixture dashboard is allowed");
    await page.getByRole("button", { name:"Stop session", exact:true }).click();
    const deadline = Date.now()+10000;
    while(Date.now()<deadline) {
        const response=await page.request.get("http://localhost:5286/api/streaming/state");
        const state=await response.json();
        if(!state.session || state.session.exited) return {sessionId:state.session?.sessionId,state:state.session?.state,exited:state.session?.exited,exitCode:state.session?.exitCode,logs:state.session?.logs.slice(-5)};
        await page.waitForTimeout(200);
    }
    throw new Error("The owned native player did not exit within the stop bound");
}
