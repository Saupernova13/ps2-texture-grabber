using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ps2TextureGrabber.Models;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Browses the GBAtemp PCSX2 HD Texture Pack subforum via FlareSolverr,
/// scores thread titles against a serial/game-name query, and extracts
/// download links from the winning thread.
/// </summary>
public sealed partial class GbatempService
{
    private static readonly (string Name, Regex Pattern)[] HostPatterns =
    [
        ("MEGA",      MegaRx()),
        ("Archive",   ArchiveRx()),
        ("GDrive",    GDriveRx()),
        ("MediaFire", MediaFireRx()),
        ("Yandex",    YandexRx()),
        ("GitHub",    GitHubReleasesRx()),
    ];

    private static readonly HttpClient _http = new(
        new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
    { Timeout = TimeSpan.FromSeconds(20) };

    private readonly FlareSolverrClient _flare;
    private readonly Logger             _log;

    public GbatempService(FlareSolverrClient flare, Logger log)
    {
        _flare = flare;
        _log   = log;
    }

    // -------------------------------------------------------------------------
    // Forum search

    public async Task<ForumThread?> FindThreadAsync(
        string  serial,
        string? gameName,
        int     nodeId   = 549,
        int     maxPages = 10,
        CancellationToken ct = default)
    {
        var sessionId  = await _flare.CreateSessionAsync().ConfigureAwait(false);
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
                try { resp = await _flare.GetPageAsync(pageUrl, sessionId).ConfigureAwait(false); }
                catch (Exception ex) { _log.Warn($"  Page {page} fetch failed: {ex.Message}"); continue; }

                List<ForumThread> pageThreads = ParseThreadsFromHtml(resp.Html);
                _log.Debug($"  Page {page} html={resp.Html.Length} threads={pageThreads.Count} allSoFar={allThreads.Count}");

                if (pageThreads.Count == 0) { _log.Debug($"  Page {page} returned no thread links — stopping"); break; }

                foreach (var t in pageThreads) allThreads.TryAdd(t.ThreadId, t);
                _log.Debug($"  Page {page} allThreads after add: {allThreads.Count}");

                foreach (var t in pageThreads)
                {
                    int score = ScoreThread(t, serial, gameName);
                    if (score > bestScore) { bestScore = score; bestResult = t; }
                }

                if (bestScore >= 50) { _log.Debug($"  Strong match found on page {page} — stopping early"); break; }
            }
        }
        finally { await _flare.DestroySessionAsync(sessionId).ConfigureAwait(false); }

        if (bestResult is not null && bestScore > 0)
        {
            _log.Success($"Selected thread: \"{bestResult.Title}\" (score {bestScore})");
            return bestResult;
        }

