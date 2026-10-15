using System.Text.Json.Serialization;

namespace PlaytestOps.Web.Models;

public sealed class UnityBuild
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RequestKey { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string SessionId { get; set; } = "";
    [JsonIgnore] public string TokenHash { get; set; } = "";
    public string Revision { get; set; } = "";
    public string State { get; set; } = "Pending";
    public string Phase { get; set; } = "Dispatching";
    public string Message { get; set; } = "";
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdateAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string UnityVersion { get; set; } = "";
    public string ActiveTarget { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public double DurationSeconds { get; set; }
    public long TotalBytes { get; set; }
    public int TotalWarnings { get; set; }
    public int TotalErrors { get; set; }
    public int Sequence { get; set; }
    public int LastLogSequence { get; set; }
    public int TotalLogCharacters { get; set; }
    public bool LogsTruncated { get; set; }
    public int DroppedLogCount { get; set; }
}

public sealed class UnityBuildLog
{
    public string BuildId { get; set; } = "";
    public int Sequence { get; set; }
    public string TimestampUtc { get; set; } = "";
    public string Level { get; set; } = "Debug";
    public string Message { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public bool Truncated { get; set; }
}
