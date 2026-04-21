using System.Net;
using System.Text.RegularExpressions;
using Ps2TextureGrabber.Services;

namespace Ps2TextureGrabber.Downloaders;

/// <summary>
/// Downloads from Google Drive public share links.
/// Handles the virus-scan confirmation page (UUID + cookie flow used since 2022).
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

        var cookieJar = new CookieContainer();
        var handler   = new HttpClientHandler
        {
            AllowAutoRedirect      = true,
            MaxAutomaticRedirections = 10,
            CookieContainer        = cookieJar,
            UseCookies             = true,
        };
        using var client = new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ps2-texture-grabber/2.0");

        // Attempt 1: usercontent domain with confirm=t (works for many files directly)
        var directUrl = $"https://drive.usercontent.google.com/download?id={fileId}&export=download&confirm=t";
        _log.Debug($"Google Drive: trying direct URL");

        using var firstResp = await client.GetAsync(
            directUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        var contentType = firstResp.Content.Headers.ContentType?.MediaType ?? "";
        if (firstResp.IsSuccessStatusCode &&
            !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            _log.Debug("Google Drive: direct download accepted");
            await HttpStreamer.StreamToFileAsync(firstResp, outFile, onProgress, ct).ConfigureAwait(false);
            return;
        }

        // Attempt 2: read the HTML to extract UUID from the virus-scan form
        var page  = await firstResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var uuidM = UuidRx().Match(page);

        if (!uuidM.Success)
            throw new InvalidOperationException(
                $"Google Drive: file {fileId} requires sign-in, is restricted, or does not exist");

        var uuid        = uuidM.Groups[1].Value;
        var downloadUrl = $"https://drive.usercontent.google.com/download?id={fileId}&export=download&confirm=t&uuid={uuid}";
        _log.Debug($"Google Drive: using UUID confirmation ({uuid})");

        using var resp = await client.GetAsync(
            downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await HttpStreamer.StreamToFileAsync(resp, outFile, onProgress, ct).ConfigureAwait(false);
    }

    private static string? ExtractFileId(string url)
    {
        var m = FileIdRx().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"(?:drive\.google\.com/file/d/|open\?id=|uc\?[^""' <>]*id=)([A-Za-z0-9_\-]+)")]
    private static partial Regex FileIdRx();

    [GeneratedRegex(@"name=""uuid""\s+value=""([^""]+)""")]
    private static partial Regex UuidRx();
}
