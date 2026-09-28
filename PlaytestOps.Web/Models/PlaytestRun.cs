namespace PlaytestOps.Web.Models;

// A separate record preserves previous attempts when a test is run again.
public sealed class PlaytestRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int PlaytestId { get; set; }
    public Playtest Playtest { get; set; } = null!;
    public string SessionId { get; set; } = "";
    public string State { get; set; } = "Queued";
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DispatchedAt { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public int? QueuePosition { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string Outcome { get; set; } = "";
    public string Message { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public string Output { get; set; } = "";
    public int Sequence { get; set; }
}
