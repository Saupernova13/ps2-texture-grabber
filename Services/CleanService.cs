using System.Text.Json;
using Ps2TextureGrabber.Models;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// <c>ps2tex --clean</c>: reclaim the disk the tool itself is holding.
///
/// Three kinds of leftover accumulate under <c>data/</c>:
///   • job working dirs — <c>data/jobs/{id}/</c> holds the downloaded archive and its
///     extracted copy. <see cref="Worker.WorkerRunner"/> deletes it on both success and
///     failure, but a worker that is killed outright (reboot, task manager) never gets to.
///     Those are texture-pack-sized, so they are the reason this command exists.
///   • caches — the GameIndex parse, the Archive.org index, gbatemp thread lists, wiki
///     lookups, and the HTML dumps saved when a thread yields no links. All rebuild.
///   • job records — the JSON + log history, kept unless --all.
///
/// Nothing belonging to a live job is ever removed. Because <see cref="JobState"/> carries
/// no pid, "live" is Status pending/running AND touched within <see cref="StaleAfter"/> —
/// otherwise a worker killed mid-download would protect its own garbage forever.
/// </summary>
public sealed class CleanService
{
    /// <summary>How long a pending/running job may go untouched before it is presumed dead.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    public sealed record Target(string Kind, string Path, long Bytes, string Note, bool IsDirectory);

    public sealed record Result(List<Target> Removed, List<Target> Failed, long Bytes, bool DryRun);

    private readonly Logger _log;
    private readonly string _jobsDir;
    private readonly string _cacheDir;

    public CleanService(Logger log, string jobsDir, string cacheDir)
    {
        _log      = log;
        _jobsDir  = jobsDir;
        _cacheDir = cacheDir;
    }

    // -------------------------------------------------------------------------
    // Discovery

    /// <summary>Everything --clean would remove, in report order.</summary>
    public List<Target> Collect(bool includeJobs)
    {
        var targets = new List<Target>();
        var live    = LiveJobIds();

        // 1. Job working dirs. data/jobs/{id}/ is a directory; the job's own record is the
        //    sibling {id}.json file, so the two are never confused.
        if (Directory.Exists(_jobsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(_jobsDir))
            {
                var id = System.IO.Path.GetFileName(dir);
                if (live.Contains(id))
                {
                    _log.Info($"Keeping working dir for job {id} - it is still running.");
                    continue;
                }
                targets.Add(new Target("work", dir, DirectorySize(dir),
                    "abandoned download/extract dir", IsDirectory: true));
            }
        }

        // 2. Caches. The whole directory is rebuildable, so it is swept wholesale rather
        //    than by a list of filenames that would drift as services are added.
        //
        //    Empty subdirectories are skipped: AppPaths.EnsureAll recreates wiki\ and
        //    missing-links\ after every run, so offering them again would make --clean
        //    report two removals and free zero bytes every single time it is run.
        if (Directory.Exists(_cacheDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_cacheDir))
            {
                var isDir = Directory.Exists(entry);
                var bytes = isDir ? DirectorySize(entry) : FileSize(entry);
                if (isDir && !Directory.EnumerateFileSystemEntries(entry).Any()) continue;
                targets.Add(new Target("cache", entry, bytes,
                    CacheNote(System.IO.Path.GetFileName(entry)), isDir));
            }
        }

        // 3. Job history, only when asked.
        if (includeJobs && Directory.Exists(_jobsDir))
        {
            foreach (var file in Directory.EnumerateFiles(_jobsDir, "*.json"))
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(file);
                if (live.Contains(id))
                {
                    _log.Info($"Keeping job record {id} - it is still running.");
                    continue;
                }
                var status = ReadStatus(file) ?? "unreadable";
                targets.Add(new Target("job", file, FileSize(file), $"job record ({status})", false));

                var log = System.IO.Path.ChangeExtension(file, ".log");
                if (File.Exists(log))
                    targets.Add(new Target("job", log, FileSize(log), $"job log ({status})", false));
            }
        }

        return targets;
    }

    /// <summary>
    /// Ids of jobs a worker is plausibly still running. Anything pending/running but
    /// untouched for <see cref="StaleAfter"/> is treated as dead, so a killed worker's
    /// multi-GB working dir does not become permanently unreclaimable.
    /// </summary>
    private HashSet<string> LiveJobIds()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_jobsDir)) return live;

        foreach (var file in Directory.EnumerateFiles(_jobsDir, "*.json"))
        {
            JobState? state;
            try { state = JsonSerializer.Deserialize<JobState>(File.ReadAllText(file)); }
            catch { continue; }
            if (state is null) continue;
            if (state.Status is not ("pending" or "running")) continue;

            var stamp = state.LastUpdate ?? state.StartedAt ?? state.CreatedAt;
            if (DateTime.TryParse(stamp, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var touched) &&
                DateTime.UtcNow - touched.ToUniversalTime() > StaleAfter)
            {
                _log.Debug($"Job {state.Id} says '{state.Status}' but has not moved since {stamp} - treating as dead.");
                continue;
            }
            live.Add(state.Id);
        }
        return live;
    }

    private static string CacheNote(string name) => name switch
    {
        "gamedb.json"        => "GameIndex parse cache, rebuilt on demand",
        "archive-index.json" => "Archive.org index cache",
        "wiki"               => "wiki.pcsx2.net page cache",
        "missing-links"      => "saved HTML of threads that yielded no links",
        _ when name.StartsWith("gbatemp-threads-", StringComparison.OrdinalIgnoreCase)
                             => "gbatemp thread list cache",
        _                    => "cache",
    };

    // -------------------------------------------------------------------------
    // Execution

    public Result Run(bool includeJobs, bool dryRun)
    {
        var targets = Collect(includeJobs);
        var removed = new List<Target>();
        var failed  = new List<Target>();

        foreach (var t in targets)
        {
            if (dryRun) { removed.Add(t); continue; }
            try
            {
                if (t.IsDirectory) Directory.Delete(t.Path, recursive: true);
                else               File.Delete(t.Path);
                removed.Add(t);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not remove {t.Path}: {ex.Message}");
                failed.Add(t);
            }
        }

        // The cache dirs are recreated empty so the next run does not have to special-case
        // their absence (EnsureAll does this at startup too, but --clean should leave the
        // tree in the shape the app expects rather than half-missing).
        if (!dryRun) AppPaths.EnsureAll();

        return new Result(removed, failed, removed.Sum(t => t.Bytes), dryRun);
    }

    // -------------------------------------------------------------------------
    // Helpers

    private static long DirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private static string? ReadStatus(string jobFile)
    {
        try { return JsonSerializer.Deserialize<JobState>(File.ReadAllText(jobFile))?.Status; }
        catch { return null; }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024          => $"{bytes / 1024.0:F1} KB",
        _                => $"{bytes} B",
    };
}
