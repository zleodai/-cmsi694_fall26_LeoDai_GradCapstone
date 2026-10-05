/* C# grammar execution stays off the UI thread. All dependencies are local and pinned. */
"use strict";
self.Prism = { manual: true, disableWorkerMessageHandler: true };
let grammarReady = false;
try {
    importScripts(
        "../lib/prism/1.30.0/prism-core.min.js",
        "../lib/prism/1.30.0/prism-clike.min.js",
        "../lib/prism/1.30.0/prism-csharp.min.js"
    );
    grammarReady = !!self.Prism.languages.csharp;
} catch {
    // Missing assets leave the server-rendered plain source intact.
}

self.onmessage = function (event) {
    const source = event.data && event.data.source;
    try {
        if (!grammarReady || typeof source !== "string" || source.length > 128 * 1024 ||
            new TextEncoder().encode(source).length > 128 * 1024) {
            self.postMessage({ error: "unavailable" });
            return;
        }
        // HTML parsing normalizes literal CR/CRLF. Numeric entities preserve the
        // original carriage returns when the inert fragment becomes textContent.
        const html = self.Prism.highlight(source, self.Prism.languages.csharp, "csharp")
            .replace(/\r/g, "&#13;");
        if (html.length > 1024 * 1024 || new TextEncoder().encode(html).length > 1024 * 1024) {
            self.postMessage({ error: "output-limit" });
            return;
        }
        // Prism escapes code; the main thread additionally accepts token spans only
        // and requires exact textContent equality before replacing any source text.
        self.postMessage({ html: html });
    } catch {
        self.postMessage({ error: "unavailable" });
    }
};
