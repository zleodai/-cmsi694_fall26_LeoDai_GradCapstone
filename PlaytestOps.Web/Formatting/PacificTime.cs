using System.Globalization;

namespace PlaytestOps.Web.Formatting;

/// <summary>Human-facing Pacific time only; persisted and exported timestamps stay UTC.</summary>
public static class PacificTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    // SQLite materializes stored UTC DateTimes with Kind=Unspecified. Never let
    // the server's own timezone reinterpret those values as local time.
    public static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime()
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static string FormatUtc(DateTime value, bool includeMilliseconds = false)
    {
        var utc = Utc(value);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, Zone);
        var format = includeMilliseconds ? "yyyy-MM-dd h:mm:ss.fff tt" : "yyyy-MM-dd h:mm:ss tt";
        return local.ToString(format, CultureInfo.InvariantCulture) + (Zone.IsDaylightSavingTime(utc) ? " PDT" : " PST");
    }

    public static string FormatUtc(string? value, bool includeMilliseconds = false) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? FormatUtc(parsed.UtcDateTime, includeMilliseconds)
            : "—";
}
