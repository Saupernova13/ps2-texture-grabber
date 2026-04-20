using System.Diagnostics;
using System.Text.RegularExpressions;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// Downloads from Google Drive.  Prefers gdown if available (handles large-file
/// virus-scan prompts automatically).  Falls back to the confirmation-token
/// dance via HttpClient.
/// </summary>
public sealed partial class GoogleDriveDownloader : IDownloader
{
    private readonly Logger _log;
    public GoogleDriveDownloader(Logger log) => _log = log;

    public async Task DownloadAsync(
        string                   url,
        string                   outFile,
        Action<long, long, int>? onProgress = null,
        CancellationToken        ct         = default)
    {
        _log.Info($"Google Drive download: {url}");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        var fileId = ExtractFileId(url)
            ?? throw new ArgumentException($"Could not extract Google Drive file ID from: {url}");

        // Try gdown first
        if (TryGdown(fileId, outFile))
        {
            _log.Success("Google Drive download complete via gdown");
            return;
        }

        // Fallback: HTTP with confirmation-token handling
        await HttpFallbackAsync(fileId, outFile, onProgress, ct).ConfigureAwait(false);
    }

    private static string? ExtractFileId(string url)
    {
        var m = FileIdRx().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    private bool TryGdown(string fileId, string outFile)
    {
        try
        {
            var gdown = FindOnPath("gdown");
            if (gdown is null) return false;

            _log.Debug("Using gdown for Google Drive download");
            var psi = new ProcessStartInfo
            {
                FileName         = gdown,
                Arguments        = $"--id {fileId} -O \"{outFile}\"",
                CreateNoWindow   = true,
                UseShellExecute  = false,
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0 && File.Exists(outFile);
        }
        catch (Exception ex)
        {
            _log.Warn($"gdown failed: {ex.Message}; falling back to HTTP");
            return false;
        }
    }

    private async Task HttpFallbackAsync(
        string                   fileId,
        string                   outFile,
        Action<long, long, int>? onProgress,
        CancellationToken        ct)
    {
        _log.Debug("Google Drive HTTP confirmation-token fallback");
        using var client  = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ps2-texture-grabber/2.0");

        var downloadUrl = $"https://drive.google.com/uc?export=download&id={fileId}";

        using var first = await client.GetAsync(downloadUrl, ct).ConfigureAwait(false);
        var html        = await first.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var confirmM = ConfirmTokenRx().Match(html);
        var finalUrl = confirmM.Success
            ? $"https://drive.google.com/uc?export=download&id={fileId}&confirm={confirmM.Groups[1].Value}"
            : downloadUrl;

        using var resp = await client.GetAsync(
            finalUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await HttpStreamer.StreamToFileAsync(resp, outFile, onProgress, ct).ConfigureAwait(false);
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                            .Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    [GeneratedRegex(@"(?:drive\.google\.com/file/d/|open\?id=|uc\?[^""' <>]*id=)([A-Za-z0-9_\-]+)")]
    private static partial Regex FileIdRx();

    [GeneratedRegex(@"confirm=([0-9A-Za-z_\-]+)")]
    private static partial Regex ConfirmTokenRx();
}
