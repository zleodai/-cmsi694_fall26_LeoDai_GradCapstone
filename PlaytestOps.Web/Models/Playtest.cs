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

    [Required, MaxLength(160)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(2000)]
    public string Description { get; set; } = string.Empty;

    public TestStatus Status { get; set; } = TestStatus.NotStarted;
}
