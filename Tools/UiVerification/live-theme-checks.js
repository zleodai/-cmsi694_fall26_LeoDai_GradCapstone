async (page) => {
  if (new URL(page.url()).origin !== "http://localhost:5282") throw new Error("This check is scoped to the local dashboard.");
  const results = [];
  const check = (ok, label) => {if (!ok) throw new Error(label); results.push(label);};
  const origin = "http://localhost:5282";
  const logPath = "/Runs/b3522a424ce34e539712213e9eff1e2b";
  await page.setViewportSize({width:1280,height:1000});
  for (const theme of ["dark", "light"]) {
    await page.goto(origin);
    if (await page.locator("html").getAttribute("data-theme") !== theme) await page.getByRole("button",{name:"Dark mode",exact:true}).click();
    await page.reload();
    check(await page.locator("html").getAttribute("data-theme") === theme, theme + " live theme survives reload");
    check(await page.locator(".test-table tbody tr").count() === 2, theme + " live tests retain two connected-Editor rows");
    check((await page.locator(".test-table").textContent()).includes("PlaytestOps Test"), theme + " live tests belong to target Unity project");
    await page.screenshot({path:"output/playwright/live-" + theme + "-tests.png",fullPage:true});
    await page.goto(origin + "/Editors");
    check((await page.locator("main").textContent()).includes("PlaytestOps Test") && (await page.locator("main").textContent()).includes("Connected"), theme + " Editors page retains connected target");
    await page.goto(origin + "/Scripts");
    check((await page.locator("main").textContent()).includes("No eligible"), theme + " source page retains honest empty author-folder state");
    check(await page.locator("html").getAttribute("data-theme") === theme, theme + " preference follows navigation");
    await page.goto(origin + logPath);
    const severities = await page.locator(".log-severity").allTextContents();
    check(["Debug", "Warning", "Error"].every(value => severities.includes(value)), theme + " saved logs retain all three severity levels");
    const contrast = await page.locator(".log-severity").evaluateAll(elements => {
      const lum = value => value.match(/[\d.]+/g).slice(0,3).map(Number).map(n => {n /= 255;return n <= .04045 ? n/12.92 : ((n+.055)/1.055)**2.4;}).reduce((sum,n,i)=>sum+n*[.2126,.7152,.0722][i],0);
      return Math.min(...elements.map(el => {const style=getComputedStyle(el), fg=lum(style.color), bg=lum(style.backgroundColor);return (Math.max(fg,bg)+.05)/(Math.min(fg,bg)+.05);}));
    });
    check(contrast >= 4.5, theme + " severity badge contrast >= 4.5:1 (minimum " + contrast.toFixed(2) + ")");
    await page.screenshot({path:"output/playwright/live-" + theme + "-logs.png",fullPage:true});
    await page.setViewportSize({width:390,height:844});
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), theme + " live mobile logs have no document overflow");
    await page.screenshot({path:"output/playwright/live-" + theme + "-logs-mobile.png",fullPage:true});
    await page.setViewportSize({width:1280,height:1000});
  }
  const assets = ["/js/theme.js", "/js/csharp-highlighting.js", "/js/csharp-worker.js", "/css/syntax.css", "/lib/prism/1.30.0/prism-core.min.js", "/lib/prism/1.30.0/prism-clike.min.js", "/lib/prism/1.30.0/prism-csharp.min.js"];
  check(await page.evaluate(async paths => (await Promise.all(paths.map(async path => (await fetch(path)).status))).every(status => status === 200), assets), "all locally bundled highlighting/theme assets are served");
  await page.getByRole("button",{name:"Dark mode",exact:true}).click();
  await page.goto(origin);
  check(await page.locator("html").getAttribute("data-theme") === "dark", "live browser ends on the tests view in dark mode");
  return {passed:results.length,results};
}
