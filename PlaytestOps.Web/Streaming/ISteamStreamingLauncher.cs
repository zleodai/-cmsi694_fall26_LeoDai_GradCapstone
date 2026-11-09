namespace PlaytestOps.Web.Streaming;

/// <summary>Execution-host boundary for Steam environment inspection and owned player launches.</summary>
public interface ISteamStreamingLauncher
{
    SteamEnvironmentInspection InspectEnvironments();
    Task ValidateEnvironmentsAsync(IReadOnlyList<string> sandboxNames, CancellationToken cancellationToken);
    Task<IStreamingOwnedProcess> LaunchAsync(string executablePath, string workingDirectory,
        string storagePath, string? sandboxName, IReadOnlyList<string> arguments,
        string streamConfigJson, string steamConfigJson, CancellationToken cancellationToken);
}
