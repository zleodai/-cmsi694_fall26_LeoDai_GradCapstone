async (page) => {
  // Mutating acceptance checks for the disposable ExampleProject only.
  // Do not run against a real game project: representative fields are changed
  // and restored, and failed checks intentionally stop at the observed state.
  let checks = 0;
  const check = (ok, label) => { if (!ok) throw new Error(label); checks++; };
  const card = id => page.locator('[data-setting-id="' + id + '"]');
  const value = id => card(id).locator('[name="value"]').inputValue();
  const submit = async (id, next, expectSaved = true) => {
    const input = card(id).locator('[name="value"]');
    if (await input.evaluate(e => e.tagName) === 'SELECT') await input.selectOption(next);
    else await input.fill(next);
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'domcontentloaded' }),
      card(id).getByRole('button', { name: 'Save', exact: true }).click()
    ]);
    if (expectSaved) {
      check((await page.getByRole('status').textContent()).includes('Setting saved'), id + ' reports save');
      check(await value(id) === next, id + ' round trips');
    }
  };
  const refreshAfterReload = async () => {
    for (let attempt = 0; attempt < 40; attempt++) {
      await page.goto('http://localhost:5284/BuildSettings');
      if (await page.locator('[data-build-state="available"]').count() === 1) {
        check(true, 'settings recover after compilation and reconnect');
        return;
      }
      await page.waitForTimeout(500);
    }
    throw new Error('Editor failed to recover after settings-triggered script reload');
  };
  const response = await page.goto('http://localhost:5284/BuildSettings');
  check(response.ok(), 'live page returns success');
  check(response.headers()['cache-control'].includes('no-store'), 'live values are not cached');
  check(await page.locator('[data-build-state="available"]').count() === 1, 'live settings available');
  check((await page.locator('main').innerText()).includes('PlaytestOps Windows Probe'), 'correct profile displayed');
  check(await page.locator('[data-setting-id]').count() >= 140, 'broad live inventory loaded');
  check(await value('Player.bundleVersion') === '2.0.27-dashboard' || await value('Player.bundleVersion') === '2.0.27', 'profile override not global version');
  const globalScenes = await value('Scenes.Global');
  const profileScenes = await value('Profile.scenes');
  check(globalScenes.includes('+Assets/Scenes/GlobalProbe.unity') && globalScenes.includes('-Assets/Scenes/ProfileProbe.unity'), 'global scene ordering/enabled state distinct');
  check(profileScenes === '{"items":["+Assets/Scenes/ProfileProbe.unity"]}', 'effective profile scene list distinct');
  check(await value('Profile.overrideGlobalScenes') === 'true', 'profile override enabled');
  const originals = {};
  for (const id of ['Player.defaultScreenWidth', 'Player.runInBackground', 'ProfilePlatform.m_CopyPDBFiles', 'ProfilePlatform.m_CompressionType', 'Player.keepLoadedShadersAlive', 'Player.unsupportedMSAAFallback', 'Player.VertexChannelCompressionMask']) originals[id] = await value(id);
  await submit('Player.bundleVersion', '2.0.27');
  await submit('Player.defaultScreenWidth', '960');
  await submit('Player.defaultScreenWidth', originals['Player.defaultScreenWidth']);
  await submit('Player.runInBackground', originals['Player.runInBackground'] === 'true' ? 'false' : 'true');
  await submit('Player.runInBackground', originals['Player.runInBackground']);
  await submit('ProfilePlatform.m_CopyPDBFiles', originals['ProfilePlatform.m_CopyPDBFiles'] === 'true' ? 'false' : 'true');
  await submit('ProfilePlatform.m_CopyPDBFiles', originals['ProfilePlatform.m_CopyPDBFiles']);
  await submit('ProfilePlatform.m_CompressionType', '1');
  await submit('ProfilePlatform.m_CompressionType', originals['ProfilePlatform.m_CompressionType']);
  await submit('Player.keepLoadedShadersAlive', originals['Player.keepLoadedShadersAlive'] === 'true' ? 'false' : 'true');
  await submit('Player.keepLoadedShadersAlive', originals['Player.keepLoadedShadersAlive']);
  await submit('Player.unsupportedMSAAFallback', originals['Player.unsupportedMSAAFallback'] === '0' ? '1' : '0');
  await submit('Player.unsupportedMSAAFallback', originals['Player.unsupportedMSAAFallback']);
  await submit('Player.VertexChannelCompressionMask', originals['Player.VertexChannelCompressionMask']);
  await submit('Profile.scenes', '{"items":["+Assets/Scenes/ProfileProbe.unity","-Assets/Scenes/GlobalProbe.unity"]}');
  check(await value('Scenes.Global') === globalScenes, 'editing profile scenes leaves global scenes unchanged');
  await submit('Profile.scenes', profileScenes);
  const originalDefines = await value('Target.ScriptingDefineSymbols');
  await submit('Target.ScriptingDefineSymbols', 'PLAYTESTOPS_DASHBOARD_TEMP');
  await refreshAfterReload();
  await submit('Target.ScriptingDefineSymbols', originalDefines);
  await refreshAfterReload();
  await submit('Player.defaultCursor', 'Assets/Scenes/GlobalProbe.unity', false);
  check((await page.getByRole('status').textContent()).includes('value is invalid'), 'wrong asset type rejected in Unity');
  check(await value('Player.defaultCursor') === '', 'wrong asset type did not mutate cursor');
  check(await value('Profile.scenes') === profileScenes, 'profile scenes restored');
  check(await value('Scenes.Global') === globalScenes, 'global scenes preserved after edits');
  await page.getByRole('searchbox', { name: 'Find a setting' }).fill('Copy PDB');
  check(await card('ProfilePlatform.m_CopyPDBFiles').isVisible(), 'setting search keeps match');
  check(!(await card('Player.bundleVersion').isVisible()), 'setting search hides unrelated fields');
  await page.getByRole('searchbox', { name: 'Find a setting' }).fill('');
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.evaluate(() => scrollTo(0, 0));
  await page.screenshot({ path: 'output/playwright/build-settings-dark-desktop.png' });
  await page.setViewportSize({ width: 390, height: 844 });
  check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'dark narrow viewport has no horizontal overflow');
  await page.screenshot({ path: 'output/playwright/build-settings-dark-mobile.png' });
  await page.getByRole('button', { name: 'Dark mode', exact: true }).click();
  check(await page.evaluate(() => document.documentElement.dataset.theme === 'light'), 'light theme applies');
  check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'light narrow viewport has no horizontal overflow');
  await page.screenshot({ path: 'output/playwright/build-settings-light-mobile.png' });
  await page.getByRole('button', { name: 'Dark mode', exact: true }).click();
  let requests = 0;
  const count = () => requests++;
  page.on('request', count);
  await page.waitForTimeout(3500);
  page.off('request', count);
  check(requests === 0, 'idle settings page does not poll');
  console.log('LIVE BUILD SETTINGS CHECKS COMPLETE: ' + checks + ' checks passed.');
  return { checks, profile: 'PlaytestOps Windows Probe', source: 'live Unity 6000.6.0f1' };
}
