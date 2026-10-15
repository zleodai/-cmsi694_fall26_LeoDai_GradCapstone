"use strict";
(() => {
    const filter = document.getElementById("build-settings-filter");
    if (!filter) return;
    filter.addEventListener("input", () => {
        const query = filter.value.toLocaleLowerCase().trim();
        document.querySelectorAll(".build-settings-category").forEach(category => {
            let visible = false;
            category.querySelectorAll(".build-settings-field").forEach(field => {
                field.hidden = !field.textContent.toLocaleLowerCase().includes(query);
                visible ||= !field.hidden;
            });
            category.hidden = !visible;
            if (query && visible) category.open = true;
        });
    });
    document.querySelectorAll(".build-settings-field form").forEach(form => {
        form.addEventListener("submit", () => {
            const button = form.querySelector("button[type=submit]");
            if (button) { button.disabled = true; button.textContent = "Saving…"; }
        });
    });
})();
