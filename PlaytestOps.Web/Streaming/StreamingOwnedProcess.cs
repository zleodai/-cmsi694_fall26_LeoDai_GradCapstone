using System.Diagnostics;

namespace PlaytestOps.Web.Streaming;

public interface IStreamingOwnedProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    StreamReader? StandardOutput { get; }
    StreamReader? StandardError { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
    void Kill();
}

/// <summary>Retains the process handle until the streaming session has finished cleanup.</summary>
public sealed class StreamingOwnedProcess : IStreamingOwnedProcess
{
    private readonly Process process;
    private readonly Action terminate;
    private readonly Action? cleanup;
    private int disposed;

    internal StreamingOwnedProcess(Process process, Action? terminate = null, Action? cleanup = null,
        StreamReader? standardOutput = null, StreamReader? standardError = null)
    {
        this.process = process;
        // Opening the handle immediately prevents a later PID reuse from changing its identity.
        _ = process.SafeHandle;
        Id = process.Id;
        this.terminate = terminate ?? (() => process.Kill(entireProcessTree: false));
        this.cleanup = cleanup;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int Id { get; }
    public bool HasExited => process.HasExited;
    public int? ExitCode => process.HasExited ? process.ExitCode : null;
    public StreamReader? StandardOutput { get; }
    public StreamReader? StandardError { get; }
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => process.WaitForExitAsync(cancellationToken);
    public void Kill() => terminate();

    public static IStreamingOwnedProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The approved player did not start.");
        try
        {
            return new StreamingOwnedProcess(process,
                standardOutput: startInfo.RedirectStandardOutput ? process.StandardOutput : null,
                standardError: startInfo.RedirectStandardError ? process.StandardError : null);
        }
        catch { process.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        process.Dispose();
        StandardOutput?.Dispose();
        StandardError?.Dispose();
        cleanup?.Invoke();
    }
}
