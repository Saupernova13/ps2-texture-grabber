using System.Text.Json;
using Ps2TextureGrabber.Models;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Checks Archive.org's pcsx2-hd-texture-packs item for a pack matching
/// the given serial. Uses the metadata API (no Playwright needed).
/// Index is cached for 24 hours.
/// </summary>
public sealed class ArchiveOrgIndexService
{
    private const string MetadataUrl  = "https://archive.org/metadata/pcsx2-hd-texture-packs";
    private const string DownloadBase = "https://archive.org/download/pcsx2-hd-texture-packs/";
    private static readonly TimeSpan   CacheTtl = TimeSpan.FromHours(24);

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "ps2tex/1.0" } },
    };

    private readonly Logger _log;
    private readonly string _cachePath;

    public ArchiveOrgIndexService(Logger log, string cachePath)
    {
        _log       = log;
        _cachePath = cachePath;
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a direct Archive.org download link if a pack exists for the
    /// given serial, otherwise null.
    /// </summary>
    public async Task<DownloadLink?> FindBySerialAsync(
        string serial, CancellationToken ct = default)
    {
        var files = await LoadFilesAsync(ct).ConfigureAwait(false);
        if (files is null) return null;

        var needle = $"[{serial}]";
        var match  = files.FirstOrDefault(f =>
            f.Contains(needle, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            _log.Info($"[Archive.org] No pack found for {serial}");
            return null;
        }

        var url = DownloadBase + Uri.EscapeDataString(match);
        _log.Success($"[Archive.org] Found pack: {match}");
        return new DownloadLink("Archive", url);
    }

    // -------------------------------------------------------------------------

    private async Task<List<string>?> LoadFilesAsync(CancellationToken ct)
    {
        // Serve from cache if fresh
        if (File.Exists(_cachePath))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath);
            if (age < CacheTtl)
            {
                _log.Debug("[Archive.org] Using cached index");
                return ParseCachedIndex(_cachePath);
            }
        }

        _log.Info("[Archive.org] Fetching index from metadata API...");
        try
        {
            var json = await _http.GetStringAsync(MetadataUrl, ct).ConfigureAwait(false);
            File.WriteAllText(_cachePath, json);
            _log.Debug("[Archive.org] Index cached");
            return ExtractFilenames(json);
        }
        catch (Exception ex)
        {
            _log.Warn($"[Archive.org] Index fetch failed: {ex.Message}");
            // Serve stale cache rather than failing entirely
            if (File.Exists(_cachePath))
            {
                _log.Warn("[Archive.org] Using stale cached index");
                return ParseCachedIndex(_cachePath);
            }
            return null;
        }
    }

    private static List<string>? ParseCachedIndex(string path)
    {
        try   { return ExtractFilenames(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static List<string> ExtractFilenames(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var       root = doc.RootElement;

        var names = new List<string>();

        // Archive.org metadata format: { "files": [ { "name": "...", ... }, ... ] }
        if (root.TryGetProperty("files", out var files))
        {
            foreach (var file in files.EnumerateArray())
            {
                if (!file.TryGetProperty("name", out var nameProp)) continue;
                var name = nameProp.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext is ".rar" or ".zip" or ".7z")
                    names.Add(name);
            }
        }

        return names;
    }
}
