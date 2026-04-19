using System.Text.Json.Nodes;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// Downloads from Yandex Disk public links via the official API endpoint.
/// </summary>
public sealed class YandexDownloader : IDownloader
{
    private readonly Logger _log;
    public YandexDownloader(Logger log) => _log = log;

    public async Task DownloadAsync(
        string                   url,
        string                   outFile,
        Action<long, long, int>? onProgress = null,
        CancellationToken        ct         = default)
    {
        _log.Info($"Yandex Disk download: {url}");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ps2-texture-grabber/2.0");

        // Yandex public API: GET /public/api/download-url?public_key=<url>
        var apiUrl  = $"https://disk.yandex.com/public/api/download-url?public_key={Uri.EscapeDataString(url)}";
        var json    = await client.GetStringAsync(apiUrl, ct).ConfigureAwait(false);
        var node    = JsonNode.Parse(json);
        var dlUrl   = node?["href"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Yandex API did not return href for: {url}");

        _log.Debug($"Yandex direct URL: {dlUrl}");

        using var resp = await client.GetAsync(
            dlUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
}
