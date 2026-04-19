using System.Net;
using System.Text.RegularExpressions;
using Ps2TextureGrabber.Models;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Browses the GBAtemp PCSX2 HD Texture Pack subforum via FlareSolverr,
/// scores thread titles against a serial/game-name query, and extracts
/// download links from the winning thread's opening post.
///
/// All per-page state is held in plain local variables — no collection
/// wrapping tricks, no scoping surprises.
/// </summary>
public sealed partial class GbatempService
{
    // Ordered list of (host-name, compiled-regex) pairs.
    // Ordering matters: MEGA first since it's the most common host.
    private static readonly (string Name, Regex Pattern)[] HostPatterns =
    [
        ("MEGA",      MegaRx()),
        ("Archive",   ArchiveRx()),
        ("GDrive",    GDriveRx()),
        ("MediaFire", MediaFireRx()),
        ("Yandex",    YandexRx()),
        ("GitHub",    GitHubRx()),
    ];

    // Single shared HttpClient for redirect-following (short links, etc.)
    // AllowAutoRedirect follows up to 10 hops transparently.
    private static readonly HttpClient _httpRedirect = new(
        new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
    { Timeout = TimeSpan.FromSeconds(15) };

    private readonly FlareSolverrClient _flare;
    private readonly Logger             _log;

    public GbatempService(FlareSolverrClient flare, Logger log)
    {
        _flare = flare;
        _log   = log;
    }

    // -------------------------------------------------------------------------
    // Forum search

    /// <summary>
    /// Browse up to <paramref name="maxPages"/> pages of the subforum index and
    /// return the highest-scoring thread, or null if nothing scores above zero.
    /// </summary>
    public async Task<ForumThread?> FindThreadAsync(
        string  serial,
        string? gameName,
        int     nodeId   = 549,
        int     maxPages = 10,
        CancellationToken ct = default)
    {
        var sessionId = await _flare.CreateSessionAsync().ConfigureAwait(false);

        // allThreads: deduplicated across pages, keyed by ThreadId (string).
        // Using Dictionary<string,ForumThread> — no ArrayList, no ,$trick,
        // no PowerShell collection-wrapping ambiguity.
        var allThreads = new Dictionary<string, ForumThread>(capacity: 400);

        ForumThread? bestResult = null;
        int          bestScore  = 0;

        try
        {
            for (int page = 1; page <= maxPages; page++)
            {
                ct.ThrowIfCancellationRequested();

                var pageUrl = page == 1
                    ? $"https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.{nodeId}/"
                    : $"https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.{nodeId}/page-{page}";

                _log.Info($"Browsing GBAtemp forum page {page}...");

                FlareSolverrClient.PageResult resp;
                try
                {
                    resp = await _flare.GetPageAsync(pageUrl, sessionId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn($"  Page {page} fetch failed: {ex.Message}");
                    continue;
                }

                // Parse into a plain List<ForumThread> — explicit type, no wrapping.
                List<ForumThread> pageThreads = ParseThreadsFromHtml(resp.Html);
                _log.Debug(
                    $"  Page {page} html={resp.Html.Length} " +
                    $"threads={pageThreads.Count} " +
                    $"allSoFar={allThreads.Count}");

                if (pageThreads.Count == 0)
                {
                    _log.Debug($"  Page {page} returned no thread links — stopping");
                    break;
                }

                // Deduplicate into allThreads.
                foreach (var t in pageThreads)
                    allThreads.TryAdd(t.ThreadId, t);

                _log.Debug($"  Page {page} allThreads after add: {allThreads.Count}");

                // Score this page's threads.
                foreach (var t in pageThreads)
                {
                    int score = ScoreThread(t, serial, gameName);
                    if (score > bestScore)
                    {
                        bestScore  = score;
                        bestResult = t;
                    }
                }

                if (bestScore >= 50)
                {
                    _log.Debug($"  Strong match found on page {page} — stopping early");
                    break;
                }
            }
        }
        finally
        {
            await _flare.DestroySessionAsync(sessionId).ConfigureAwait(false);
        }

        if (bestResult is not null && bestScore > 0)
        {
            _log.Success($"Selected thread: \"{bestResult.Title}\" (score {bestScore})");
            return bestResult;
        }

        _log.Warn($"No matching thread found in {allThreads.Count} forum entries scanned");
        return null;
    }

    // -------------------------------------------------------------------------
    // Thread scoring  (pure function, no I/O)

    private static int ScoreThread(ForumThread t, string serial, string? gameName)
    {
        int score = 0;

        if (!string.IsNullOrEmpty(serial)
            && t.Title.Contains(serial, StringComparison.OrdinalIgnoreCase))
            score += 100;

        if (!string.IsNullOrEmpty(gameName))
        {
            var tokens    = NormalizeText(gameName)
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Where(tok => tok.Length >= 2);
            var titleLow  = t.Title.ToLowerInvariant();
            foreach (var tok in tokens)
                if (titleLow.Contains(tok, StringComparison.Ordinal))
                    score += 10;
        }

        if (NoiseTitleRx().IsMatch(t.Title))  score -= 50;
        if (QualityTitleRx().IsMatch(t.Title)) score += 20;

        return score;
    }

    // -------------------------------------------------------------------------
    // HTML parsing

    /// <summary>
    /// Parse XenForo thread-listing HTML into a <see cref="List{T}"/>.
    /// Pattern: href="/threads/{slug}.{id}/"...>Title&lt;/a&gt;
    /// Returns an empty list (never null) when nothing matches.
    /// </summary>
    private static List<ForumThread> ParseThreadsFromHtml(string html)
    {
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var threads = new List<ForumThread>();

        foreach (Match m in ThreadLinkRx().Matches(html))
        {
            var id = m.Groups[2].Value;
            if (!seen.Add(id)) continue;   // skip duplicates

            var title = WebUtility.HtmlDecode(m.Groups[3].Value.Trim());
            if (string.IsNullOrWhiteSpace(title)
                || PaginationTitleRx().IsMatch(title))
                continue;

            threads.Add(new ForumThread(
                ThreadId: id,
                Slug:     m.Groups[1].Value,
                Title:    title,
                Url:      $"https://gbatemp.net/threads/{m.Groups[1].Value}.{id}/"));
        }

        return threads;
    }

    // -------------------------------------------------------------------------
    // Download link extraction

    /// <summary>
    /// Fetch a thread page and return all external download links found in the
    /// opening post's bbWrapper div, ordered by host priority.
    ///
    /// Extra steps beyond the naive pass:
    ///  1. Scan all external hrefs in the OP for short/unknown links and follow
    ///     HTTP redirect chains to classify the resolved final URL.
    ///  2. For matched GitHub release-page URLs (not direct asset files), fetch
    ///     the page via FlareSolverr and extract .zip/.7z/.rar asset links.
    ///  3. If STILL no links, save the OP HTML to disk so a human or AI agent
    ///     can inspect it, and print the path prominently.
    /// </summary>
    public async Task<List<DownloadLink>> GetDownloadLinksAsync(
        string            threadUrl,
        ForumThread?      thread   = null,
        CancellationToken ct       = default)
    {
        ct.ThrowIfCancellationRequested();
        _log.Info($"Fetching thread: {threadUrl}");

        var resp = await _flare.GetPageAsync(threadUrl).ConfigureAwait(false);
        var html = resp.Html;

        // Isolate the first post's bbWrapper.  Fall back to full page if:
        //  - no match at all, OR
        //  - the match is suspiciously short (< 300 chars) which means we
        //    captured a sidebar/description widget rather than the actual post.
        var postMatch      = FirstPostRx().Match(html);
        var capturedOp     = postMatch.Success ? postMatch.Groups[1].Value : "";
        var searchArea     = capturedOp.Length >= 300 ? capturedOp : html;
        if (capturedOp.Length == 0)
            _log.Warn("Could not isolate first post; scanning full page");
        else if (capturedOp.Length < 300)
            _log.Warn($"First-post bbWrapper too short ({capturedOp.Length} chars) — likely a widget; falling back to full-page scan");

        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<DownloadLink>();

        // --- Pass 1: known-host direct patterns --------------------------------
        foreach (var (hostName, pattern) in HostPatterns)
        {
            foreach (Match hit in pattern.Matches(searchArea))
            {
                var url = hit.Value.TrimEnd('.', ',', ')', ']', '"', '\'');
                if (seen.Add(url))
                    links.Add(new DownloadLink(hostName, url));
            }
        }

        // --- Pass 2: follow GitHub release pages → extract direct asset URLs ---
        var githubPageLinks = links
            .Where(l => l.Host == "GitHub" && !GitHubDirectAssetRx().IsMatch(l.Url))
            .ToList();

        foreach (var gl in githubPageLinks)
        {
            _log.Debug($"  Following GitHub release page: {gl.Url}");
            var assetLinks = await FollowGitHubReleasePageAsync(gl.Url, ct).ConfigureAwait(false);
            foreach (var al in assetLinks)
                if (seen.Add(al.Url))
                    links.Add(al);
        }

        // --- Pass 3: resolve short / unknown hrefs via HTTP redirect chain -----
        var allHrefs = AllExternalHrefRx().Matches(searchArea)
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value.Trim()))
            .Where(u => u.StartsWith("http", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Only bother with hrefs we haven't already classified.
        var unclassified = allHrefs
            .Where(u => !HostPatterns.Any(hp => hp.Pattern.IsMatch(u)))
            .ToList();

        if (unclassified.Count > 0)
            _log.Debug($"  Found {unclassified.Count} unclassified href(s) — resolving redirects...");

        foreach (var rawUrl in unclassified)
        {
            ct.ThrowIfCancellationRequested();
            var resolved = await ResolveRedirectAsync(rawUrl, ct).ConfigureAwait(false);
            if (resolved is null) continue;

            _log.Debug($"  Resolved {rawUrl}  →  {resolved}");

            // Try to classify the resolved URL.
            foreach (var (hostName, pattern) in HostPatterns)
            {
                var m = pattern.Match(resolved);
                if (!m.Success) continue;
                var url = m.Value.TrimEnd('.', ',', ')', ']', '"', '\'');
                if (seen.Add(url))
                {
                    _log.Debug($"    Classified as {hostName}");
                    links.Add(new DownloadLink(hostName, url));
                }
                break;
            }
        }

        // --- Result reporting --------------------------------------------------
        if (links.Count == 0)
        {
            _log.Warn("No download links found in OP");
            // Dump the FULL thread HTML so a human/AI agent gets the whole picture,
            // not just the (possibly wrong) OP extract.
            DumpMissingLinksHtml(threadUrl, thread, html);
        }
        else
        {
            _log.Success(
                $"Found {links.Count} download link(s): "
                + string.Join(", ", links.Select(l => l.Host)));
        }

        return links;
    }

    // -------------------------------------------------------------------------
    // Short-link / redirect resolution

    /// <summary>
    /// Follow HTTP redirects (up to 10 hops) and return the final URL, or null
    /// if the request times out or throws.  Does NOT use FlareSolverr — this is
    /// a plain HTTP HEAD request that relies on the server returning Location
    /// headers, which shortener services always do.
    /// </summary>
    private async Task<string?> ResolveRedirectAsync(string url, CancellationToken ct)
    {
        try
        {
            // HEAD first (cheap); fall back to GET if the server rejects HEAD.
            var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var resp = await _httpRedirect.SendAsync(req, ct).ConfigureAwait(false);

            // Some servers (Cloudflare-protected) return 403/405 for HEAD.
            if (!resp.IsSuccessStatusCode &&
                (int)resp.StatusCode is 403 or 405 or 503)
            {
                var get = new HttpRequestMessage(HttpMethod.Get, url);
                get.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                resp = await _httpRedirect.SendAsync(
                    get,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);
            }

            return resp.RequestMessage?.RequestUri?.ToString() ?? url;
        }
        catch (Exception ex)
        {
            _log.Debug($"  Redirect resolve failed for {url}: {ex.Message}");
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // GitHub release page → direct asset links

    /// <summary>
    /// Fetch a GitHub releases page via FlareSolverr and extract direct-download
    /// asset URLs (.zip, .7z, .rar).  GitHub serves these without Cloudflare but
    /// we reuse FlareSolverr for consistency and session reuse.
    /// </summary>
    private async Task<List<DownloadLink>> FollowGitHubReleasePageAsync(
        string releaseUrl, CancellationToken ct)
    {
        var result = new List<DownloadLink>();
        try
        {
            var resp = await _flare.GetPageAsync(releaseUrl).ConfigureAwait(false);
            foreach (Match m in GitHubDirectAssetRx().Matches(resp.Html))
            {
                var url = "https://github.com" + m.Groups[1].Value;
                url = WebUtility.HtmlDecode(url);
                result.Add(new DownloadLink("GitHub", url));
                _log.Debug($"    GitHub asset: {url}");
            }
            if (result.Count == 0)
                _log.Warn($"  No direct GitHub assets found on: {releaseUrl}");
        }
        catch (Exception ex)
        {
            _log.Warn($"  Could not fetch GitHub release page: {ex.Message}");
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // HTML dump for AI triage

    /// <summary>
    /// When no download links can be extracted, save the OP HTML to disk so an
    /// AI agent (or a human) can inspect it and update the parsing logic.
    /// </summary>
    private void DumpMissingLinksHtml(string threadUrl, ForumThread? thread, string opHtml)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.MissingLinksDir);

            // Use thread slug if available, otherwise sanitize the URL.
            var slug = thread?.Slug
                ?? Regex.Replace(threadUrl, @"[^\w\-]", "_").TrimEnd('_');
            // Trim very long names.
            if (slug.Length > 80) slug = slug[..80];

            var dumpPath = Path.Combine(AppPaths.MissingLinksDir, $"{slug}.html");
            File.WriteAllText(dumpPath, opHtml, System.Text.Encoding.UTF8);

            _log.Warn(
                "========================================================\n" +
                "  DOWNLOAD LINKS NOT FOUND — OP HTML saved for triage:\n" +
                $"  {dumpPath}\n" +
                "  An AI agent can open this file and determine the correct\n" +
                "  link pattern to add to GbatempService.cs.\n" +
                "========================================================");
        }
        catch (Exception ex)
        {
            _log.Warn($"  Could not write missing-links dump: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers

    private static string NormalizeText(string s)
        => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    // ---- compiled regexes ----

    // XenForo thread links: href="/threads/{slug}.{id}/" ... >Title</a>
    [GeneratedRegex("""href="/threads/([A-Za-z0-9\-_.]+)\.(\d+)/"\s[^>]*>([^<]+)</a>""")]
    private static partial Regex ThreadLinkRx();

    // Pagination / member-profile noise titles
    [GeneratedRegex(@"^(Page \d+|#\d+|Last)$", RegexOptions.IgnoreCase)]
    private static partial Regex PaginationTitleRx();

    // First post bbWrapper (Singleline so . matches newlines)
    [GeneratedRegex(
        """<div class="bbWrapper">(.*?)</div>\s*(?:<(?:div|aside|footer))""",
        RegexOptions.Singleline)]
    private static partial Regex FirstPostRx();

    // Thread title quality signals
    [GeneratedRegex(
        @"\b(request|dump|help|wanted|looking\s+for|need|question)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoiseTitleRx();

    [GeneratedRegex(
        @"\b(hd|upscaled|texture|remaster|pack|replacement|4k|2k)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex QualityTitleRx();

    // Download-link patterns (one per supported host)
    [GeneratedRegex(@"https?://mega\.nz/(?:file|folder)/[A-Za-z0-9#!_\-]+")]
    private static partial Regex MegaRx();

    [GeneratedRegex(@"https?://archive\.org/(?:details|download)/[^\s""'<>]+")]
    private static partial Regex ArchiveRx();

    [GeneratedRegex(@"https?://(?:drive|docs)\.google\.com/(?:file/d/|open\?id=|uc\?[^""' <>]*id=)[A-Za-z0-9_\-]+")]
    private static partial Regex GDriveRx();

    [GeneratedRegex(@"https?://(?:www\.)?mediafire\.com/(?:file|folder)/[A-Za-z0-9_\-/?&=.]+")]
    private static partial Regex MediaFireRx();

    [GeneratedRegex(@"https?://disk\.yandex\.(?:ru|com)/d/[A-Za-z0-9_\-]+")]
    private static partial Regex YandexRx();

    [GeneratedRegex(@"https?://github\.com/[^/\s""'<>]+/[^/\s""'<>]+/releases/[^\s""'<>]+")]
    private static partial Regex GitHubRx();

    // Direct GitHub release asset: href="/owner/repo/releases/download/tag/file.ext"
    [GeneratedRegex(
        """href="(/[^"]+/releases/download/[^"]+\.(?:zip|7z|rar|tar\.gz))" """,
        RegexOptions.IgnoreCase)]
    private static partial Regex GitHubDirectAssetRx();

    // All external href values from HTML — used to catch shorteners and unknowns.
    [GeneratedRegex("""href=["'](https?://[^"'<>\s]+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex AllExternalHrefRx();
}
