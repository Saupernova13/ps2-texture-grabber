using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Fallback serial / CRC lookup via wiki.pcsx2.net.
///
/// Two query paths:
///   1.  Serial  -> GET https://wiki.pcsx2.net/{SERIAL}  (wiki redirects to game page)
///   2.  Name    -> opensearch API, then fetch the top result
///
/// Pages are cached under data/cache/wiki/ for 7 days.
/// </summary>
public sealed partial class WikiService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    private readonly FlareSolverrClient _flare;
    private readonly Logger             _log;
    private readonly string             _wikiCacheDir;

    public WikiService(FlareSolverrClient flare, Logger log, string wikiCacheDir)
    {
        _flare        = flare;
        _log          = log;
        _wikiCacheDir = wikiCacheDir;
        Directory.CreateDirectory(wikiCacheDir);
    }

    // -------------------------------------------------------------------------
    // Public API

    /// <summary>Returns a serial string (e.g. "SLUS-20336") or null.</summary>
    public async Task<string?> GetSerialForNameAsync(string name, CancellationToken ct = default)
    {
        var page = await FindGamePageAsync(name, ct).ConfigureAwait(false);
        if (page is null) return null;

        var regions = ParseRegionData(page.Html);
        if (regions.Count == 0)
        {
            _log.Debug($"  Wiki page found for '{name}' but no serial data extracted");
            return null;
        }
        // Prefer NTSC-U, then PAL, then whatever we have
        var best = regions
            .OrderBy(r => r.Region switch { "NTSC-U" => 0, "PAL" => 1, _ => 2 })
            .First();
        return best.Serial;
    }

    /// <summary>Returns a CRC string (8 hex chars, upper) or null.</summary>
    public async Task<string?> GetCrcForSerialAsync(string serial, CancellationToken ct = default)
    {
        var page = await FindGamePageAsync(serial, ct).ConfigureAwait(false);
        if (page is null) return null;

        var regions = ParseRegionData(page.Html);
        var match   = regions.FirstOrDefault(r =>
            string.Equals(r.Serial, serial, StringComparison.OrdinalIgnoreCase));
        return match?.Crc;
    }

    // -------------------------------------------------------------------------
    // Page fetch (cached)

    private sealed record WikiPage(string Title, string Url, string Html);

    private async Task<WikiPage?> FindGamePageAsync(string query, CancellationToken ct)
    {
        // Path 1 — direct serial redirect
        if (SerialFormatRx().IsMatch(query))
        {
            var url = $"https://wiki.pcsx2.net/{query}";
            _log.Debug($"Wiki serial redirect: {query}");
            try
            {
                var html  = await FetchCachedAsync($"serial_{query}", url, ct).ConfigureAwait(false);
                if (html is not null
                    && !html.Contains("There is currently no text in this page")
                    && !html.Contains("Wiki does not have an article"))
                {
                    var titleM = H1TitleRx().Match(html);
                    var title  = titleM.Success
                        ? WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim())
                        : query;
                    return new WikiPage(title, url, html);
                }
                _log.Debug($"Wiki has no page for serial {query}");
            }
            catch (Exception ex)
            {
                _log.Warn($"Wiki serial fetch failed: {ex.Message}");
            }
        }

        // Path 2 — opensearch API
        var results = await OpenSearchAsync(query, ct).ConfigureAwait(false);
        if (results.Count == 0)
        {
            _log.Warn($"Wiki opensearch returned no results for '{query}'");
            return null;
        }

        var scored = results
            .Select(r => (Result: r, Score: ScoreResult(r.Title, query)))
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored[0].Score <= 0)
        {
            _log.Warn($"Wiki opensearch: no result scored positively for '{query}'");
            return null;
        }

        var best = scored[0].Result;
        try
        {
            var html = await FetchCachedAsync($"page_{best.Title}", best.Url, ct).ConfigureAwait(false);
            if (html is null) return null;
            return new WikiPage(best.Title, best.Url, html);
        }
        catch (Exception ex)
        {
            _log.Error($"Wiki page fetch failed for {best.Url}: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Opensearch

    private sealed record SearchResult(string Title, string Url);

    private async Task<List<SearchResult>> OpenSearchAsync(string query, CancellationToken ct, int limit = 5)
    {
        var enc    = Uri.EscapeDataString(query);
        var apiUrl = $"https://wiki.pcsx2.net/api.php?action=opensearch&search={enc}&limit={limit}&format=json";
        var key    = $"opensearch_{query}_{limit}";

        _log.Debug($"Wiki opensearch: {query}");
        var raw = await FetchCachedAsync(key, apiUrl, ct).ConfigureAwait(false);
        if (raw is null) return [];

        // FlareSolverr wraps JSON in <pre>...</pre>
        var preM = PreTagRx().Match(raw);
        var json = preM.Success ? WebUtility.HtmlDecode(preM.Groups[1].Value) : raw;

        try
        {
            var arr = JsonNode.Parse(json)?.AsArray();
            if (arr is null || arr.Count < 4) return [];

            var titles = arr[1]?.AsArray();
            var urls   = arr[3]?.AsArray();
            if (titles is null || urls is null) return [];

            var out_ = new List<SearchResult>(titles.Count);
            for (int i = 0; i < titles.Count; i++)
            {
                var t = titles[i]?.GetValue<string>();
                var u = urls[i]?.GetValue<string>();
                if (t is not null && u is not null)
                    out_.Add(new SearchResult(t, u));
            }
            return out_;
        }
        catch
        {
            _log.Warn($"Wiki opensearch returned non-JSON for '{query}'");
            return [];
        }
    }

    // -------------------------------------------------------------------------
    // Region data extraction

    private sealed record RegionEntry(string Serial, string? Crc, string Region);

    private List<RegionEntry> ParseRegionData(string pageHtml)
    {
        var out_ = new List<RegionEntry>();

        // Strip scripts and styles
        var html = ScriptRx().Replace(pageHtml, "");
        html = StyleRx().Replace(html, "");

        // Find "CRCs:" label blocks — each followed immediately by a <td> with hex CRCs
        var crcBlocks = CrcBlockRx().Matches(html);

        if (crcBlocks.Count == 0)
        {
            // No CRCs — still record serials with null CRC
            foreach (Match sm in SerialInHtmlRx().Matches(html))
                out_.Add(new RegionEntry(sm.Groups[1].Value, null, "Unknown"));
            return out_;
        }

        foreach (Match blk in crcBlocks)
        {
            var tdContent = blk.Groups[1].Value;
            var crcs = CrcValueRx().Matches(tdContent)
                .Select(m => m.Groups[1].Value.ToUpperInvariant())
                .Distinct()
                .ToList();
            if (crcs.Count == 0) continue;

            // Context window: up to 4000 chars before the CRC label
            int ctxStart = Math.Max(0, blk.Index - 4000);
            var ctx      = html[ctxStart..blk.Index];

            // Nearest preceding serial
            var serialMs = SerialInHtmlRx().Matches(ctx);
            var serial   = serialMs.Count > 0
                ? serialMs[serialMs.Count - 1].Groups[1].Value
                : null;

            // Nearest preceding region marker
            var regionMs = RegionMarkerRx().Matches(ctx);
            string region;
            if (regionMs.Count > 0)
            {
                region = regionMs[regionMs.Count - 1].Value.ToUpperInvariant();
            }
            else if (serial is not null)
            {
                region = GuessRegionFromSerial(serial);
            }
            else
            {
                region = "Unknown";
            }

            if (serial is null) continue;

            // Each CRC gets its own entry (wiki sometimes lists multiple dumps)
            foreach (var crc in crcs)
                out_.Add(new RegionEntry(serial, crc, region));
        }

        return out_;
    }

    private static string GuessRegionFromSerial(string serial)
    {
        var prefix = serial[..4];
        return prefix switch
        {
            "SLUS" or "SCUS" => "NTSC-U",
            "SLES" or "SCES" => "PAL",
            "SLPS" or "SCPS" => "NTSC-J",
            "SLPM" or "SCPM" => "NTSC-J",
            _                => "Unknown"
        };
    }

    // -------------------------------------------------------------------------
    // Fuzzy title scoring for opensearch results

    private static int ScoreResult(string title, string query)
    {
        var t = Normalize(title);
        var q = Normalize(query);
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(q)) return 0;

        int score = 0;
        if (t == q) score += 1000;
        if (t.StartsWith(q, StringComparison.Ordinal)) score += 300;

        var qTokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                       .Where(tok => tok.Length >= 2)
                       .ToArray();
        if (qTokens.Length > 0 && qTokens.All(tok =>
            Regex.IsMatch(t, $@"\b{Regex.Escape(tok)}\b")))
            score += 500;

        return score;
    }

    // -------------------------------------------------------------------------
    // Cache helpers

    private async Task<string?> FetchCachedAsync(string key, string url, CancellationToken ct)
    {
        var path = CachePath(key);
        if (File.Exists(path))
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age < CacheTtl)
            {
                _log.Debug($"Wiki cache hit: {key}");
                return File.ReadAllText(path);
            }
        }

        var resp = await _flare.GetPageAsync(url).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(resp.Html))
            File.WriteAllText(path, resp.Html);
        return resp.Html;
    }

    private string CachePath(string key)
    {
        var safe = SafeKeyRx().Replace(key, "_");
        return Path.Combine(_wikiCacheDir, safe + ".html");
    }

    private static string Normalize(string s)
        => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    // ---- compiled regexes ----
    [GeneratedRegex(@"^[A-Z]{4}-\d{5}$")]
    private static partial Regex SerialFormatRx();

    [GeneratedRegex(@"<h1[^>]*class=""firstHeading""[^>]*>([^<]+)</h1>")]
    private static partial Regex H1TitleRx();

    [GeneratedRegex(@"<pre[^>]*>(.*?)</pre>", RegexOptions.Singleline)]
    private static partial Regex PreTagRx();

    [GeneratedRegex(@"(?is)<script[^>]*>.*?</script>")]
    private static partial Regex ScriptRx();

    [GeneratedRegex(@"(?is)<style[^>]*>.*?</style>")]
    private static partial Regex StyleRx();

    [GeneratedRegex(@"(?is)CRCs?:\s*</b>\s*</td>\s*<td[^>]*>(.*?)</td>")]
    private static partial Regex CrcBlockRx();

    [GeneratedRegex(@"\b([0-9A-Fa-f]{8})\b")]
    private static partial Regex CrcValueRx();

    [GeneratedRegex(@"\b([A-Z]{4}-\d{5})\b")]
    private static partial Regex SerialInHtmlRx();

    [GeneratedRegex(@"(?i)(NTSC-U|NTSC-J|NTSC-K|NTSC-C|NTSC-A|PAL)")]
    private static partial Regex RegionMarkerRx();

    [GeneratedRegex(@"[^A-Za-z0-9_\-\.]")]
    private static partial Regex SafeKeyRx();
}
