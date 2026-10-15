"use strict";
(() => {
    const detail = document.getElementById("build-detail");
    if (!detail) return;
    const logs = document.getElementById("build-logs");
    const level = document.getElementById("build-log-level");
    let after = Number(logs.dataset.lastSequence || 0);
    const text = (id, value) => { document.getElementById(id).textContent = String(value ?? ""); };
    function filter() {
        logs.querySelectorAll(".build-log-entry").forEach(entry => {
            entry.hidden = level.value !== "all" && (level.value === "errors" ? !["Error", "Assert", "Exception"].includes(entry.dataset.level) : entry.dataset.level !== level.value);
        });
    }
    level.addEventListener("change", filter);
    async function refresh() {
        try {
            const url = new URL(window.location.href);
            url.search = ""; url.searchParams.set("handler", "Status"); url.searchParams.set("after", String(after));
            const response = await fetch(url, { cache: "no-store", credentials: "same-origin", headers: { "Accept": "application/json" } });
            if (!response.ok) throw new Error("Build refresh failed");
            const data = await response.json(); const build = data.build;
            if (!build || build.id !== detail.dataset.buildId || !Array.isArray(data.logs)) throw new Error("Unexpected build response");
            text("build-state", build.state); text("build-phase", build.phase); text("build-message", build.message);
            text("build-target", build.unityVersion + " · " + build.activeTarget); text("build-profile", build.profilePath);
            text("build-duration", Number(build.durationSeconds).toFixed(1)); text("build-counts", build.totalWarnings + " / " + build.totalErrors); text("build-bytes", build.totalBytes);
            text("build-output", build.outputPath); text("build-executable", build.executablePath); text("build-manifest", build.manifestPath);
            document.getElementById("build-unknown-actions").hidden = build.state !== "Unknown";
            const truncation = document.getElementById("build-truncation"); truncation.hidden = !build.logsTruncated; truncation.querySelector("span").textContent = String(build.droppedLogCount);
            for (const log of data.logs) {
                if (log.sequence <= after) continue;
                if (log.sequence !== after + 1) throw new Error("Build log gap");
                const pre = document.createElement("pre"); pre.className = "text-wrap border-top pt-2 build-log-entry";
                pre.dataset.level = log.level; pre.dataset.sequence = String(log.sequence);
                pre.textContent = `[${log.sequence}] ${log.timestampUtc} ${log.level}${log.truncated ? " [TRUNCATED]" : ""}\n${log.message}\n${log.stackTrace}`;
                logs.appendChild(pre); after = log.sequence;
            }
            logs.dataset.lastSequence = String(after); filter();
            if (data.hasMore) window.setTimeout(refresh, 0);
            else if (data.active) window.setTimeout(refresh, 1000);
        } catch { document.getElementById("build-refresh-error").hidden = false; }
    }
    if (detail.dataset.buildMore === "true") void refresh();
    else if (detail.dataset.buildActive === "true") window.setTimeout(refresh, 1000);
})();
