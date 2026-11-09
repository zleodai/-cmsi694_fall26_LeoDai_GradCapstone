using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlaytestOps.Web.Data;
using PlaytestOps.Web.Models;
using PlaytestOps.Web.Streaming;

namespace PlaytestOps.Web.Bridge;

public sealed record BuildDeletionPreview(UnityBuild Build, int LogCount, string? BlockedReason, bool CanDeleteFiles, string? FileError);
public sealed record BuildDeletionResult(bool Deleted, string? Error, int StatusCode = 409);

// User-facing removal of a saved build, never a Unity cancellation command.
public sealed class BuildDeletionService(IServiceScopeFactory scopes, BridgeRegistry registry,
    StreamingSupervisor streaming, ILogger<BuildDeletionService> logger)
{
    public static bool IsTerminal(string state) => state is "Succeeded" or "Failed" or "Rejected" or "Interrupted";

    public static bool IsLocalDeleteRequest(HttpContext context)
    {
        if (!BridgeRegistry.IsLocalOperator(context) ||
            string.Equals(context.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase)) return false;
        var origins = context.Request.Headers.Origin;
        return origins.Count == 0 || (origins.Count == 1 && Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) &&
            origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 && origin.UserInfo.Length == 0 &&
            string.Equals(origin.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(origin.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<BuildDeletionPreview?> PreviewAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return null;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var build = await db.UnityBuilds.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (build is null) return null;
        var blocked = !IsTerminal(build.State) ? "Pending, running and unknown builds cannot be deleted. Finish or resolve the build first."
            : streaming.IsBuildInUse(id) ? "This build has an active player or unfinished stream cleanup. Stop the stream and wait for the player to exit." : null;
        string? fileError = null;
        try { ValidateOutput(build, false, ct); }
        catch (Exception ex) when (FileFailure(ex)) { fileError = FileError(ex); }
        return new(build, await db.UnityBuildLogs.CountAsync(item => item.BuildId == id, ct), blocked, fileError is null, fileError);
    }

    public async Task<BuildDeletionResult> DeleteAsync(string id, bool deleteFiles, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return new(false, "Invalid build ID.", 400);
        // Lock order is streaming lifecycle, then catalog. Start/stop and build
        // updates cannot race the safety checks or the database commit.
        using var lease = await streaming.AcquireBuildDeletionLeaseAsync(id, ct);
        if (lease is null) return new(false, "This build is in use by a player or unfinished stream cleanup. Stop the stream first.");
        await registry.CatalogGate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var build = await db.UnityBuilds.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (build is null) return new(false, "Build not found. It may already have been deleted.", 404);
            if (!IsTerminal(build.State)) return new(false, "Pending, running and unknown builds cannot be deleted. Finish or resolve the build first.");
            ct.ThrowIfCancellationRequested();
            if (deleteFiles)
            {
                try
                {
                    var output = ValidateOutput(build, true, ct);
                    // The path is derived only from the persisted record. It is
                    // exactly one marker-owned GUID folder, never an artifact list.
                    // A disconnected browser must not cancel halfway through removal.
                    if (Directory.Exists(output)) await Task.Run(() => Directory.Delete(output, recursive: true), CancellationToken.None);
                }
                catch (Exception ex) when (FileFailure(ex))
                {
                    logger.LogWarning(ex, "Build output deletion failed for {BuildId}; saved history was retained", id);
                    return new(false, FileError(ex) + " The build history and logs were kept. If removal started, some files may already be gone; retry or leave the file option unchecked.");
                }
            }
            db.UnityBuilds.Remove(build); // Captured logs cascade with the record.
            try { await db.SaveChangesAsync(CancellationToken.None); }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Build history deletion failed for {BuildId}", id);
                return new(false, "The build history could not be removed. Files may already have been removed if selected; refresh and retry.", 503);
            }
            return new(true, null, 200);
        }
        finally { registry.CatalogGate.Release(); }
    }

    private static bool FileFailure(Exception ex) => ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or
        NotSupportedException or JsonException or InvalidOperationException or System.Security.SecurityException;

    private static string FileError(Exception ex) => ex is InvalidDataException ? ex.Message
        : "The local output folder is unavailable, linked, read-only or locked. Check the files and permissions before retrying.";

    private static string ValidateOutput(UnityBuild build, bool scan, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("Output-folder deletion is available only on the local Windows host. You can still delete history only.");
        // An operator's Unknown acknowledgement does not stop Unity. Retain
        // those artifacts even when its Interrupted history is removed.
        if (build.State == "Interrupted") throw new InvalidDataException("Interrupted builds may still be written by Unity. Delete history only and inspect their files manually.");
        var output = Canonical(build.OutputPath);
        var owner = Path.GetDirectoryName(output);
        var builds = owner is null ? null : Path.GetDirectoryName(owner);
        var project = builds is null ? null : Path.GetDirectoryName(builds);
        if (!string.Equals(Path.GetFileName(output), build.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(owner), "PlaytestOps", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(builds), "Builds", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(project) || string.Equals(project.TrimEnd('\\'), Path.GetPathRoot(project)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The saved output is not an approved project/Builds/PlaytestOps/build-ID folder. Delete history only.");
        foreach (var identity in new[] { (build.ExecutablePath, "Player.exe"), (build.ManifestPath, "build-manifest.json") })
            if (identity.Item1.Length > 0 && !string.Equals(Canonical(identity.Item1), Path.Combine(output, identity.Item2), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The saved artifact paths do not belong to this build folder. Delete history only.");
        CheckAncestors(output);
        if (File.Exists(output)) throw new InvalidDataException("The output location is a file, not a build folder.");
        if (!Directory.Exists(output)) return output; // Already removed externally; no filesystem work remains.
        var marker = Path.Combine(output, ".playtestops-build");
        if (!File.Exists(marker)) throw new InvalidDataException("The PlaytestOps build ownership marker is missing. Delete history only.");
        CheckOrdinary(marker);
        if (new FileInfo(marker).Length != 0) throw new InvalidDataException("The build ownership marker is invalid. Delete history only.");
        var manifest = Path.Combine(output, "build-manifest.json");
        if (File.Exists(manifest))
        {
            CheckOrdinary(manifest);
            using var file = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("The build manifest is invalid or oversized. Delete history only.");
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!root.TryGetProperty("protocolVersion", out var protocol) || !protocol.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("buildId", out var identity) || identity.GetString() != build.Id ||
                !root.TryGetProperty("revision", out var revision) || revision.GetString() != build.Revision ||
                (root.TryGetProperty("activeTarget", out var target) && target.GetString() != build.ActiveTarget))
                throw new InvalidDataException("The manifest identifies a different build. Delete history only.");
        }
        if (scan) Scan(output, ct);
        return output;
    }

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) || path.Any(char.IsControl) || path.Skip(2).Contains(':') ||
            path.Split(['\\', '/']).Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
            throw new InvalidDataException("Only ordinary fully qualified local build paths can be removed. Delete history only.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void CheckAncestors(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current)) CheckOrdinary(current);
    }

    private static FileAttributes CheckOrdinary(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked files or directories cannot be removed by the dashboard. Delete history only.");
        return attributes;
    }

    private static void Scan(string output, CancellationToken ct)
    {
        var pending = new Stack<string>(); pending.Push(output);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            var attributes = CheckOrdinary(directory);
            if ((attributes & FileAttributes.ReadOnly) != 0) throw new IOException("Read-only build directory.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > 100000) throw new InvalidDataException("The build folder exceeds the safe deletion entry limit. Delete history only.");
                attributes = CheckOrdinary(path);
                if ((attributes & FileAttributes.ReadOnly) != 0) throw new IOException("Read-only build file.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
            }
        }
    }
}
