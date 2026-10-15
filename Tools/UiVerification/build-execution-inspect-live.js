async (page) => {
  const state = (await page.locator('#build-state').innerText()).trim();
  const proof = await page.evaluate(() => window.playtestopsBuildProof);
  const logs = await page.locator('#build-logs').innerText();
  const levels = await page.locator('.build-log-entry').evaluateAll(entries => [...new Set(entries.map(entry => entry.dataset.level))]);
  const result = {
    buildId: await page.locator('#build-detail').getAttribute('data-build-id'), state, proof,
    output: await page.locator('#build-output').innerText(),
    executable: await page.locator('#build-executable').innerText(),
    manifest: await page.locator('#build-manifest').innerText(),
    counts: await page.locator('#build-counts').innerText(), levels,
    fixtureDebug: logs.includes('PLAYTESTOPS_BUILD_FIXTURE'),
    fixtureWarning: logs.includes('PLAYTESTOPS_BUILD_EXPECTED_WARNING'),
    fixtureFailure: logs.includes('PLAYTESTOPS_BUILD_EXPECTED_FAILURE'),
    logCount: await page.locator('.build-log-entry').count()
  };
  if (!['Succeeded', 'Failed', 'Rejected', 'Unknown'].includes(state)) return result;
  const download = await page.request.get(page.url() + '?handler=LogFile');
  const downloaded = await download.text();
  if (!download.ok() || !downloaded.includes('PLAYTESTOPS')) throw new Error('Full captured log download missing fixture output');
  result.downloadBytes = downloaded.length;
  let requests = 0;
  const listener = request => { if (request.url().includes('handler=Status')) requests++; };
  page.on('request', listener);
  await page.waitForTimeout(2200);
  page.off('request', listener);
  if (requests !== 0) throw new Error('Terminal page kept polling');
  result.terminalIdleRequests = requests;
  for (const value of ['Warning', 'errors', 'Debug', 'all']) {
    await page.locator('#build-log-level').selectOption(value);
    const visible = await page.locator('.build-log-entry').evaluateAll(entries => entries.filter(entry => !entry.hidden).map(entry => entry.dataset.level));
    if (value === 'Warning' && visible.some(level => level !== 'Warning')) throw new Error('Warning filter leaked another level');
    if (value === 'Debug' && visible.some(level => level !== 'Debug')) throw new Error('Debug filter leaked another level');
    if (value === 'errors' && visible.some(level => !['Error', 'Assert', 'Exception'].includes(level))) throw new Error('Error filter leaked another level');
  }
  return result;
}
