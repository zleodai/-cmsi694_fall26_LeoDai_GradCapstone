namespace PlaytestOps.Web.Models;

public sealed class UnityProject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string UnityVersion { get; set; } = "";
    public DateTimeOffset LastSyncedAt { get; set; }
}
