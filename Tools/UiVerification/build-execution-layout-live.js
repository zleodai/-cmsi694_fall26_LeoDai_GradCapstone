async (page) => {
  let checks = 0;
  const check = (ok, label) => { if (!ok) throw new Error(label); checks++; };
  const pages = ['/Builds/1bfece69e14542c7b2177d35d3f4f702', '/Builds'];
  const result = [];
  for (const path of pages) {
    await page.goto('http://localhost:5284' + path);
    check((await page.locator('main').innerText()).includes('PlaytestOps Build Probe'), 'correct fixture identity');
    for (const theme of ['dark','light']) {
      if (await page.evaluate(() => document.documentElement.dataset.theme) !== theme) await page.getByRole('button',{name:'Dark mode',exact:true}).click();
      check(await page.evaluate(() => document.documentElement.dataset.theme) === theme, theme + ' theme applies');
      for (const width of [390,1440]) {
        await page.setViewportSize({width,height:width===390?844:1000});
        check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), path+' '+theme+' '+width+' no overflow');
        result.push({path,theme,width});
      }
    }
  }
  if (await page.evaluate(() => document.documentElement.dataset.theme) !== 'dark') await page.getByRole('button',{name:'Dark mode',exact:true}).click();
  return {checks,result};
}
