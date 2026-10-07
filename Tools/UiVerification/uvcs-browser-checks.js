async (page) => {
  const results = [];
  const check = (ok, label) => { if (!ok) throw new Error(label); results.push(label); };
  const fixture = (await page.locator("main").textContent()).includes("UVCS verification fixture");
  check(await page.locator(".vcs-snapshot").getAttribute("data-vcs-state") === "available", "connected Editor supplies an available snapshot");
  check(await page.locator("#vcs-pending tbody tr").count() > 0, "pending workspace items are displayed");
  check(await page.locator("#vcs-history [data-vcs-changeset]").count() > 0, "changeset history is displayed");
  check(await page.locator("form[method=post], [contenteditable]").count() === 0, "version-control page has no write forms or editors");
  check(await page.evaluate(() => !document.querySelector('[hx-trigger*="every"]')), "version-control page has no idle polling triggers");
  check(await page.locator("#vcs-pending img, #vcs-history script").count() === 0 && await page.evaluate(() => !window.__uvcsXss), "metadata remains inert escaped text");
  for (const theme of ["dark", "light"]) {
    if (await page.locator("html").getAttribute("data-theme") !== theme) await page.getByRole("button", {name:"Dark mode",exact:true}).click();
    await page.setViewportSize({width:1280,height:1000});
    await page.screenshot({path:"output/playwright/uvcs-" + (fixture ? "fixture-" : "live-") + theme + "-desktop.png",fullPage:true});
    check(await page.locator("html").getAttribute("data-theme") === theme, theme + " theme control applies to UVCS page");
    await page.setViewportSize({width:390,height:844});
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), theme + " mobile UVCS page has no horizontal document overflow");
    check(await page.locator(".vcs-table-wrap").getAttribute("tabindex") === "0", theme + " pending scroll region is keyboard accessible");
    await page.screenshot({path:"output/playwright/uvcs-" + (fixture ? "fixture-" : "live-") + theme + "-mobile.png",fullPage:true});
  }
  if (fixture) {
    check(await page.locator("#vcs-incoming [data-vcs-changeset]").count() === 2, "synthetic positive incoming changesets are displayed separately from history");
    await page.locator('[data-vcs-page="next"]').click();
    check(await page.locator(".vcs-snapshot").getAttribute("data-vcs-offset") === "50", "history pagination advances bounded offset");
    await page.getByRole("combobox", {name:"Changeset history scope"}).selectOption("branch");
    await page.getByRole("button", {name:"View version control",exact:true}).click();
    check(await page.locator(".vcs-snapshot").getAttribute("data-vcs-scope") === "branch" && await page.locator(".vcs-snapshot").getAttribute("data-vcs-offset") === "0", "changing history scope resets pagination and queries the current branch");
  }
  if (await page.locator("html").getAttribute("data-theme") !== "dark") await page.getByRole("button", {name:"Dark mode",exact:true}).click();
  await page.setViewportSize({width:1280,height:1000});
  return {passed:results.length, fixture, results};
}
