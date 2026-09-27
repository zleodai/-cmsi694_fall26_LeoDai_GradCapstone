using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Pages;

public sealed record TestListViewModel(IReadOnlyList<Playtest> Tests, bool LoadFailed = false, IReadOnlySet<string>? ConnectedProjects = null, string? Notice = null, bool CanRun = false);
