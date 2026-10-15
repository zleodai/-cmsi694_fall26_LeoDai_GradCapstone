async (page) => {
  // Mutates and restores only the disposable PlaytestOps Windows Probe profile.
  const origin = 'http://localhost:5284';
  let checks = 0;
  const check = (ok, label) => { if (!ok) throw new Error(label); checks++; };
  const card = id => page.locator('[data-setting-id="' + id + '"]');
  const read = id => card(id).locator('[name="value"]').inputValue();
  const save = async (id, value) => {
    const input = card(id).locator('[name="value"]');
    if (await input.evaluate(el => el.tagName) === 'SELECT') await input.selectOption(value);
    else await input.fill(value);
    await Promise.all([page.waitForNavigation({waitUntil:'domcontentloaded'}), card(id).getByRole('button', {name:'Save',exact:true}).click()]);
    check((await page.getByRole('status').innerText()).includes('Setting saved'), id + ' saved');
    check(await read(id) === value, id + ' persisted');
  };
  const results = [];
  for (const test of [
    { id:'ProfilePlatform.m_CreateSolution', value:'true', reason:'CreateSolution' },
    { id:'ProfilePlatform.m_Architecture', value:'2', reason:'architecture' }
  ]) {
    await page.goto(origin + '/BuildSettings');
    check((await page.locator('main').innerText()).includes('PlaytestOps Windows Probe'), 'disposable profile selected');
    const original = await read(test.id);
    check(original !== test.value, 'fixture starts with runnable configuration');
    await save(test.id, test.value);
    await page.goto(origin + '/Builds');
    await Promise.all([page.waitForNavigation({waitUntil:'domcontentloaded'}),page.getByRole('button',{name:'Build Windows player',exact:true}).click()]);
    const buildId = await page.locator('#build-detail').getAttribute('data-build-id');
    await page.waitForFunction(() => ['Rejected','Failed','Succeeded','Unknown'].includes(document.querySelector('#build-state')?.textContent.trim()), {timeout:20000});
    const state = (await page.locator('#build-state').innerText()).trim();
    const message = await page.locator('#build-message').innerText();
    check(state === 'Rejected', test.id + ' rejected before output');
    check((await page.locator('#build-output').innerText()) === '', test.id + ' has no output folder');
    check(message.toLowerCase().includes(test.reason.toLowerCase()), test.id + ' actionable reason');
    results.push({buildId,state,message});
    await page.goto(origin + '/BuildSettings');
    await save(test.id, original);
  }
  return {checks,results};
}
