"use strict";

// Dependency-free checks against the checked-in Pacific-time browser helper.
// Run from the repository root: node Tools/UiVerification/pacific-time-checks.cjs
// An optional first argument selects another repository/staging root.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { spawnSync } = require("node:child_process");

const child = process.argv[2] === "--child";
const root = path.resolve(child ? process.argv[4] : (process.argv[2] || path.resolve(__dirname, "../..")));

if (!child) {
    let total = 0;
    for (const zone of ["UTC", "Asia/Tokyo", "America/Los_Angeles"]) {
        const result = spawnSync(process.execPath, [__filename, "--child", zone, root], {
            env: { ...process.env, TZ: zone, LANG: "fr_FR.UTF-8" },
            encoding: "utf8",
            maxBuffer: 1024 * 1024,
            timeout: 15000
        });
        process.stdout.write(result.stdout || "");
        process.stderr.write(result.stderr || "");
        if (result.error) throw result.error;
        if (result.status !== 0) throw new Error(`Pacific-time checks failed in host zone ${zone}`);
        const count = /ZONE PASSED: (\d+) checks/.exec(result.stdout || "");
        assert.ok(count, `A completed assertion count is required for ${zone}`);
        total += Number(count[1]);
    }
    console.log(`PACIFIC BROWSER TIME VERIFICATION PASSED: ${total} checks across 3 host time zones.`);
} else {
    const zone = process.argv[3];
    const filename = path.join(root, "PlaytestOps.Web/wwwroot/js/site.js");
    const context = vm.createContext({ window: {}, document: { addEventListener() {} }, Intl, Date, console });
    vm.runInContext(fs.readFileSync(filename, "utf8"), context, { filename, timeout: 1000 });
    const time = context.window.PlaytestOpsTime;
    let checks = 0;
    function check(condition, label) {
        assert.ok(condition, `${zone}: ${label}`);
        checks++;
        console.log(`PASS [${zone}] ${label}`);
    }
    function equal(actual, expected, label) {
        assert.equal(actual, expected, `${zone}: ${label}`);
        checks++;
        console.log(`PASS [${zone}] ${label}`);
    }
    check(time && typeof time.parseUtc === "function", "parseUtc is exposed through PlaytestOpsTime");
    check(time && typeof time.formatUtc === "function", "formatUtc is exposed through PlaytestOpsTime");
    check(time && typeof time.formatTimeUtc === "function", "formatTimeUtc is exposed through PlaytestOpsTime");

    for (const [value, expected, label] of [
        ["2026-07-15T12:00:00Z", "2026-07-15T12:00:00.000Z", "explicit Z denotes the same UTC instant"],
        ["2026-07-15T12:00:00", "2026-07-15T12:00:00.000Z", "legacy offset-less UTC timestamp never uses the host zone"],
        ["2026-07-15T12:00:00.1234567", "2026-07-15T12:00:00.123Z", "offset-less .NET fractional timestamps preserve milliseconds"],
        ["2026-07-15T14:00:00+02:00", "2026-07-15T12:00:00.000Z", "positive explicit offset is honored"],
        ["2026-01-15T04:00:00-08:00", "2026-01-15T12:00:00.000Z", "negative explicit offset is honored"]
    ]) {
        const parsed = time.parseUtc(value);
        check(parsed !== null && Number.isFinite(parsed.valueOf()), label + " parses as a valid Date");
        equal(parsed.toISOString(), expected, label);
    }

    for (const [value, expected, label] of [
        ["2026-01-15T12:00:00Z", "2026-01-15 4:00:00 AM PST", "winter date uses PST"],
        ["2026-07-15T12:00:00Z", "2026-07-15 5:00:00 AM PDT", "summer date uses PDT"],
        ["2026-07-15T03:04:05Z", "2026-07-14 8:04:05 PM PDT", "conversion retains the previous Pacific calendar date"],
        ["2026-03-08T09:59:59Z", "2026-03-08 1:59:59 AM PST", "spring-forward predecessor remains PST"],
        ["2026-03-08T10:00:00Z", "2026-03-08 3:00:00 AM PDT", "spring-forward skips the nonexistent local hour"],
        ["2026-11-01T08:30:00Z", "2026-11-01 1:30:00 AM PDT", "first repeated fall hour is labeled PDT"],
        ["2026-11-01T09:30:00Z", "2026-11-01 1:30:00 AM PST", "second repeated fall hour is labeled PST"],
        ["2026-07-15T07:00:00Z", "2026-07-15 12:00:00 AM PDT", "midnight is 12 AM"],
        ["2026-07-15T19:00:00Z", "2026-07-15 12:00:00 PM PDT", "noon is 12 PM"],
        ["2026-07-15T14:00:00+02:00", "2026-07-15 5:00:00 AM PDT", "formatted positive offset retains its instant"],
        ["2026-01-15T04:00:00-08:00", "2026-01-15 4:00:00 AM PST", "formatted negative offset retains its instant"],
        ["2026-07-15T12:00:00", "2026-07-15 5:00:00 AM PDT", "legacy UTC timestamp formats correctly in every host zone"]
    ]) equal(time.formatUtc(value), expected, label);

    equal(time.formatUtc("2026-07-15T12:00:00.1234567Z", true), "2026-07-15 5:00:00.123 AM PDT",
        "requested milliseconds use three digits without rounding");
    equal(time.formatUtc("2026-07-15T12:00:00.1234567Z"), "2026-07-15 5:00:00 AM PDT",
        "milliseconds are omitted by default");
    equal(time.formatUtc("2026-01-15T12:00:00Z", true), "2026-01-15 4:00:00.000 AM PST",
        "whole-second timestamps display .000 when milliseconds are requested");
    equal(time.formatTimeUtc("2026-07-15T12:00:00Z"), "5:00:00 AM PDT",
        "time-only formatting still identifies daylight time");
    equal(time.formatTimeUtc("2026-01-15T12:00:00Z"), "4:00:00 AM PST",
        "time-only formatting still identifies standard time");

    for (const value of [null, undefined, "", " ", "not-a-date"]) {
        equal(time.parseUtc(value), null, "invalid or missing timestamp is not parsed");
        equal(time.formatUtc(value), "—", "invalid or missing full timestamp has a readable placeholder");
        equal(time.formatTimeUtc(value), "—", "invalid or missing time has a readable placeholder");
    }
    console.log(`ZONE PASSED: ${checks} checks (${zone}).`);
}
