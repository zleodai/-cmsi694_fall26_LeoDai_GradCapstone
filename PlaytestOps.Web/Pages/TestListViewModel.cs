using PlaytestOps.Web.Models;

namespace PlaytestOps.Web.Pages;

public sealed record TestListViewModel(IReadOnlyList<Playtest> Tests, bool LoadFailed = false);
