/* Read-only progressive enhancement; no source uploads, formatting or autodetection. */
(function () {
    "use strict";
    const MAX_INPUT_BYTES = 128 * 1024;
    const MAX_OUTPUT_BYTES = 1024 * 1024;
    const MAX_DOM_NODES = 10000;
    const WORKER_TIMEOUT_MS = 2500;
    const cleanup = new Set();
    const plainNotice = "Syntax colors unavailable; the exact source is still shown as plain text.";

    function setStatus(code, message) {
        const pre = code.closest("pre");
        const status = pre && pre.parentElement && pre.parentElement.querySelector("[data-highlight-status]");
        if (status) status.textContent = message;
    }

    function validatedMarkup(html, source) {
        if (typeof html !== "string" || html.length > MAX_OUTPUT_BYTES ||
            new TextEncoder().encode(html).length > MAX_OUTPUT_BYTES) return null;
        const template = document.createElement("template");
        // Template contents are inert, and no result is inserted into the page until
        // every node and the decoded source text have passed the checks below.
        template.innerHTML = html;
        const walker = document.createTreeWalker(template.content, NodeFilter.SHOW_ALL);
        let nodes = 0;
        let node;
        while ((node = walker.nextNode())) {
            if (++nodes > MAX_DOM_NODES) return null;
            if (node.nodeType === Node.TEXT_NODE) continue;
            if (node.nodeType !== Node.ELEMENT_NODE || node.tagName !== "SPAN" ||
                node.attributes.length !== 1 || !node.hasAttribute("class")) return null;
            const classes = node.getAttribute("class");
            if (!classes || classes.length > 128 || !/^token(?: [a-z][a-z0-9-]*)+$/.test(classes)) return null;
            let depth = 0;
            for (let parent = node.parentNode; parent && parent !== template.content; parent = parent.parentNode)
                if (++depth > 64) return null;
        }
        // In particular, Prism 1.30 normalizes NBSP; those files stay plain rather
        // than silently altering displayed/copied source, BOMs or whitespace.
        return template.content.textContent === source ? template.content : null;
    }

    function highlight(code) {
        if (code.dataset.highlightState) return;
        const source = code.textContent;
        code.dataset.highlightState = "plain";
        if (!window.Worker || !window.TextEncoder || source.length > MAX_INPUT_BYTES ||
            new TextEncoder().encode(source).length > MAX_INPUT_BYTES) {
            setStatus(code, plainNotice);
            return;
        }
        let worker;
        let timer;
        let finished = false;
        function finish(message) {
            if (finished) return;
            finished = true;
            window.clearTimeout(timer);
            if (worker) worker.terminate();
            cleanup.delete(stop);
            setStatus(code, message);
        }
        function stop() { finish(plainNotice); }
        try {
            const url = new URL(code.dataset.highlightWorker, window.location.href);
            if (url.origin !== window.location.origin || !/^https?:$/.test(url.protocol)) {
                finish(plainNotice);
                return;
            }
            worker = new Worker(url.href);
            cleanup.add(stop);
            timer = window.setTimeout(stop, WORKER_TIMEOUT_MS);
            setStatus(code, "Preparing C# syntax colors; source remains available.");
            worker.onerror = function (event) { event.preventDefault(); stop(); };
            worker.onmessageerror = stop;
            worker.onmessage = function (event) {
                if (finished) return;
                try {
                    const fragment = validatedMarkup(event.data && event.data.html, source);
                    if (!fragment || !code.isConnected || code.textContent !== source) { stop(); return; }
                    code.replaceChildren(fragment);
                    code.dataset.highlightState = "ready";
                    finish("C# syntax colors enabled; source text is unchanged.");
                } catch { stop(); }
            };
            worker.postMessage({ source: source });
        } catch { stop(); }
    }

    function initialize() {
        // A script page currently displays one selected file. Keep enhancement
        // bounded if another view adds multiple code panels later.
        document.querySelectorAll("code[data-csharp-highlight]").forEach(function (code, index) {
            if (index < 4) highlight(code);
        });
    }
    window.addEventListener("pagehide", function () { cleanup.forEach(function (stop) { stop(); }); });
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", initialize, { once: true });
    else initialize();
})();
