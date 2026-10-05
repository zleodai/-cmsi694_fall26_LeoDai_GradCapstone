"use strict";
// Dependency-free checks of the actual early theme bootstrap, not a second implementation.
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const file = path.resolve(__dirname, "../../PlaytestOps.Web/wwwroot/js/theme.js");
const source = fs.readFileSync(file, "utf8");
let checks = 0;
function check(ok, label) {
  if (!ok) throw new Error(label);
  checks++; process.stdout.write(`PASS ${label}\n`);
}
function setup(initial, blocked = false, loading = true) {
  const saved = new Map(initial === undefined ? [] : [["playtestops.theme", initial]]);
  const events = {};
  const buttonEvents = {};
  const root = { dataset: {}, style: {} };
  const label = { textContent: "" };
  const button = { hidden: true, attributes: {}, title: "",
    setAttribute: (name, value) => { button.attributes[name] = value; },
    querySelector: () => label,
    addEventListener: (name, callback) => { buttonEvents[name] = callback; }
  };
  let mounted = !loading;
  const document = { documentElement: root, readyState: loading ? "loading" : "complete",
    getElementById: () => mounted ? button : null,
    addEventListener: (name, callback) => { events[name] = callback; }
  };
  const window = { localStorage: {
    getItem(key) { if (blocked) throw new Error("storage denied"); return saved.get(key) ?? null; },
    setItem(key, value) { if (blocked) throw new Error("storage denied"); saved.set(key, value); }
  }, addEventListener: (name, callback) => { events[name] = callback; } };
  vm.runInNewContext(source, { window, document }, { filename: file, timeout: 1000 });
  return { root, label, button, saved, events, mount() { mounted = true; events.DOMContentLoaded?.(); }, click() { buttonEvents.click(); } };
}
const empty = setup();
check(empty.root.dataset.theme === "dark", "dark theme is applied before body mount");
check(empty.root.dataset.bsTheme === "dark" && empty.root.style.colorScheme === "dark", "Bootstrap and native control theme agree");
empty.mount();
check(!empty.button.hidden && empty.button.attributes["aria-pressed"] === "true", "theme control becomes available with correct dark state");
empty.click();
check(empty.root.dataset.theme === "light" && empty.root.dataset.bsTheme === "light", "toggle changes both application and Bootstrap theme");
check(empty.label.textContent === "Light" && empty.button.attributes["aria-pressed"] === "false", "visible label and accessible state track light theme");
check(empty.saved.get("playtestops.theme") === "light", "preference is persisted under scoped key");
const restored = setup(empty.saved.get("playtestops.theme"));
check(restored.root.dataset.theme === "light", "saved light theme is restored before first paint");
restored.mount(); restored.click();
check(restored.root.dataset.theme === "dark" && restored.saved.get("playtestops.theme") === "dark", "toggle returns to dark and persists it");
const denied = setup("light", true);
check(denied.root.dataset.theme === "dark", "blocked storage falls back to dark without throwing");
denied.mount(); denied.click();
check(denied.root.dataset.theme === "light", "toggle still works when storage writes are blocked");
check(setup("invalid").root.dataset.theme === "dark", "invalid saved preference fails to dark default");
check(setup("light", false, false).button.attributes["aria-pressed"] === "false", "late script initialization attaches and labels toggle correctly");
empty.saved.set("playtestops.theme", "dark"); empty.events.storage({ key: "playtestops.theme" });
check(empty.root.dataset.theme === "dark", "cross-tab preference changes are applied");
empty.saved.delete("playtestops.theme"); empty.events.storage({ key: null });
check(empty.root.dataset.theme === "dark", "clearing storage returns to dark default");
process.stdout.write(`SUCCESS: ${checks} theme checks\n`);
