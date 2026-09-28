// The test fragment polls only while queued or active attempts exist. Idle refresh is manual.
document.addEventListener("htmx:beforeRequest", function (event) {
    if (event.detail.target.id !== "test-list") return;
    document.getElementById("refresh-error").hidden = true;
    event.detail.target.setAttribute("aria-busy", "true");
});

document.addEventListener("htmx:beforeSwap", function (event) {
    // Render only our known, readable database-error fragment on a 503.
    if (event.detail.target.id === "test-list" && event.detail.xhr.status === 503 &&
        event.detail.xhr.getResponseHeader("X-PlaytestOps-Fragment") === "tests") {
        event.detail.shouldSwap = true;
        event.detail.isError = false;
    }
});

document.addEventListener("htmx:afterRequest", function (event) {
    if (event.detail.target.id !== "test-list") return;
    event.detail.target.setAttribute("aria-busy", "false");
    if (event.detail.failed) document.getElementById("refresh-error").hidden = false;
});

// Transport failures do not always set afterRequest.failed (there is no HTTP response).
for (const eventName of ["htmx:sendError", "htmx:timeout"]) {
    document.addEventListener(eventName, function (event) {
        if (event.detail.target?.id !== "test-list") return;
        document.getElementById("refresh-error").hidden = false;
        event.detail.target.setAttribute("aria-busy", "false");
    });
}
