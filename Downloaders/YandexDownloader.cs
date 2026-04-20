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

        var apiUrl  = $"https://disk.yandex.com/public/api/download-url?public_key={Uri.EscapeDataString(url)}";
        var json    = await client.GetStringAsync(apiUrl, ct).ConfigureAwait(false);
        var node    = JsonNode.Parse(json);
        var dlUrl   = node?["href"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Yandex API did not return href for: {url}");

        _log.Debug($"Yandex direct URL: {dlUrl}");

        using var resp = await client.GetAsync(
            dlUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await HttpStreamer.StreamToFileAsync(resp, outFile, onProgress, ct).ConfigureAwait(false);
    }
}
