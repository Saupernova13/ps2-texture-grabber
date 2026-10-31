using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ps2TextureGrabber.Models;

namespace Ps2TextureGrabber.Services;

/// <summary>
/// Browses the GBAtemp PCSX2 HD Texture Pack subforum, scores thread titles against a
/// serial/game-name query, and extracts download links from the winning thread.
///
/// Discovery order:
///   1. Local thread cache (JSON, built up over time).
///   2. RSS feed - plain HTTP, no Playwright. The ~40 most recently active threads.
///   3. Playwright forum-page scrape, only when the cache has no confident match.
/// Both 2 and 3 merge into the cache, so later searches can skip them.
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

    private readonly PlaywrightFetcher _flare;
    private readonly Logger            _log;

    public GbatempService(PlaywrightFetcher flare, Logger log)
    {
        _flare = flare;
        _log   = log;
    }

    // -------------------------------------------------------------------------
    // Thread discovery

    /// <summary>
    /// Candidate threads by score, descending, all within 10 points of the best - so the
    /// caller can try each in turn when the top thread has no download links.
    /// </summary>
    public async Task<List<ForumThread>> FindThreadCandidatesAsync(
        string  serial,
        string? gameName,
        int     nodeId    = 549,
        int     maxPages  = 5,
        string? userQuery = null,
        CancellationToken ct = default)
    {
        var candidates = await DiscoverScoredThreadsAsync(
            serial, gameName, nodeId, maxPages, userQuery, ct).ConfigureAwait(false);
        if (candidates.Count == 0) return [];

        int best = candidates[0].Score;
        var result = candidates
            .Where(x => x.Score >= best - 10)
            .Select(x => x.Thread)
            .ToList();

        _log.Success($"Selected thread: \"{result[0].Title}\" (score {best})");
        if (result.Count > 1)
            _log.Info($"  {result.Count - 1} fallback candidate(s) within 10 pts");

        return result;
    }

    /// <summary>
    /// Every matching thread with its score, best first. Unlike FindThreadCandidatesAsync
    /// nothing is trimmed to the top band - a search UI can show the whole result set.
    /// </summary>
    public Task<List<(ForumThread Thread, int Score)>> SearchThreadsAsync(
        string  serial,
        string? gameName,
        int     nodeId    = 549,
        int     maxPages  = 5,
        string? userQuery = null,
        CancellationToken ct = default)
        => DiscoverScoredThreadsAsync(serial, gameName, nodeId, maxPages, userQuery, ct);

    /// <summary>
    /// Every thread on the forum node - all pages, not just the RSS window or the first few.
    /// The last page number is read off page 1's page nav, so the listing is complete even
    /// for large forums. Everything found merges into the thread cache.
    ///
    /// A listing that was fully scraped less than ListingCacheWindow ago is served straight
    /// from the cache - re-scraping 30+ forum pages every time is slow for no gain. Pass
    /// refresh: true to force a live scrape regardless.
    /// </summary>
    public async Task<List<ForumThread>> ListAllThreadsAsync(
        int nodeId, bool refresh = false, CancellationToken ct = default)
    {
        var cacheFile = ThreadCacheFile(nodeId);
        var cacheAge  = File.Exists(cacheFile)
            ? (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile))
            : (TimeSpan?)null;

        if (!refresh && cacheAge is not null && cacheAge < ListingCacheWindow)
        {
            var cached = LoadThreadCache(nodeId);
            if (cached.Count > 0)
            {
                _log.Success(
                    $"Using thread listing from {Math.Max(1, (int)cacheAge.Value.TotalMinutes)} minute(s) ago " +
                    $"({cached.Count} thread(s)). Use --refresh to re-scrape the forum.");
                return cached.Values
                    .OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        var cache = LoadThreadCache(nodeId);
        var rss   = await FetchRssThreadsAsync(nodeId, ct).ConfigureAwait(false);
        MergeIntoCache(cache, rss);

        var page1 = await FetchForumPageAsync(nodeId, 1, ct).ConfigureAwait(false);
        MergeIntoCache(cache, page1.Threads);

        int totalPages = ParseLastPageNumber(page1.Html);
        if (totalPages > MaxBrowsePages)
        {
            _log.Warn($"Listing capped at {MaxBrowsePages} of {totalPages} page(s)");
            totalPages = MaxBrowsePages;
        }

        // Sequential on purpose: fetches are paced, so parallel pages buy nothing,
        // and a sequential loop can bail out the moment Cloudflare turns hostile.
        if (totalPages > 1)
        {
            _log.Info($"Forum has {totalPages} page(s) — scraping the rest");
            int consecutiveFailures = 0;
            for (int page = 2; page <= totalPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await FetchForumPageAsync(nodeId, page, ct).ConfigureAwait(false);
                if (result.Threads.Count == 0)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _log.Warn(
                            $"{consecutiveFailures} forum pages in a row failed — Cloudflare is " +
                            "blocking further requests. This usually clears within 15-30 minutes. " +
                            "The listing so far has been kept.");
                        break;
                    }
                    continue;
                }
                consecutiveFailures = 0;
                MergeIntoCache(cache, result.Threads);
            }
        }

        SaveThreadCache(nodeId, cache);
        _log.Success($"{cache.Count} thread(s) known for node {nodeId}");
        return cache.Values
            .OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Sane upper bound on pages scraped by --browse; a misparsed page nav
    /// cannot turn the listing into a crawl of thousands of pages.</summary>
    private const int MaxBrowsePages = 500;

    /// <summary>Consecutive failed page fetches before the scrape gives up - a few in
    /// a row means a Cloudflare block, not bad luck.</summary>
    private const int MaxConsecutiveFailures = 3;

    /// <summary>How long a scraped listing counts as fresh before --browse re-scrapes.</summary>
    private static readonly TimeSpan ListingCacheWindow = TimeSpan.FromHours(12);

    /// <summary>Shared discovery pipeline: thread cache -> RSS -> (maybe) forum scrape,
    /// returning every match scored, best first.</summary>
    private async Task<List<(ForumThread Thread, int Score)>> DiscoverScoredThreadsAsync(
        string  serial,
        string? gameName,
        int     nodeId,
        int     maxPages,
        string? userQuery,
        CancellationToken ct)
    {
        var keywords = !string.IsNullOrWhiteSpace(userQuery)
            ? userQuery.Trim()
            : BuildSearchKeywords(serial, gameName);

        if (string.IsNullOrWhiteSpace(keywords))
        {
            _log.Warn("No search keywords available");
            return [];
        }

        // 1. Load the thread cache built up from previous runs.
        var cache = LoadThreadCache(nodeId);
        _log.Debug($"  Thread cache: {cache.Count} known thread(s)");

        // 2. Refresh via RSS — plain HTTP, no Playwright required. Cloudflare usually
        //    403s it; the page-1 refresh in step 4 then covers the newest threads.
        var rssThreads = await FetchRssThreadsAsync(nodeId, ct).ConfigureAwait(false);
        bool cacheUpdated = MergeIntoCache(cache, rssThreads);

        // 3. Score everything in the cache.
        var candidates = ScoreAndFilter(cache.Values, serial, gameName, userQuery);
        int bestScore = candidates.Count > 0 ? candidates[0].Score : 0;

        // 4. If nothing confident found, scrape forum pages via Playwright.
        if (bestScore < 20)
        {
            _log.Info($"Cache/RSS miss (best score {bestScore}) — scraping forum pages");
            var scraped = await BrowseForumPagesAsync(nodeId, maxPages, ct).ConfigureAwait(false);
            bool added  = MergeIntoCache(cache, scraped);
            cacheUpdated |= added;
            candidates = ScoreAndFilter(cache.Values, serial, gameName, userQuery);
        }
        else if (rssThreads.Count == 0)
        {
            // RSS did not deliver: pull page 1 through the browser instead. It lists the
            // most recently active threads, so brand-new posts are not missed even when
            // the cache already holds a confident match.
            var page1 = await FetchForumPageAsync(nodeId, 1, ct).ConfigureAwait(false);
            if (MergeIntoCache(cache, page1.Threads))
            {
                cacheUpdated = true;
                candidates   = ScoreAndFilter(cache.Values, serial, gameName, userQuery);
            }
        }

        // 5. Persist any newly discovered threads for future searches.
        if (cacheUpdated)
            SaveThreadCache(nodeId, cache);

        if (candidates.Count == 0)
        {
            _log.Warn($"No matching threads among {cache.Count} known thread(s)");
            return [];
        }

        _log.Info($"Searched {cache.Count} known GBAtemp thread(s) — {candidates.Count} match(es)");

        foreach (var (t, score) in candidates.Take(10))
            _log.Debug($"    score={score,4} {t.Title}");

        return candidates;
    }

    // -------------------------------------------------------------------------
    // RSS feed (plain HTTP, no Playwright)

    private async Task<List<ForumThread>> FetchRssThreadsAsync(int nodeId, CancellationToken ct)
    {
        var url = $"https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.{nodeId}/index.rss";
        _log.Info($"Fetching GBAtemp RSS (node {nodeId})");

        try
        {
            // The RSS endpoint sits behind the same Cloudflare edge as the forum and 403s
            // the default .NET UA.
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/135.0.0.0 Safari/537.36");
            req.Headers.Accept.ParseAdd("application/rss+xml, application/xml, text/xml, */*");
            req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var xml     = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc     = XDocument.Parse(xml);
            var results = new List<ForumThread>();

            foreach (var item in doc.Descendants("item"))
            {
                var title = item.Element("title")?.Value?.Trim();
                var link  = item.Element("link")?.Value?.Trim();
                var guid  = item.Element("guid")?.Value?.Trim();

                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(link)) continue;

                var m    = ThreadUrlRx().Match(link);
                var slug = m.Success ? m.Groups[1].Value : "";
                var id   = m.Success ? m.Groups[2].Value : (guid ?? "");
                if (string.IsNullOrEmpty(id)) continue;

                results.Add(new ForumThread(id, slug, WebUtility.HtmlDecode(title), link));
            }

            _log.Debug($"  RSS: {results.Count} thread(s)");
            return results;
        }
        catch (Exception ex)
        {
            // Non-fatal: the caller refreshes page 1 via the browser or falls through to
            // the forum scrape, both of which have the browser's Cloudflare clearance.
            _log.Warn($"  RSS fetch failed ({ex.Message}) — will rely on cache + browser scrape");
            return [];
        }
    }

    // -------------------------------------------------------------------------
    // Thread cache (JSON, persisted across runs)

    private static string ThreadCacheFile(int nodeId)
        => Path.Combine(AppPaths.CacheDir, $"gbatemp-threads-{nodeId}.json");

    private static readonly JsonSerializerOptions _cacheJsonOpts = new()
    {
        WriteIndented          = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static Dictionary<string, ForumThread> LoadThreadCache(int nodeId)
    {
        var path = ThreadCacheFile(nodeId);
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ForumThread>>(
                       File.ReadAllText(path), _cacheJsonOpts)
                   ?? new(StringComparer.Ordinal);
        }
        catch { return new(StringComparer.Ordinal); }
    }

    private static void SaveThreadCache(int nodeId, Dictionary<string, ForumThread> cache)
    {
        Directory.CreateDirectory(AppPaths.CacheDir);
        File.WriteAllText(ThreadCacheFile(nodeId),
            JsonSerializer.Serialize(cache, _cacheJsonOpts));
    }

    /// <summary>Merges threads into cache by ThreadId. Returns true if any new entries were added.</summary>
    private static bool MergeIntoCache(
        Dictionary<string, ForumThread> cache, IEnumerable<ForumThread> threads)
    {
        bool added = false;
        foreach (var t in threads)
            if (!string.IsNullOrEmpty(t.ThreadId) && cache.TryAdd(t.ThreadId, t))
                added = true;
        return added;
    }

    // -------------------------------------------------------------------------
    // Scoring

    private List<(ForumThread Thread, int Score)> ScoreAndFilter(
        IEnumerable<ForumThread> threads, string serial, string? gameName, string? userQuery)
    {
        // Anchor on what the user asked for, falling back to the resolved game name. Numbers
        // from EITHER source count, so a sequel number is enforced even when the user omitted it.
        var anchorSource = !string.IsNullOrWhiteSpace(userQuery)
            ? userQuery
            : (gameName ?? string.Empty);
        var anchorTokens    = SignificantTokens(anchorSource);
        var requiredNumbers = anchorTokens.Where(IsNumberToken)
            .Concat(SignificantTokens(gameName ?? string.Empty).Where(IsNumberToken))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return threads
            .Select(t => (Thread: t, Score: ScoreThread(t, serial, gameName)))
            .Where(x => TitleMatchesGame(x.Thread.Title, serial, anchorTokens, requiredNumbers))
            .OrderByDescending(x => x.Score)
            .ToList();
    }

    private static int ScoreThread(ForumThread t, string serial, string? gameName)
    {
        int score = 0;
        if (!string.IsNullOrEmpty(serial) && t.Title.Contains(serial, StringComparison.OrdinalIgnoreCase))
            score += 100;
        if (!string.IsNullOrEmpty(gameName))
        {
            var titleTokens = new HashSet<string>(Tokenize(t.Title), StringComparer.Ordinal);
            foreach (var tok in SignificantTokens(gameName))
                if (titleTokens.Contains(tok)) score += 10;
        }
        if (NoiseTitleRx().IsMatch(t.Title))   score -= 50;
        if (QualityTitleRx().IsMatch(t.Title)) score += 20;
        return score;
    }

    // -------------------------------------------------------------------------
    // Game-identity gate
    //
    // A thread is eligible only if its title plausibly refers to the resolved game. Without
    // this, a "Baroque HD Texture Pack" thread scored on generic quality words alone and won a
    // "Persona 3" query. Ranking still uses ScoreThread; this only decides eligibility.

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        { "the", "of", "a", "an", "and", "to", "in", "for", "vs", "de", "la" };

    private static readonly Dictionary<string, string> RomanToArabic = new(StringComparer.Ordinal)
    {
        ["i"] = "1", ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5",
        ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9", ["x"] = "10",
    };

    /// <summary>Lowercase tokens; standalone roman numerals mapped to arabic so
    /// "Persona III" and "Persona 3" tokenize identically.</summary>
    private static List<string> Tokenize(string s)
    {
        var raw = NormalizeText(s).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outp = new List<string>(raw.Length);
        foreach (var t in raw)
            outp.Add(RomanToArabic.TryGetValue(t, out var a) ? a : t);
        return outp;
    }

    private static bool IsNumberToken(string t) => t.Length > 0 && t.All(char.IsDigit);

    /// <summary>Tokens minus stopwords; numbers kept (they discriminate sequels).</summary>
    private static List<string> SignificantTokens(string s)
        => Tokenize(s).Where(t => !StopWords.Contains(t)).Distinct(StringComparer.Ordinal).ToList();

    private static bool TitleMatchesGame(
        string title, string serial, List<string> anchorTokens, List<string> requiredNumbers)
    {
        if (!string.IsNullOrEmpty(serial)
            && title.Contains(serial, StringComparison.OrdinalIgnoreCase))
            return true;

        if (anchorTokens.Count == 0) return false;

        var titleTokens = new HashSet<string>(Tokenize(title), StringComparer.Ordinal);

        // Every number the query/game names must be present (Persona 3 != 4/FES).
        foreach (var n in requiredNumbers)
            if (!titleTokens.Contains(n)) return false;

        var distinctive = anchorTokens.Where(t => t.Length >= 3 && !IsNumberToken(t)).ToList();
        if (distinctive.Count == 0)
            // Anchor is only short/numeric tokens — require all of them present.
            return anchorTokens.All(titleTokens.Contains);

        int matched = distinctive.Count(titleTokens.Contains);
        if (matched == 0) return false;

        // Two+ distinctive words is strong on its own. A single shared word ("god" in both
        // "God of War" and "God Hand") only counts when a sequel number also matched, or the
        // game name is a single word.
        return matched >= 2
            || requiredNumbers.Count > 0
            || matched == distinctive.Count;
    }

    // -------------------------------------------------------------------------
    // Forum listing pagination via Playwright (sequential; fetches are paced).

    private async Task<List<ForumThread>> BrowseForumPagesAsync(
        int nodeId, int maxPages, CancellationToken ct)
    {
        _log.Info($"Browsing GBAtemp forum (node {nodeId}), pages 1–{maxPages}");

        // Fetches are paced, so parallel pages buy nothing; a sequential loop can
        // bail out the moment Cloudflare turns hostile instead of burning timeouts.
        var all = new List<ForumThread>();
        int consecutiveFailures = 0;
        for (int page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await FetchForumPageAsync(nodeId, page, ct).ConfigureAwait(false);
            if (result.Threads.Count == 0)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _log.Warn($"{consecutiveFailures} forum pages in a row failed — Cloudflare " +
                              "is blocking further requests; stopping the scrape.");
                    break;
                }
                continue;
            }
            consecutiveFailures = 0;
            all.AddRange(result.Threads);
        }

        _log.Debug($"  Forum scrape total: {all.Count} thread(s)");
        return all;
    }

    private async Task<(List<ForumThread> Threads, string Html)> FetchForumPageAsync(
        int nodeId, int page, CancellationToken ct)
    {
        var url = page == 1
            ? $"https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.{nodeId}/"
            : $"https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.{nodeId}/page-{page}";

        _log.Debug($"  Loading forum page {page}: {url}");
        try
        {
            var listing = await _flare.GetPageAsync(url).ConfigureAwait(false);
            var threads = ParseForumListingThreads(listing.Html);
            _log.Debug($"  Page {page}: {threads.Count} thread(s)");
            return (threads, listing.Html);
        }
        catch (Exception ex)
        {
            _log.Warn($"  Forum page {page} fetch failed: {ex.Message}");
            return ([], "");
        }
    }

    /// <summary>Highest page number found in a forum listing's page nav (XenForo's compact
    /// nav always links the last page). Defaults to 1 when no pagination is present.</summary>
    private static int ParseLastPageNumber(string html)
    {
        int last = 1;
        foreach (Match m in PageNumberRx().Matches(html))
            if (int.TryParse(m.Groups[1].Value, out var n) && n > last)
                last = n;
        return last;
    }

    private static List<ForumThread> ParseForumListingThreads(string html)
    {
        var results = new List<ForumThread>();
        var seen    = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in ForumListingThreadRx().Matches(html))
        {
            var slug  = m.Groups[1].Value;
            var id    = m.Groups[2].Value;
            if (!seen.Add(id)) continue;

            var raw   = m.Groups[3].Value;
            var title = WebUtility.HtmlDecode(StripHtmlTagsRx().Replace(raw, string.Empty)).Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;

            results.Add(new ForumThread(id, slug, title, $"https://gbatemp.net/threads/{slug}.{id}/"));
        }

        return results;
    }

    private static string BuildSearchKeywords(string serial, string? gameName)
    {
        if (!string.IsNullOrWhiteSpace(gameName))
        {
            var cleaned = Regex.Replace(gameName, @"\s*\([^)]*\)\s*", " ").Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? gameName.Trim() : cleaned;
        }
        return serial?.Trim() ?? string.Empty;
    }

    // =========================================================================
    // Download link extraction
    //
    // Newest post first, stopping at the first post that yields links:
    //
    //   2a. Direct known-host URLs (MEGA, Archive, GDrive, MediaFire, Yandex, GitHub
    //       /releases/download/ assets).
    //   2b. GitHub repo roots -> REST API for the latest release -> assets, release-body
    //       external links, then README.
    //   2c. Unknown or short hrefs -> resolve the redirect chain -> re-classify.
    // =========================================================================

    public async Task<List<DownloadLink>> GetDownloadLinksAsync(
        string            threadUrl,
        ForumThread?      thread   = null,
        bool              dumpMissingHtml = true,
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
            if (dumpMissingHtml)
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

    // Thread links in a XenForo forum listing page (structItem-title).
    [GeneratedRegex("""class="[^"]*structItem-title[^"]*"[^>]*>(?:\s*<[^>]+>)*\s*<a\s+href="/threads/([A-Za-z0-9\-_.]+)\.(\d+)/[^"]*"[^>]*>(.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex ForumListingThreadRx();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex StripHtmlTagsRx();

    // All post bodies in a thread page — each match.Groups[1] is one post.
    [GeneratedRegex("""<div\s+class="bbWrapper">(.*?)</div>\s*(?=<(?:div|aside|footer|article))""", RegexOptions.Singleline)]
    private static partial Regex AllPostBodiesRx();

    [GeneratedRegex(@"\b(request|dump|help|wanted|looking\s+for|need|question)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseTitleRx();

    [GeneratedRegex(@"\b(hd|upscaled|texture|remaster|pack|replacement|4k|2k)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityTitleRx();

    // Thread URL slug and ID from an RSS <link> or forum href
    [GeneratedRegex(@"/threads/([A-Za-z0-9\-_.]+)\.(\d+)/")]
    private static partial Regex ThreadUrlRx();

    // Page numbers in a forum listing's page nav (hrefs like "/page-12").
    [GeneratedRegex(@"/page-(\d+)")]
    private static partial Regex PageNumberRx();

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
