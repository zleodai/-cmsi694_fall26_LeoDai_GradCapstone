"use strict";

// Dependency-free checks for the actual vendored assets and worker implementation.
// Run: node Tools/UiVerification/highlighting-checks.cjs [absolute-staging-root]
// DOM insertion, fallback timing and theme switching still require browser checks.
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const crypto = require("node:crypto");
const stage = process.argv[2] ? path.resolve(process.argv[2]) : path.resolve(__dirname, "../..");
const assets = path.join(stage, "PlaytestOps.Web/wwwroot/lib/prism/1.30.0");
const workerDirectory = path.join(stage, "PlaytestOps.Web/wwwroot/js");
let checks = 0;
function check(ok, message) {
    if (!ok) throw new Error(message);
    checks++;
    console.log("PASS " + message);
}

const pins = [
    ["prism-core.min.js", "6caad316dd991f24f8004e0b9c19c055cb5829ff65e973fbee406f96d81b8e7e"],
    ["prism-clike.min.js", "c76ba4e240932bdc75546be30e550f5ba5e13815ff71511c76e9e27ac3072444"],
    ["prism-csharp.min.js", "f4eca14394e584a4a3a747fe6dc0a93ddbc657880f7dbac3f8d119ccb206107e"],
    ["LICENSE", "2b947f0901a7ffcf08a89957da9783c0e9c6e72cb6ce8e959f501ab5409e4d2b"]
];
for (const [name, hash] of pins) {
    check(crypto.createHash("sha256").update(fs.readFileSync(path.join(assets, name))).digest("hex") === hash,
        "pinned vendor hash " + name);
}
for (const filename of ["csharp-worker.js", "csharp-highlighting.js"]) {
    new vm.Script(fs.readFileSync(path.join(workerDirectory, filename), "utf8"), { filename });
    check(true, "JavaScript syntax " + filename);
}

// Reproduce WorkerGlobalScope and same-origin importScripts without browser APIs.
// The real worker source, not a duplicated highlighter, handles every request.
let reply;
class WorkerGlobalScope {}
const scope = new WorkerGlobalScope();
scope.WorkerGlobalScope = WorkerGlobalScope;
scope.self = scope;
scope.TextEncoder = TextEncoder;
scope.postMessage = value => { reply = value; };
vm.createContext(scope);
scope.importScripts = (...files) => files.forEach(file => {
    vm.runInContext(fs.readFileSync(path.resolve(workerDirectory, file), "utf8"), scope, { timeout: 1000 });
});
vm.runInContext(fs.readFileSync(path.join(workerDirectory, "csharp-worker.js"), "utf8"), scope, { timeout: 1000 });
function request(source) {
    reply = undefined;
    scope.testSource = source;
    vm.runInContext("self.onmessage({data:{source:testSource}})", scope, { timeout: 2500 });
    return reply;
}
// Prism core only emits escaped text and token spans. This decoder is for checking
// its output text in Node; the browser uses a stricter inert DOM/span validator.
function decode(html) {
    // HTML parsing normalizes literal carriage returns before decoding entities.
    // Decode ampersands last so source text such as "&#13;" stays literal.
    return html.replace(/\r\n?/g, "\n").replace(/<\/?span\b[^>]*>/g, "")
        .replace(/&#13;/g, "\r").replace(/&lt;/g, "<").replace(/&amp;/g, "&");
}
const source = [
    "\uFEFFusing UnityEngine;",
    "// café 日本語 😀 <img src=x onerror=alert(1)> &amp;",
    "[SerializeField] private int count = 42;",
    "public class Test : MonoBehaviour {",
    "  public string Value() => $\"value {count}\";",
    "}"
].join("\n");
let result = request(source);
check(typeof result.html === "string", "worker loads official CSharp grammar");
for (const token of ["keyword", "comment", "number", "string", "function", "class-name"]) {
    check(result.html.includes("token " + token), "CSharp token " + token);
}
check(!result.html.includes("<img"), "code-like HTML is escaped by official grammar");
check(decode(result.html) === source, "highlighted text preserves BOM Unicode and indentation");
const crlfSource = "\uFEFFusing UnityEngine;\r\n// café 日本語 😀\r\npublic class Test {}\r\n";
result = request(crlfSource);
check(typeof result.html === "string" && decode(result.html) === crlfSource,
    "CRLF survives HTML parsing without source-text changes");
const crSource = "// lone CR\rpublic class Test {}\r// literal &#13;";
result = request(crSource);
check(typeof result.html === "string" && decode(result.html) === crSource,
    "lone CR and literal entity-like source survive HTML parsing");
check(!result.html.includes("\r") && result.html.includes("&#13;"),
    "worker encodes every carriage return before inert HTML parsing");
const nbspSource = "\u00A0public class Test {}";
result = request(nbspSource);
check(decode(result.html) !== nbspSource, "NBSP mismatch detectable for plain fallback");
check(request("x".repeat(128 * 1024 + 1)).error === "unavailable", "worker refuses oversized input");
check(request("日".repeat(45000)).error === "unavailable", "worker limits UTF8 bytes rather than characters");
check(typeof request("x".repeat(128 * 1024)).html === "string", "worker accepts exact 128KiB plain-input boundary");
check(request(";".repeat(30000)).error === "output-limit", "worker bounds token-expanded highlighted output");
const carriageReturnExpansion = ";".repeat(18000) + "\r".repeat(80000);
const rawExpansion = scope.Prism.highlight(carriageReturnExpansion, scope.Prism.languages.csharp, "csharp");
check(Buffer.byteLength(rawExpansion, "utf8") <= 1024 * 1024 &&
    Buffer.byteLength(rawExpansion.replace(/\r/g, "&#13;"), "utf8") > 1024 * 1024 &&
    request(carriageReturnExpansion).error === "output-limit",
    "worker bounds output after carriage-return entity expansion");
check(request(null).error === "unavailable", "worker rejects non-text requests");

function luminance(hex) {
    const rgb = hex.match(/[0-9a-f]{2}/gi).map(channel => parseInt(channel, 16) / 255)
        .map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
    return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
}
// Read the actual stylesheet so changes cannot silently diverge from the check.
const css = fs.readFileSync(path.join(stage, "PlaytestOps.Web/wwwroot/css/syntax.css"), "utf8");
const dark = css.match(/:root\s*\{([^}]+)\}/)[1];
const light = css.match(/:root\[data-theme="light"\]\s*\{([^}]+)\}/)[1];
for (const [theme, background, declarations] of [["dark", "111a24", dark], ["light", "f7f9fc", light]]) {
    const colors = [...declarations.matchAll(/--syntax-[a-z-]+:\s*#([0-9a-f]{6})/gi)].map(match => match[1]);
    check(colors.length === 8, theme + " defines every token palette category");
    const bg = luminance(background);
    const ratios = colors.map(color => {
        const value = luminance(color);
        return (Math.max(value, bg) + 0.05) / (Math.min(value, bg) + 0.05);
    });
    check(ratios.every(ratio => ratio >= 4.5),
        theme + " token colors at least 4.5:1 contrast (minimum " + Math.min(...ratios).toFixed(2) + ":1)");
}
console.log("HIGHLIGHT WORKER CHECKS COMPLETE: " + checks + " checks passed.");
