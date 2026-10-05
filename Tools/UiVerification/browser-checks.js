async (page) => {
  const selectedUrl = page.url().replace(/&uiFixture=[^&]*/g, "");
  if (!selectedUrl.includes("/Scripts?") || !selectedUrl.includes("path=")) throw new Error("Select the isolated preview fixture first.");
  const results = [];
  const check = (ok, label) => { if (!ok) throw new Error(label); results.push(label); };
  const code = () => page.locator("code[data-csharp-highlight]");
  const state = async expected => page.waitForFunction(value => document.querySelector("code[data-csharp-highlight]")?.dataset.highlightState === value, expected);
  const settleButtons = async () => page.evaluate(async () => {
    const buttons = [...document.querySelectorAll(".btn")];
    buttons.forEach(button => getComputedStyle(button).color);
    await Promise.all(buttons.flatMap(button => button.getAnimations()).map(animation => animation.finished.catch(() => {})));
  });
  await page.route("**/js/csharp-worker.js", route => route.abort());
  await page.goto(selectedUrl); await state("plain");
  const original = await code().textContent();
  check(original.includes("日本語 😀") && original.includes("<img src=x"), "plain fixture retains Unicode and HTML-like comments");
  await page.unroute("**/js/csharp-worker.js");
  await page.reload(); await state("ready");
  check(await code().textContent() === original, "highlighted DOM source exactly matches unhighlighted DOM source");
  check(await code().locator("img,script,iframe").count() === 0, "highlighted source contains no executable elements");
  const contrast = async () => page.evaluate(() => {
    const rgb = value => value.match(/[\d.]+/g).slice(0, 3).map(Number);
    const lum = value => rgb(value).map(n => { n /= 255; return n <= .04045 ? n / 12.92 : ((n + .055) / 1.055) ** 2.4; }).reduce((sum, n, i) => sum + n * [.2126, .7152, .0722][i], 0);
    const bg = lum(getComputedStyle(document.querySelector("#source-code")).backgroundColor);
    return Math.min(...[...document.querySelectorAll("#source-code .token")].map(token => {
      const fg = lum(getComputedStyle(token).color); return (Math.max(fg, bg) + .05) / (Math.min(fg, bg) + .05);
    }));
  });
  for (const theme of ["dark", "light"]) {
    if (await page.locator("html").getAttribute("data-theme") !== theme) await page.getByRole("button", {name:"Dark mode", exact:true}).click();
    await settleButtons();
    check(await page.getByRole("button", {name:"Dark mode", exact:true}).getAttribute("aria-pressed") === String(theme === "dark"), theme + " toggle state is accessible");
    const ratio = await contrast();
    check(ratio >= 4.5, theme + " computed token contrast >= 4.5:1 (minimum " + ratio.toFixed(2) + ")");
    await page.setViewportSize({width:1280,height:1000});
    await page.screenshot({path:"output/playwright/" + theme + "-code-desktop.png",fullPage:true});
    await page.reload(); await state("ready");
    check(await page.locator("html").getAttribute("data-theme") === theme, theme + " preference survives reload");
  }
  await page.getByRole("button", {name:"Dark mode", exact:true}).focus();
  await page.keyboard.press("Enter");
  await settleButtons();
  check(await page.locator("html").getAttribute("data-theme") === "dark", "keyboard Enter switches back to dark");
  await page.setViewportSize({width:390,height:844});
  check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), "mobile page has no horizontal document overflow");
  check(await page.locator("#source-code").evaluate(el => el.scrollWidth <= el.clientWidth + 1 && getComputedStyle(el).whiteSpace === "pre-wrap"), "long code wraps inside its own panel on mobile");
  await page.screenshot({path:"output/playwright/dark-code-mobile.png",fullPage:true});

  await page.addInitScript(() => {
    const mode = new URL(location.href).searchParams.get("uiFixture");
    if (mode === "crlf" || mode === "nbsp") document.addEventListener("readystatechange", () => {
      if (document.readyState !== "interactive") return;
      const source = mode === "crlf" ? "// CRLF\r\npublic class Example {\r    int Value = 42;\r\n}\r\n" : "public\u00a0class Example {}";
      document.querySelector("code[data-csharp-highlight]").textContent = source;
      window.__expectedFixture = source;
    });
    if (["unsafe", "changed", "timeout"].includes(mode)) window.Worker = class {
      postMessage() {
        if (mode !== "timeout") queueMicrotask(() => this.onmessage({data:{html: mode === "unsafe" ? '<img src=x onerror="window.__sourceXss=true">' : '<span class="token keyword">tampered</span>'}}));
      }
      terminate() {}
    };
    if (mode === "unsupported") window.Worker = undefined;
    if (mode === "storage") Object.defineProperty(window, "localStorage", {get() {throw new Error("storage denied");}});
  });
  await page.goto(selectedUrl + "&uiFixture=crlf"); await state("ready");
  check(await code().evaluate(el => el.textContent === window.__expectedFixture), "CRLF and lone CR remain exact after DOM insertion");
  for (const mode of ["nbsp", "unsafe", "changed", "timeout", "unsupported"]) {
    await page.goto(selectedUrl + "&uiFixture=" + mode); await state("plain");
    check(await code().textContent() === (mode === "nbsp" ? "public\u00a0class Example {}" : original), mode + " failure keeps exact plain source");
    check(await code().locator("img,script,iframe").count() === 0, mode + " failure inserts no executable markup");
  }
  await page.goto(selectedUrl + "&uiFixture=storage"); await state("ready");
  check(await page.locator("html").getAttribute("data-theme") === "dark", "denied storage defaults safely to dark");
  await page.getByRole("button", {name:"Dark mode", exact:true}).click();
  check(await page.locator("html").getAttribute("data-theme") === "light", "theme toggle works with denied storage");
  await page.goto(selectedUrl); await state("ready");
  await page.setViewportSize({width:1280,height:1000});
  return {passed:results.length, results};
}
