using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// HTTP streaming download with progress reporting.
/// Handles archive.org /details/ -> /download/ normalisation.
/// </summary>
public sealed class DirectDownloader : IDownloader
{
    private readonly Logger _log;
    public DirectDownloader(Logger log) => _log = log;

    public async Task DownloadAsync(
        string                    url,
        string                    outFile,
        Action<long, long, int>?  onProgress = null,
        CancellationToken         ct         = default)
    {
        // Normalise archive.org details links
        var finalUrl = url;
        if (url.Contains("archive.org/details/"))
            finalUrl = url.Replace("/details/", "/download/");

        _log.Info($"Direct HTTP download: {finalUrl}");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        using var client = new HttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;  // streaming; rely on read timeout
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ps2-texture-grabber/2.0");

        using var resp = await client.GetAsync(
            finalUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total      = resp.Content.Headers.ContentLength ?? -1;
        long bytesRead = 0;
        int  lastPct   = -1;
        long lastTick  = Environment.TickCount64;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Open(outFile, FileMode.Create, FileAccess.Write);

        var buf = new byte[81_920];
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            bytesRead += n;

            if (onProgress is not null && total > 0)
            {
                int pct  = (int)(bytesRead * 100L / total);
                long now = Environment.TickCount64;
                if (pct != lastPct && now - lastTick >= 1000)
                {
                    onProgress(bytesRead, total, pct);
                    lastPct  = pct;
                    lastTick = now;
                }
            }
        }

        // Final progress tick
        if (onProgress is not null && total > 0)
            onProgress(bytesRead, total, 100);

        if (!File.Exists(outFile) || new FileInfo(outFile).Length == 0)
            throw new InvalidOperationException($"Download produced empty file: {outFile}");
    }
}
