async (page) => {
  await page.goto('http://localhost:5284/Builds');
  const text = await page.locator('main').innerText();
  if (!text.includes('PlaytestOps Windows Probe') || !text.includes('PlaytestOps Build Probe')) throw new Error('Disposable example identity missing');
  await Promise.all([
    page.waitForNavigation({ waitUntil: 'domcontentloaded' }),
    page.getByRole('button', { name: 'Build Windows player', exact: true }).click()
  ]);
  const match = page.url().match(/\/Builds\/([a-f0-9]{32})$/);
  if (!match) throw new Error('Build request did not open a unique build record: ' + page.url());
  await page.evaluate(() => {
    const proof = { samples: [], liveMarkerBeforeTerminal: false, terminal: '' };
    const record = () => {
      const state = document.querySelector('#build-state')?.textContent.trim() || '';
      const phase = document.querySelector('#build-phase')?.textContent.trim() || '';
      const count = document.querySelectorAll('.build-log-entry').length;
      const marker = (document.querySelector('#build-logs')?.textContent || '').includes('PLAYTESTOPS_BUILD_LIVE_STEP');
      if (['Pending', 'Running'].includes(state) && marker) proof.liveMarkerBeforeTerminal = true;
      if (['Succeeded', 'Failed', 'Rejected', 'Unknown'].includes(state)) proof.terminal = state;
      const last = proof.samples[proof.samples.length - 1];
      if (!last || last.state !== state || last.phase !== phase || last.count !== count) proof.samples.push({ state, phase, count, time: Date.now() });
    };
    new MutationObserver(record).observe(document.querySelector('main'), { childList: true, subtree: true, characterData: true });
    window.playtestopsBuildProof = proof;
    record();
  });
  return { buildId: match[1], state: await page.locator('#build-state').innerText(), url: page.url() };
}