        _log.Warn($"No matching thread found in {allThreads.Count} forum entries scanned");
        return null;
    }

    private static int ScoreThread(ForumThread t, string serial, string? gameName)
    {
        int score = 0;
        if (!string.IsNullOrEmpty(serial) && t.Title.Contains(serial, StringComparison.OrdinalIgnoreCase))
            score += 100;
        if (!string.IsNullOrEmpty(gameName))
        {
            var tokens   = NormalizeText(gameName).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(tok => tok.Length >= 2);
            var titleLow = t.Title.ToLowerInvariant();
            foreach (var tok in tokens)
                if (titleLow.Contains(tok, StringComparison.Ordinal)) score += 10;
        }
        if (NoiseTitleRx().IsMatch(t.Title))   score -= 50;
        if (QualityTitleRx().IsMatch(t.Title)) score += 20;
        return score;
    }

    private static List<ForumThread> ParseThreadsFromHtml(string html)
    {
        var seen    = new HashSet<string>(StringComparer.Ordinal);
        var threads = new List<ForumThread>();
        foreach (Match m in ThreadLinkRx().Matches(html))
        {
            var id = m.Groups[2].Value;
            if (!seen.Add(id)) continue;
            var title = WebUtility.HtmlDecode(m.Groups[3].Value.Trim());
            if (string.IsNullOrWhiteSpace(title) || PaginationTitleRx().IsMatch(title)) continue;
            threads.Add(new ForumThread(id, m.Groups[1].Value, title,
                $"https://gbatemp.net/threads/{m.Groups[1].Value}.{id}/"));
        }
        return threads;
    }

    // =========================================================================
    // Download link extraction
    //
    // Strategy (newest post first; stops when any post yields links):
    //
    //   2a. Direct known-host URL patterns (MEGA, Archive, GDrive, MediaFire,
    //       Yandex, GitHub /releases/download/ direct assets).
    //   2b. GitHub REPO ROOT links (github.com/owner/repo) -> GitHub REST API
    //       for latest release -> assets + release body external links ->
    //       README fallback.
    //   2c. Unknown / short hrefs in the post -> HTTP redirect-chain resolve
    //       -> re-classify against known hosts.
    // =========================================================================

    public async Task<List<DownloadLink>> GetDownloadLinksAsync(
        string            threadUrl,
        ForumThread?      thread   = null,
        CancellationToken ct       = default)
    {
        ct.ThrowIfCancellationRequested();
        _log.Info($"Fetching thread: {threadUrl}");

        var resp = await _flare.GetPageAsync(threadUrl).ConfigureAwait(false);
        var html = resp.Html;

        var postBodies = AllPostBodiesRx()
            .Matches(html)
            .Select(m => m.Groups[1].Value)
            .Where(b => b.Length >= 50)
            .Reverse()   // newest post first
            .ToList();

        if (postBodies.Count == 0)
        {
            _log.Warn("Could not extract post bodies; scanning full page as fallback");
            postBodies.Add(html);
        }
        else _log.Debug($"  Found {postBodies.Count} post(s) (scanning newest first)");

        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<DownloadLink>();

        foreach (var body in postBodies)
        {
            ct.ThrowIfCancellationRequested();

            // 2a — direct known-host patterns
            foreach (var (hostName, pattern) in HostPatterns)
                foreach (Match hit in pattern.Matches(body))
                {
                    var url = hit.Value.TrimEnd('.', ',', ')', ']', '"', '\'');
                    if (seen.Add(url)) links.Add(new DownloadLink(hostName, url));
                }

            // 2b — GitHub repo root -> latest release via API
            foreach (Match m in GitHubRepoRootRx().Matches(body))
            {
                var repoUrl   = m.Value.TrimEnd('.', ',', ')', ']', '"', '\'', '/');
                var repoMatch = GitHubOwnerRepoRx().Match(repoUrl);
                if (!repoMatch.Success) continue;
                var owner    = repoMatch.Groups[1].Value;
                var repo     = repoMatch.Groups[2].Value;
                var cacheKey = $"gh:{owner}/{repo}";
                if (!seen.Add(cacheKey)) continue;
                _log.Debug($"  GitHub repo found — querying latest release: {owner}/{repo}");
                foreach (var rl in await ExpandGitHubRepoAsync(owner, repo, ct).ConfigureAwait(false))
                    if (seen.Add(rl.Url)) links.Add(rl);
            }

            // 2c — short / unknown hrefs: follow redirects, re-classify
            var postHrefs = AllExternalHrefRx().Matches(body)
                .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value.Trim()))
                .Where(u => u.StartsWith("http", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(u => !HostPatterns.Any(hp => hp.Pattern.IsMatch(u)) && !GitHubRepoRootRx().IsMatch(u))
                .ToList();

            if (postHrefs.Count > 0) _log.Debug($"  {postHrefs.Count} unclassified href(s) in post — resolving...");

            foreach (var rawUrl in postHrefs)
            {
                ct.ThrowIfCancellationRequested();
                var resolved = await ResolveRedirectAsync(rawUrl, ct).ConfigureAwait(false);
                if (resolved is null || resolved == rawUrl) continue;
                _log.Debug($"  Short-link: {rawUrl} -> {resolved}");
                foreach (var (hostName, pattern) in HostPatterns)
                {
                    var hm = pattern.Match(resolved);
                    if (!hm.Success) continue;
                    var url = hm.Value.TrimEnd('.', ',', ')', ']', '"', '\'');
                    if (seen.Add(url)) { _log.Debug($"    Classified as {hostName}"); links.Add(new DownloadLink(hostName, url)); }
                    break;
                }
            }

            if (links.Count > 0) break;  // stop at newest post with links
        }

        if (links.Count == 0)
        {
            _log.Warn("No download links found in any post");
            DumpMissingLinksHtml(threadUrl, thread, html);
        }
        else _log.Success($"Found {links.Count} download link(s): " + string.Join(", ", links.Select(l => l.Host)));

        return links;
    }

    // -------------------------------------------------------------------------
    // GitHub repo -> latest release expansion (API-based, no Cloudflare needed)

    private async Task<List<DownloadLink>> ExpandGitHubRepoAsync(
        string owner, string repo, CancellationToken ct)
    {
        var result = new List<DownloadLink>();

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("ps2tex/1.0");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            var apiResp = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (apiResp.IsSuccessStatusCode)
            {
                using var doc  = JsonDocument.Parse(await apiResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var       root = doc.RootElement;

                if (root.TryGetProperty("html_url", out var pageUrl))
                    _log.Debug($"    Latest release: {pageUrl.GetString()}");

                // 1. Direct release assets
                if (root.TryGetProperty("assets", out var assets))
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name  = asset.GetProperty("name").GetString() ?? "";
                        var dlUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                        var ext   = Path.GetExtension(name).ToLowerInvariant();
                        if (ext is ".zip" or ".7z" or ".rar" && !string.IsNullOrEmpty(dlUrl))
                        {
                            _log.Debug($"    Release asset: {name}");
                            result.Add(new DownloadLink("GitHub", dlUrl));
                        }
                    }

                // 2. Release body — scan for external host links
                if (root.TryGetProperty("body", out var bodyEl))
                {
                    var body = bodyEl.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        _log.Debug($"    Scanning release body for external links...");
                        result.AddRange(ScanTextForHostLinks(body, "release body"));
                    }
                }
            }
            else _log.Warn($"  GitHub API {(int)apiResp.StatusCode} for {owner}/{repo}");
        }
        catch (Exception ex) { _log.Warn($"  GitHub API error for {owner}/{repo}: {ex.Message}"); }

        // 3. README fallback
        if (result.Count == 0)
        {
            _log.Debug($"    No links from release — checking README...");
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://raw.githubusercontent.com/{owner}/{repo}/HEAD/README.md");
                req.Headers.UserAgent.ParseAdd("ps2tex/1.0");
                var r = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (r.IsSuccessStatusCode)
                    result.AddRange(ScanTextForHostLinks(
                        await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false), "README"));
            }
            catch (Exception ex) { _log.Debug($"    README fetch failed: {ex.Message}"); }
        }

        return result;
    }

    private List<DownloadLink> ScanTextForHostLinks(string text, string source)
    {
        var result = new List<DownloadLink>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (hostName, pattern) in HostPatterns)
            foreach (Match m in pattern.Matches(text))
            {
                var url = m.Value.TrimEnd('.', ',', ')', ']', '"', '\'');
                if (seen.Add(url)) { _log.Debug($"    [{source}] {hostName}: {url}"); result.Add(new DownloadLink(hostName, url)); }
            }
        return result;
    }

    // -------------------------------------------------------------------------
    // Short-link redirect resolution

    private async Task<string?> ResolveRedirectAsync(string url, CancellationToken ct)
    {
        try
        {
            var req  = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode && (int)resp.StatusCode is 403 or 405 or 503)
            {
                var get = new HttpRequestMessage(HttpMethod.Get, url);
                get.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                resp = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            return resp.RequestMessage?.RequestUri?.ToString() ?? url;
        }
        catch (Exception ex) { _log.Debug($"  Redirect resolve failed for {url}: {ex.Message}"); return null; }
    }

    // -------------------------------------------------------------------------
    // HTML dump for manual / AI triage

    private void DumpMissingLinksHtml(string threadUrl, ForumThread? thread, string fullHtml)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.MissingLinksDir);
            var slug = thread?.Slug ?? Regex.Replace(threadUrl, @"[^\w\-]", "_").TrimEnd('_');
            if (slug.Length > 80) slug = slug[..80];
            var dumpPath = Path.Combine(AppPaths.MissingLinksDir, $"{slug}.html");
            File.WriteAllText(dumpPath, fullHtml, System.Text.Encoding.UTF8);
            _log.Warn(
                "========================================================\n" +
                "  DOWNLOAD LINKS NOT FOUND - full thread HTML saved:\n" +
                $"  {dumpPath}\n" +
                "  Open this file, find the download link, then add a\n" +
                "  matching regex to GbatempService HostPatterns.\n" +
                "========================================================");
        }
        catch (Exception ex) { _log.Warn($"  Could not write missing-links dump: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // Helpers

    private static string NormalizeText(string s)
        => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    // ---- compiled regexes ----

    [GeneratedRegex("""href="/threads/([A-Za-z0-9\-_.]+)\.(\d+)/"\s[^>]*>([^<]+)</a>""")]
    private static partial Regex ThreadLinkRx();

    [GeneratedRegex(@"^(Page \d+|#\d+|Last)$", RegexOptions.IgnoreCase)]
    private static partial Regex PaginationTitleRx();

    // All post bodies in a thread page — each match.Groups[1] is one post.
    [GeneratedRegex("""<div\s+class="bbWrapper">(.*?)</div>\s*(?=<(?:div|aside|footer|article))""", RegexOptions.Singleline)]
    private static partial Regex AllPostBodiesRx();

    [GeneratedRegex(@"\b(request|dump|help|wanted|looking\s+for|need|question)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseTitleRx();

    [GeneratedRegex(@"\b(hd|upscaled|texture|remaster|pack|replacement|4k|2k)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTitleRx();

    [GeneratedRegex(@"https?://mega\.nz/(?:file|folder)/[A-Za-z0-9#!_\-]+")]
    private static partial Regex MegaRx();

    [GeneratedRegex(@"https?://archive\.org/(?:details|download)/[^\s""'<>]+")]
    private static partial Regex ArchiveRx();

    [GeneratedRegex(@"https?://(?:drive|docs)\.google\.com/(?:file/d/|open\?id=|uc\?[^""'<>\s]*id=)[A-Za-z0-9_\-]+")]
    private static partial Regex GDriveRx();

    // MediaFire — URL-encoded filenames contain %28 %29 etc.
    [GeneratedRegex(@"https?://(?:www\.)?mediafire\.com/(?:file|folder)/[A-Za-z0-9_\-/?&=.%+]+")]
    private static partial Regex MediaFireRx();

    [GeneratedRegex(@"https?://disk\.yandex\.(?:ru|com)/d/[A-Za-z0-9_\-]+")]
    private static partial Regex YandexRx();

    // GitHub direct release asset download (already-resolved /releases/download/ URL)
    [GeneratedRegex(@"https?://github\.com/[^/\s""'<>]+/[^/\s""'<>]+/releases/download/[^\s""'<>]+")]
    private static partial Regex GitHubReleasesRx();

    // GitHub repo ROOT — matches github.com/owner/repo with optional trailing path
    [GeneratedRegex(@"https?://github\.com/([A-Za-z0-9\-_.]+)/([A-Za-z0-9\-_.]+)(?:/(?:tree|blob)/[^\s""'<>]*|\.git|/?)?(?=[\s""'<>.]|$)")]
    private static partial Regex GitHubRepoRootRx();

    // Extract owner/repo from any github.com URL
    [GeneratedRegex(@"github\.com/([A-Za-z0-9\-_.]+)/([A-Za-z0-9\-_.]+)")]
    private static partial Regex GitHubOwnerRepoRx();

    // All external hrefs within a post body
    [GeneratedRegex("""href=["'](https?://[^"'<>\s]+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex AllExternalHrefRx();
}
