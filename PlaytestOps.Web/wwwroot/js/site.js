// List and selected-run fragments poll only while work is active. Idle refresh is manual.
// Shared timestamp formatting is loaded before page-specific build/streaming scripts.
(function (root) {
    "use strict";
    const options = {
        timeZone: "America/Los_Angeles", year: "numeric", month: "2-digit", day: "2-digit",
        hour: "numeric", minute: "2-digit", second: "2-digit", hour12: true, timeZoneName: "short"
    };
    const full = new Intl.DateTimeFormat("en-US", options);
    const precise = new Intl.DateTimeFormat("en-US", { ...options, fractionalSecondDigits: 3 });
    function parseUtc(value) {
        if (value instanceof Date) return Number.isFinite(value.valueOf()) ? value : null;
        if (typeof value !== "string") return null;
        const text = value.trim();
        if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?$/i.test(text)) return null;
        // Older API responses omitted Z on SQLite UTC values. Do not let the
        // browser's timezone reinterpret those values as local wall-clock time.
        const date = new Date(/(?:Z|[+-]\d{2}:\d{2})$/i.test(text) ? text : text + "Z");
        return Number.isFinite(date.valueOf()) ? date : null;
    }
    function format(value, includeMilliseconds, timeOnly) {
        const date = parseUtc(value);
        if (!date) return "—";
        const parts = Object.fromEntries((includeMilliseconds ? precise : full).formatToParts(date)
            .filter(part => part.type !== "literal").map(part => [part.type, part.value]));
        const day = timeOnly ? "" : `${parts.year}-${parts.month}-${parts.day} `;
        const fraction = includeMilliseconds ? `.${parts.fractionalSecond}` : "";
        return `${day}${parts.hour}:${parts.minute}:${parts.second}${fraction} ${parts.dayPeriod} ${parts.timeZoneName}`;
    }
    root.PlaytestOpsTime = Object.freeze({
        parseUtc,
        formatUtc: (value, includeMilliseconds = false) => format(value, includeMilliseconds, false),
        formatTimeUtc: value => format(value, false, true)
    });
})(window);

function playtestFragment(target) {
    if (target?.id === "test-list") return { name: "tests", error: document.getElementById("refresh-error") };
    if (target?.id === "run-details") return { name: "run-details", error: document.getElementById("run-refresh-error") };
    return null;
}

document.addEventListener("htmx:beforeRequest", function (event) {
    const fragment = playtestFragment(event.detail.target);
    if (!fragment) return;
    if (fragment.error) fragment.error.hidden = true;
    event.detail.target.setAttribute("aria-busy", "true");
});

document.addEventListener("htmx:beforeSwap", function (event) {
    // Render only our known, readable database-error fragment on a 503.
    const fragment = playtestFragment(event.detail.target);
    if (fragment && event.detail.xhr.status === 503 &&
        event.detail.xhr.getResponseHeader("X-PlaytestOps-Fragment") === fragment.name) {
        event.detail.shouldSwap = true;
        event.detail.isError = false;
    }
});

document.addEventListener("htmx:afterRequest", function (event) {
    const fragment = playtestFragment(event.detail.target);
    if (!fragment) return;
    event.detail.target.setAttribute("aria-busy", "false");
    if (event.detail.failed && fragment.error) fragment.error.hidden = false;
});

// Transport failures do not always set afterRequest.failed (there is no HTTP response).
for (const eventName of ["htmx:sendError", "htmx:timeout"]) {
    document.addEventListener(eventName, function (event) {
        const fragment = playtestFragment(event.detail.target);
        if (!fragment) return;
        if (fragment.error) fragment.error.hidden = false;
        event.detail.target.setAttribute("aria-busy", "false");
    });
}
