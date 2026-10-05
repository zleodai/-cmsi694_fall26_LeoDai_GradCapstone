using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Pages;

public sealed record RunDetailsViewModel(PlaytestRun? Run, IReadOnlyList<RunLogEntry> Logs,
    string Level = "all", bool LoadFailed = false)
{
    public IReadOnlyList<RunLogEntry> AllLogs { get; } = Run?.Logs ?? [];
    public bool IsActive => Run?.State is "Queued" or "Pending" or "Running";
    public int DebugCount => AllLogs.Count(log => log.Level == "Debug");
    public int WarningCount => AllLogs.Count(log => log.Level == "Warning");
    public int ErrorCount => AllLogs.Count(log => log.Level is "Error" or "Assert" or "Exception");
}
