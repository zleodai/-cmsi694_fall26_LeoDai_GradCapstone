async (page) => {
  const results = [];
  const check = (ok, label) => {if (!ok) throw new Error(label); results.push(label);};
  await page.reload();
  await page.waitForFunction(() => document.querySelector("#source-code code")?.dataset.highlightState === "ready");
  const original = await page.locator("#source-code code").textContent();
  const tokens = await page.locator("#source-code .token").count();
  check(tokens > 0, "selected live C# source remains syntax highlighted");
  const dimensions = [];
  for (const width of [1280, 900, 390, 1280]) {
    await page.setViewportSize({width,height:1000});
    const measured = await page.locator("#source-code").evaluate(pre => ({
      width:pre.clientWidth, scrollWidth:pre.scrollWidth, height:pre.scrollHeight,
      whiteSpace:getComputedStyle(pre).whiteSpace, childWhiteSpace:getComputedStyle(pre.querySelector("code")).whiteSpace,
      overflowWrap:getComputedStyle(pre).overflowWrap,
      documentFits:document.documentElement.scrollWidth <= innerWidth
    }));
    check(measured.whiteSpace === "pre-wrap" && measured.childWhiteSpace === "pre-wrap" && measured.overflowWrap === "anywhere", width + "px viewer and code use responsive whitespace-preserving wrapping");
    check(measured.scrollWidth <= measured.width + 1 && measured.documentFits, width + "px code and document have no horizontal overflow");
    check(await page.locator("#source-code code").textContent() === original && await page.locator("#source-code .token").count() === tokens, width + "px resize preserves exact source text and token count");
    dimensions.push({viewport:width,...measured});
    if (width === 390 || dimensions.length === 1) {
      await page.locator("#source-code").scrollIntoViewIfNeeded();
      await page.screenshot({path:"output/playwright/wrapped-code-" + width + ".png",fullPage:true});
    }
  }
  check(dimensions[2].height > dimensions[0].height, "narrower code box reflows live source into more visual lines");
  check(dimensions[3].height === dimensions[0].height && dimensions[3].width === dimensions[0].width, "expanding the window restores the original wide wrapping without reload");
  await page.setViewportSize({width:390,height:844});
  const probe = await page.locator("#source-code code").evaluate(code => {
    const retained = [...code.childNodes].map(node => node.cloneNode(true));
    const original = code.textContent;
    const token = document.createElement("span"); token.className = "token string";
    const source = '\t    "' + "LongIdentifier".repeat(150) + '";\n    // preserved indentation\n';
    token.textContent = source; code.replaceChildren(token);
    const pre = code.closest("pre");
    const result = {fits:pre.scrollWidth <= pre.clientWidth + 1, exact:code.textContent === source, whiteSpace:getComputedStyle(token).whiteSpace};
    code.replaceChildren(...retained);
    result.restored = code.textContent === original;
    return result;
  });
  check(probe.fits && probe.whiteSpace === "pre-wrap", "synthetic long unbroken highlighted token wraps within a narrow panel");
  check(probe.exact && probe.restored, "wrapping preserves tabs/indentation/newlines and restores the live source after the page-local probe");
  await page.getByRole("button",{name:"Dark mode",exact:true}).click();
  check(await page.locator("#source-code code").textContent() === original && await page.locator("#source-code").evaluate(pre => pre.scrollWidth <= pre.clientWidth + 1), "alternate theme retains wrapping and exact code");
  await page.getByRole("button",{name:"Dark mode",exact:true}).click();
  await page.setViewportSize({width:1280,height:1000});
  return {passed:results.length,results,dimensions};
}
