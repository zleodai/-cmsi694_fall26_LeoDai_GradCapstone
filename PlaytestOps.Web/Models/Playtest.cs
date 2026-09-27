using System.ComponentModel.DataAnnotations;

namespace PlaytestOps.Web.Models;

public enum TestStatus
{
    NotStarted = 0,
    Running = 1,
    Failed = 2,
    Passed = 3
}

public sealed class Playtest
{
    public int Id { get; set; }
    public List<PlaytestRun> Runs { get; set; } = [];

    [Required, MaxLength(160)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    public TestStatus Status { get; set; } = TestStatus.NotStarted;
    public string? ProjectId { get; set; }
    public UnityProject? Project { get; set; }
    public string? UniqueName { get; set; }
    public string? FullName { get; set; }
    public string? Assembly { get; set; }
    public string? Mode { get; set; }
    public string? RunState { get; set; }
    public string? SkipReason { get; set; }
    public bool IsAvailable { get; set; } = true;
}
