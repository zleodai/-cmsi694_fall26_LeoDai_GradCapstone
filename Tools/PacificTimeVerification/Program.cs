using System.Globalization;
using System.Text.Json;
using PlaytestOps.Web.Formatting;

var checks = 0;
var initialCulture = CultureInfo.CurrentCulture;
var initialUiCulture = CultureInfo.CurrentUICulture;

void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
    Console.WriteLine("PASS " + label);
}

void Equal(string actual, string expected, string label) =>
    Check(actual == expected, label + (actual == expected ? "" : $"; expected '{expected}', got '{actual}'"));

try
{
    var winter = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    var summer = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
    var unspecified = DateTime.SpecifyKind(summer, DateTimeKind.Unspecified);
    var normalized = PacificTime.Utc(unspecified);

    Check(normalized.Kind == DateTimeKind.Utc, "Unspecified persisted timestamps acquire UTC kind");
    Check(normalized.Ticks == unspecified.Ticks, "Unspecified UTC normalization never shifts clock ticks");
    Check(PacificTime.Utc(summer) == summer && PacificTime.Utc(summer).Kind == DateTimeKind.Utc,
        "already-UTC timestamps remain UTC and unchanged");
    var local = summer.ToLocalTime();
    Check(PacificTime.Utc(local).Kind == DateTimeKind.Utc && PacificTime.Utc(local) == summer,
        "Local-kind timestamps convert through their actual local offset");
    Equal(JsonSerializer.Serialize(normalized), "\"2026-07-15T12:00:00Z\"",
        "normalized UTC DateTime serializes with an explicit Z");
    Equal(PacificTime.FormatUtc(unspecified), "2026-07-15 5:00:00 AM PDT",
        "Unspecified persisted timestamp displays as UTC, not host local time");
    Equal(PacificTime.FormatUtc(local), PacificTime.FormatUtc(summer),
        "Local and UTC representations of the same instant display identically");
    Equal(PacificTime.FormatUtc(winter), "2026-01-15 4:00:00 AM PST", "winter date uses UTC-8 and PST");
    Equal(PacificTime.FormatUtc(summer), "2026-07-15 5:00:00 AM PDT", "summer date uses UTC-7 and PDT");

    var cases = new (string Utc, string Expected, string Label)[]
    {
        ("2026-07-15T03:04:05Z", "2026-07-14 8:04:05 PM PDT", "conversion rolls back to the prior Pacific date"),
        ("2026-03-08T09:59:59Z", "2026-03-08 1:59:59 AM PST", "last second before spring-forward uses PST"),
        ("2026-03-08T10:00:00Z", "2026-03-08 3:00:00 AM PDT", "spring-forward skips the nonexistent 2 AM hour"),
        ("2026-11-01T08:30:00Z", "2026-11-01 1:30:00 AM PDT", "first repeated fall-back hour retains PDT"),
        ("2026-11-01T09:30:00Z", "2026-11-01 1:30:00 AM PST", "second repeated fall-back hour is distinguished as PST"),
        ("2026-07-15T07:00:00Z", "2026-07-15 12:00:00 AM PDT", "midnight uses 12 AM rather than 0 AM"),
        ("2026-07-15T19:00:00Z", "2026-07-15 12:00:00 PM PDT", "noon uses 12 PM"),
        ("2026-01-15T04:00:00-08:00", "2026-01-15 4:00:00 AM PST", "explicit negative UTC offset is honored"),
        ("2026-07-15T14:00:00+02:00", "2026-07-15 5:00:00 AM PDT", "explicit positive UTC offset is honored"),
        ("2026-07-15T12:00:00", "2026-07-15 5:00:00 AM PDT", "legacy offset-less UTC string is not interpreted as host local time")
    };
    foreach (var item in cases) Equal(PacificTime.FormatUtc(item.Utc), item.Expected, item.Label);

    var fractional = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
    Equal(PacificTime.FormatUtc(fractional, true), "2026-07-15 5:00:00.123 AM PDT",
        "DateTime milliseconds are displayed when requested without rounding up");
    Equal(PacificTime.FormatUtc("2026-07-15T12:00:00.1234567Z", true), "2026-07-15 5:00:00.123 AM PDT",
        "string timestamps preserve requested milliseconds");
    Equal(PacificTime.FormatUtc(fractional), "2026-07-15 5:00:00 AM PDT",
        "milliseconds are omitted by default");

    foreach (var invalid in new string?[] { null, "", " ", "not-a-date" })
        Equal(PacificTime.FormatUtc(invalid), "—", "missing or invalid timestamp has a readable placeholder");

    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
    Equal(PacificTime.FormatUtc(summer), "2026-07-15 5:00:00 AM PDT",
        "display layout and AM/PM labels do not depend on host culture");
    Equal(PacificTime.FormatUtc("2026-01-15T12:00:00Z"), "2026-01-15 4:00:00 AM PST",
        "UTC string parsing remains culture independent");

    Console.WriteLine($"PACIFIC TIME VERIFICATION PASSED: {checks} checks.");
}
catch (Exception error)
{
    Console.Error.WriteLine("PACIFIC TIME VERIFICATION FAILED: " + error.Message);
    Environment.ExitCode = 1;
}
finally
{
    CultureInfo.CurrentCulture = initialCulture;
    CultureInfo.CurrentUICulture = initialUiCulture;
}
