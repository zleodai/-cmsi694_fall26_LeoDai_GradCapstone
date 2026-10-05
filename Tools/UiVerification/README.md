# Theme and C# highlighting verification

Run from the repository root with Node.js available. The Node checks use built-in modules and the actual checked-in scripts/vendor files; no npm dependencies are needed:

```powershell
node Tools/UiVerification/theme-checks.cjs
node Tools/UiVerification/highlighting-checks.cjs
```

Expected results at the 2026-10-05 checkpoint: **14 theme checks** and **29 Worker checks**. The latter accepts an optional absolute repository/staging-root argument. Theme checks cover early default/restored preferences, accessible labels, Bootstrap/native theme synchronization, storage denial, and cross-tab changes. Worker checks verify pinned Prism 1.30.0 hashes, explicit C# tokens, escaped HTML-like source, Unicode/BOM/CRLF handling, input/output limits, NBSP mismatch detection, and both palette contrast ratios. These mocked contexts do not prove browser layout, DOM insertion, or paint behavior.

## Isolated browser checks

Use the separate [SourceVerification preview](../SourceVerification/README.md): build the web app into a disposable directory, then run its verifier with `--preview`. It creates a synthetic Editor fixture and disposable database and prints a preview URL. Keep it running in another terminal. Do not use the live service, real Unity project, or working database, and do not rebuild its output while the fixture server is running on Windows.

Check `node --version`, `npm --version`, and `npx --version`. Use a **fresh, non-persistent Playwright CLI session** so stored preferences and injected failure fixtures cannot affect an existing browser session. From the repository root:

```powershell
$uiCheckSession = "ui-checks-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
New-Item -ItemType Directory -Path output/playwright -Force
npx --yes --package @playwright/cli playwright-cli --session $uiCheckSession open "<printed selected-script preview URL>"
npx --yes --package @playwright/cli playwright-cli --session $uiCheckSession snapshot
npx --yes --package @playwright/cli playwright-cli --session $uiCheckSession run-code --filename Tools/UiVerification/browser-checks.js
npx --yes --package @playwright/cli playwright-cli --session $uiCheckSession close
```

Select the fixture script first if the printed URL is only a project list. The selected URL must be `/Scripts?...&path=...`. The `run-code --filename` file is the locally saved `browser-checks.js`, not inline JavaScript. Run it **only against this isolated SourceVerification preview**: it deliberately changes page-local Worker/storage behavior and injects source-text failure fixtures. Close the CLI session even after a failure, then stop the preview tool to release its port. None of these checks imports scripts into Unity or executes Unity tests.

Expected result: **25 browser checks**. Coverage includes safe DOM insertion, exact pre/post-enhancement DOM text, Unicode/HTML-like comments, CRLF/lone CR, NBSP plain fallback, rejected executable markup, changed text, Worker timeout/unavailability, blocked storage, mouse/keyboard theme switching and persistence, computed token contrast, desktop screenshots, and 390px mobile overflow/responsive code wrapping. The wrapping assertion replaces the original horizontal code-scroll expectation. Screenshots are written to `output/playwright/`. Contrast minima at the checkpoint were **6.70:1 dark** and **4.76:1 light**.

The HTML source parser can normalize line endings before enhancement; exact-text checks compare the original and highlighted **DOM text**, not downloaded file bytes. Plain fallback is a valid safety result, not a failed source read. Run the separate SourceVerification (**104 checks**) and ConnectionVerification (**126 checks**) harnesses for backend/protocol regressions. The web build also passed with zero warnings/errors. These are staged/isolated validation results, not live-deployment, gameplay, physical-phone, or ML-agent qualification evidence.

## Live UI checkpoint

`live-theme-checks.js` is a separate read-only dashboard checkpoint for `http://localhost:5282`. It performs GETs and changes only its test browser's theme preference; it does not queue runs or inject failure fixtures. Its 2026-10-05 assumptions are the connected PlaytestOps Test project with exactly two catalog rows, empty default author folders, and saved run `b3522a424ce34e539712213e9eff1e2b`. Those assumptions are intentionally explicit and may need updating for a later project state. Run with the same CLI `run-code --filename` mechanism in a separate fresh session opened to the live root. Twenty checks passed: both-theme navigation/persistence, catalog/connection retention, empty source state, historical severity logs, badge contrast, mobile overflow and local asset serving. Populated live Unity source, test execution and physical-phone behavior are not covered by this checkpoint.

## Responsive wrapping checkpoint

`wrapping-checks.js` uses a separate test browser already opened to a selected, successfully highlighted script. Run it with the CLI `run-code --filename` mechanism. The 2026-10-05 checkpoint used PlaytestOps Test's `Assets/Scripts/Services/Effects/BigHitEffect.cs`; the file must have enough long lines to demonstrate increased visual height at 390px. Eighteen checks passed at 1280/900/390px and back, covering live reflow, no horizontal overflow, identical source text and token counts, and alternate-theme wrapping. A synthetic long unbroken token with tabs/indentation is temporarily inserted only into the test browser's DOM and then restored; no source file or database is written and no tests are queued. This checkpoint reflects the later populated-source state, unlike the earlier empty-folder live-theme checkpoint. Screenshots are saved in `output/playwright/`.
