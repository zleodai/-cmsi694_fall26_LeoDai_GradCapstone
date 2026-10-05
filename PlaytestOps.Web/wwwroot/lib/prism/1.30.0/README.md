# PrismJS C# assets

Vendored unchanged from the official PrismJS **v1.30.0** release, verified on 2026-10-05. MIT license is preserved in `LICENSE`. No runtime CDN, network code upload, package manager or external highlighting service is used.

Release: [PrismJS v1.30.0](https://github.com/PrismJS/prism/releases/tag/v1.30.0). Annotated tag object: `5d7a861f82f25cb3faf765a3122b0a1b08bc1ada`; release commit: `76dde18a575831c91491895193f56081ac08b0c5`.

The [official C# grammar](https://github.com/PrismJS/prism/blob/v1.30.0/components/prism-csharp.js) requires C-like, as declared in [components.json](https://github.com/PrismJS/prism/blob/v1.30.0/components.json). Only core, C-like and C# are loaded, explicitly in that order. No plugins, autodetection or automatic DOM highlighting are enabled. See the [Prism API](https://prismjs.com/docs/prism) and [token/theme documentation](https://prismjs.com/extending.html).

## Exact vendored bytes

Downloads use `https://raw.githubusercontent.com/PrismJS/prism/v1.30.0/` and these paths. Re-verify hashes when intentionally updating the pin; do not modify grammar files locally.

| Local file | Upstream path | SHA-256 |
| --- | --- | --- |
| prism-core.min.js | components/prism-core.min.js | 6caad316dd991f24f8004e0b9c19c055cb5829ff65e973fbee406f96d81b8e7e |
| prism-clike.min.js | components/prism-clike.min.js | c76ba4e240932bdc75546be30e550f5ba5e13815ff71511c76e9e27ac3072444 |
| prism-csharp.min.js | components/prism-csharp.min.js | f4eca14394e584a4a3a747fe6dc0a93ddbc657880f7dbac3f8d119ccb206107e |
| LICENSE | LICENSE | 2b947f0901a7ffcf08a89957da9783c0e9c6e72cb6ce8e959f501ab5409e4d2b |

## PlaytestOps integration and fallback

`wwwroot/js/csharp-worker.js` runs Prism's C# grammar inside a same-origin Web Worker. `csharp-highlighting.js` takes the server-rendered code's exact `textContent`; it never formats, trims, executes or uploads it. The worker accepts at most 128 KiB of UTF-8 input and 1 MiB of UTF-8 highlighted output. The UI terminates it after 2500 ms, on completion, on failure, or on page exit.

Before inserting markup, the UI accepts only text and `span` nodes with a single bounded `class="token ..."` attribute, limits the result to 10000 DOM nodes and 64 levels of nesting, and requires its decoded `textContent` to equal the original exactly. Any failure retains the original escaped plain source and displays a readable status. Prism 1.30.0 normalizes non-breaking spaces (NBSP); code containing them therefore falls back to plain text instead of changing whitespace. BOM, Unicode, indentation and displayed text must still match exactly. Highlighting is approximate syntax coloring, not a Roslyn parser or proof of compilation; newer C# constructs can be imperfectly colored.

Use `<code class="language-csharp" data-csharp-highlight data-highlight-worker=".../js/csharp-worker.js">` inside `section.source-code-panel`; put `[data-highlight-status]` beside its `pre`. Load the enhancement script and `syntax.css` only on the script viewer. Missing JavaScript, assets, Worker/TextEncoder support, timeouts, malformed output, oversized output or text mismatches all leave the readable source intact.

`syntax.css` defines separate dark and light token palettes under `:root` and `:root[data-theme="light"]`, matching the dashboard theme. Theme changes are CSS-only; they do not re-tokenize or alter text. Keyword/type/function/string/number/comment distinctions are cosmetic and do not modify source hashes, database records, or the read-only source protocol.
