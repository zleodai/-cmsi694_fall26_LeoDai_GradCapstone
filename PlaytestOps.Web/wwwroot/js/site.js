// List and selected-run fragments poll only while work is active. Idle refresh is manual.
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
