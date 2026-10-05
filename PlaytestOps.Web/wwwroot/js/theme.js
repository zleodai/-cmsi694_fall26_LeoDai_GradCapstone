// Run before styles load so a saved preference is applied before the first paint.
(function () {
    "use strict";
    const storageKey = "playtestops.theme";
    const root = document.documentElement;

    function preference() {
        try {
            return window.localStorage.getItem(storageKey) === "light" ? "light" : "dark";
        } catch {
            // Storage can be disabled by browser privacy policy; dark remains usable.
            return "dark";
        }
    }

    function apply(theme) {
        const selected = theme === "light" ? "light" : "dark";
        root.dataset.theme = selected;
        root.dataset.bsTheme = selected;
        root.style.colorScheme = selected;
        const toggle = document.getElementById("theme-toggle");
        if (!toggle) return;
        toggle.hidden = false;
        toggle.setAttribute("aria-pressed", String(selected === "dark"));
        toggle.title = selected === "dark" ? "Switch to light theme" : "Switch to dark theme";
        const label = toggle.querySelector("[data-theme-label]");
        if (label) label.textContent = selected === "dark" ? "Dark" : "Light";
    }

    apply(preference());

    function initialize() {
        apply(root.dataset.theme);
        document.getElementById("theme-toggle")?.addEventListener("click", function () {
            const selected = root.dataset.theme === "dark" ? "light" : "dark";
            apply(selected);
            try {
                window.localStorage.setItem(storageKey, selected);
            } catch {
                // The toggle still works for this page even when persistence is blocked.
            }
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initialize, { once: true });
    } else {
        initialize();
    }
    window.addEventListener("storage", function (event) {
        if (event.key === storageKey || event.key === null) apply(preference());
    });
})();
