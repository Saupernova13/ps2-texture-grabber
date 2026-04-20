using System.Text.Json;
using Ps2TextureGrabber.Downloaders;
using Ps2TextureGrabber.Models;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Worker;

/// <summary>
/// Detached worker entry point.
/// Reads a job JSON file, then executes:
///   1. Download  (tries each link in order via HostDispatcher)
///   2. Extract   (ArchiveExtractor)
///   3. Install   (copy files to textures/{SERIAL}/replacements/)
///   4. Configure (write gamesettings INI flags)
/// All steps write progress back to the job JSON file so the caller can poll.
/// </summary>
public static class WorkerRunner
{
    public static async Task RunAsync(string jobFile, CancellationToken ct = default)
    {
        // ---- load state ----
        if (!File.Exists(jobFile))
            throw new FileNotFoundException($"Job file not found: {jobFile}");

        var state = JsonSerializer.Deserialize<JobState>(File.ReadAllText(jobFile))
            ?? throw new InvalidOperationException("Job file is empty or invalid");

        var logFile = state.LogFile ?? Path.ChangeExtension(jobFile, ".log");
        state.LogFile = logFile;

        var log = new Logger();
        log.SetLogFile(logFile);

        log.Info($"=== Worker started for job {state.Id} ===");
        log.Info($"Query:  {state.Query}");
        log.Info($"Serial: {state.Serial}");

        // ---- mark running ----
        state.Status    = "running";
        state.StartedAt = DateTime.UtcNow.ToString("o");
        Progress(state, jobFile, "pending", 0, "Worker starting");

        try
        {
            var jobDir = Path.Combine(Path.GetDirectoryName(jobFile)!, state.Id);
            Directory.CreateDirectory(jobDir);

            // ----------------------------------------------------------------
            // 1. Download
            // ----------------------------------------------------------------
            var links = state.DownloadLinks;
            if (links.Count == 0) throw new InvalidOperationException("Job has no download links");

            var archivePath = Path.Combine(jobDir, "archive.bin");
            Progress(state, jobFile, "downloading", 0,
                $"Trying {links.Count} download link(s)");

            var dispatcher = new HostDispatcher(log);
            var servedBy   = await dispatcher.DownloadAsync(
                links,
                archivePath,
                (bytes, total, pct, host) =>
                {
                    Progress(state, jobFile,
                        step:            "downloading",
                        progress:        pct,
                        message:         $"{host}: {pct}% of {total / 1_048_576.0:F1} MB",
                        bytesDownloaded: bytes,
                        totalBytes:      total,
                        currentLink:     host);
                },
                ct).ConfigureAwait(false);

            state.ServedBy = servedBy;
            Progress(state, jobFile, "downloading", 100, $"Download complete via {servedBy}",
                currentLink: servedBy);

            // Rename archive to detected extension
            var urlForHost = links.FirstOrDefault(l => l.Host == servedBy)?.Url ?? "";
            if (HasKnownExtension(urlForHost, out var extFromUrl))
            {
                var named = Path.ChangeExtension(archivePath, extFromUrl);
                if (named != archivePath)
                {
                    File.Move(archivePath, named, overwrite: true);
                    archivePath = named;
                }
            }
            else
            {
                archivePath = ArchiveExtractor.SniffAndRename(archivePath);
            }

            log.Debug($"Archive path: {archivePath}");

            // ----------------------------------------------------------------
            // 2. Extract
            // ----------------------------------------------------------------
            Progress(state, jobFile, "extracting", 0, "Extracting archive...");
            var extractDir = Path.Combine(jobDir, "extracted");
            var extractor  = new ArchiveExtractor(log);
            extractor.Extract(archivePath, extractDir);
            Progress(state, jobFile, "extracting", 100, "Extraction complete");

            try   { File.Delete(archivePath); log.Debug($"Deleted archive: {archivePath}"); }
            catch (Exception ex) { log.Warn($"Could not delete archive: {ex.Message}"); }

            // ----------------------------------------------------------------
            // 3. Install
            // ----------------------------------------------------------------
            Progress(state, jobFile, "installing", 0, "Installing texture files...");

            var textureRoot = ArchiveExtractor.FindTextureRoot(extractDir);
            log.Debug($"Texture root: {textureRoot}");

            var targetRoot = Path.Combine(
                state.TexturesPath!,
                state.Serial!,
                "replacements");

            extractor.InstallFiles(textureRoot, targetRoot);
            Progress(state, jobFile, "installing", 100, $"Installed to {targetRoot}");

            // ----------------------------------------------------------------
            // 4. Configure INI
            // ----------------------------------------------------------------
            Progress(state, jobFile, "configuring", 0, "Writing PCSX2 INI flags...");

            var pcsx2     = new Pcsx2Service(log);
            var crc       = pcsx2.ResolveCrcFromIni(state.Serial!, state.GamesettingsPath!);
            var iniPath   = Pcsx2Service.GetIniPath(state.Serial!, crc, state.GamesettingsPath!);
            pcsx2.SetTextureIni(iniPath);

            log.Success($"INI configured: {iniPath}");
            Progress(state, jobFile, "configuring", 100, "INI configured");

            // ----------------------------------------------------------------
            // Done
            // ----------------------------------------------------------------
            state.Status      = "complete";
            state.CompletedAt = DateTime.UtcNow.ToString("o");
            Progress(state, jobFile, "complete", 100,
                $"All done! Texture pack installed for {state.GameName}");

            log.Success($"=== Job {state.Id} complete ===");
        }
        catch (OperationCanceledException)
        {
            state.Status  = "failed";
            state.Message = "Cancelled";
            Progress(state, jobFile, "failed", state.Progress, "Cancelled");
            log.Warn("Worker cancelled");
        }
        catch (Exception ex)
        {
            state.Status  = "failed";
            state.Message = ex.Message;
            Progress(state, jobFile, "failed", state.Progress, $"FAILED: {ex.Message}");
            log.Error($"Worker failed: {ex}");
            Environment.Exit(1);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers

    private static void Progress(
        JobState state,
        string   jobFile,
        string   step,
        int      progress,
        string   message,
        long?    bytesDownloaded = null,
        long?    totalBytes      = null,
        string?  currentLink     = null)
    {
        JobService.UpdateProgress(state, jobFile,
            step:            step,
            progress:        progress,
            message:         message,
            bytesDownloaded: bytesDownloaded,
            totalBytes:      totalBytes,
            currentLink:     currentLink);
    }

    private static bool HasKnownExtension(string url, out string ext)
    {
        // Strip query string before checking
        var path = url.Contains('?') ? url[..url.IndexOf('?')] : url;
        ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".zip" or ".7z" or ".rar";
    }
}
