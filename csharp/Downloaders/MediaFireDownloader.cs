using System.Text.RegularExpressions;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// Downloads from MediaFire.
/// MediaFire serves a HTML page with a direct download button; we extract the
/// actual file URL from the page source and then stream it.
/// </summary>
public sealed partial class MediaFireDownloader : IDownloader
{
    private readonly Logger _log;
    public MediaFireDownloader(Logger log) => _log = log;

    public async Task DownloadAsync(
        string                   url,
        string                   outFile,
        Action<long, long, int>? onProgress = null,
        CancellationToken        ct         = default)
    {
        _log.Info($"MediaFire download: {url}");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ps2-texture-grabber/2.0");

        // Fetch the MediaFire page to find the direct download link
        var page    = await client.GetStringAsync(url, ct).ConfigureAwait(false);
        var directM = DirectLinkRx().Match(page);
        if (!directM.Success)
            throw new InvalidOperationException(
                $"Could not find direct download link on MediaFire page: {url}");

        var directUrl = directM.Groups[1].Value;
        _log.Debug($"MediaFire direct URL: {directUrl}");

        using var resp = await client.GetAsync(
            directUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total     = resp.Content.Headers.ContentLength ?? -1;
        long read     = 0;
        int  lastPct  = -1;
        long lastTick = Environment.TickCount64;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Open(outFile, FileMode.Create, FileAccess.Write);
        var buf = new byte[81_920];
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (onProgress is not null && total > 0)
            {
                int pct  = (int)(read * 100L / total);
                long now = Environment.TickCount64;
                if (pct != lastPct && now - lastTick >= 1000)
                {
                    onProgress(read, total, pct);
                    lastPct  = pct;
                    lastTick = now;
                }
            }
        }
        if (onProgress is not null && total > 0) onProgress(read, total, 100);
    }

    // The download URL appears as: id="downloadButton" href="https://download..."
    [GeneratedRegex(@"id=""downloadButton""\s+href=""([^""]+)""")]
    private static partial Regex DirectLinkRx();
}
