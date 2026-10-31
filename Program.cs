using System.Reflection;
using System.Text.Json;
using Ps2TextureGrabber;
using Ps2TextureGrabber.Models;
using Ps2TextureGrabber.Services;
using Ps2TextureGrabber.Worker;

// ============================================================================
//  ps2tex — top-level argument dispatch
//
//    ps2tex --query "Dragon Ball Z Budokai Tenkaichi 3" [options]
//    ps2tex --search "<query>"       all matches + links, download nothing
//    ps2tex --browse                 every GBAtemp thread, download nothing
//    ps2tex --list | --status <jobId> | --clean [--all] [--dry-run] | --version
//    ps2tex worker --job-file <path>       ← internal; spawned by JobService
//
//  See README.md for the option list.
// ============================================================================

AppPaths.EnsureAll();

var cli = Args.Parse(Environment.GetCommandLineArgs()[1..]);

// ---- --version (answered before anything else; bug reports start here) ----
if (cli.Version)
{
    var asm = Assembly.GetExecutingAssembly();
    var ver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
              ?? asm.GetName().Version?.ToString()
              ?? "unknown";
    // Strip the source-revision suffix the SDK appends (e.g. "1.2.3+abc1234").
    var plus = ver.IndexOf('+');
    Console.WriteLine($"ps2tex {(plus > 0 ? ver[..plus] : ver)}");
    return 0;
}

// ---- worker subcommand (internal, no user-facing output) ----
if (cli.Command == "worker")
{
    if (string.IsNullOrEmpty(cli.JobFile))
    {
        Console.Error.WriteLine("worker: --job-file required");
        return 1;
    }
    await WorkerRunner.RunAsync(cli.JobFile);
    return 0;
}

// ---- load .settings; command-line args always win ----
var settings = SettingsFile.Load(AppPaths.SettingsFile);
string Get(string key, string? argVal, string? defaultVal = null)
    => argVal ?? settings.GetValueOrDefault(key) ?? defaultVal ?? string.Empty;

var texturesPath     = Get("TexturesPath",     cli.TexturesPath,     Pcsx2Paths.Textures);
var gamesettingsPath = Get("GamesettingsPath", cli.GamesettingsPath, Pcsx2Paths.GameSettings);
var gameIndexPath    = Get("GameIndexPath",    cli.GameIndexPath,    Pcsx2Paths.GameIndex);
var nodeId           = cli.NodeId > 0 ? cli.NodeId
    : (settings.TryGetValue("GbatempNodeId", out var nStr) && int.TryParse(nStr, out var n) ? n : 549);

var log = new Logger();

// Resolves a query to a GameEntry: locally installed games first, then GameIndex.yaml,
// then wiki.pcsx2.net as a last resort. Shared by --query and --search.
async Task<GameEntry?> ResolveQueryAsync(string query, bool interactive)
{
    var gameDbSvc = new GameDbService(log);
    var gameDb    = gameDbSvc.Load(gameIndexPath, AppPaths.GameDbCache);

    // Prefer locally installed game — check gamesettings INIs and texture folders
    // so that e.g. "The Sims 2" resolves to the user's PAL serial, not the US default.
    var pcsx2Svc     = new Pcsx2Service(log);
    var localSerials = pcsx2Svc.GetLocalGameSerials(gamesettingsPath, texturesPath);

    if (localSerials.Count > 0)
    {
        var localDb = gameDb.Where(e => localSerials.Contains(e.Serial)).ToList();
        GameEntry? local = null;
        if (localDb.Count > 0)
            local = gameDbSvc.Resolve(localDb, query, interactive);
        if (local is not null)
        {
            log.Success($"[LOCAL] Matched to locally installed game {local.Serial} ({local.DisplayName}, {local.Region})");
            return local;
        }
    }

    // Fall back to full GameIndex.yaml search (region-biased toward NTSC-U)
    var entry = gameDbSvc.Resolve(gameDb, query, interactive);
    if (entry is not null) return entry;

    log.Warn($"No local GameDB match for '{query}' — trying wiki.pcsx2.net...");
    var wikiSerial = await new WikiService(log, AppPaths.WikiCacheDir)
        .GetSerialForNameAsync(query);
    if (wikiSerial is not null)
    {
        var bySerial = gameDbSvc.FindBySerial(gameDb, wikiSerial)
            ?? new GameEntry(wikiSerial, query, query, null);
        log.Success($"[WIKI] Resolved '{query}' -> {wikiSerial}");
        return bySerial;
    }
    return null;
}

// ============================================================================
//  --clean
//  Placed before --status/--list/--query: it carries no job id and no query.
// ============================================================================
if (cli.Clean)
{
    var cleaner = new CleanService(log, AppPaths.JobsDir, AppPaths.CacheDir);
    var cleanResult = cleaner.Run(includeJobs: cli.All, dryRun: cli.DryRun);
    var total   = cleanResult.Removed.Sum(t => t.Bytes) + cleanResult.Failed.Sum(t => t.Bytes);

    if (cli.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            removed = cleanResult.Removed.Select(t => t.Path),
            failed  = cleanResult.Failed.Select(t => t.Path),
            bytes   = cleanResult.Bytes,
            dryRun  = cleanResult.DryRun,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return cleanResult.Failed.Count > 0 ? 1 : 0;
    }

    if (cleanResult.Removed.Count == 0 && cleanResult.Failed.Count == 0)
    {
        log.Success("Nothing to clean - ps2tex is holding no working dirs or caches.");
        return 0;
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(cleanResult.DryRun ? "Would remove:" : "Removing:");
    Console.ResetColor();
    Console.WriteLine();

    foreach (var t in cleanResult.Removed.Concat(cleanResult.Failed))
    {
        var ok = !cleanResult.Failed.Contains(t);
        Console.ForegroundColor = ok ? ConsoleColor.Gray : ConsoleColor.Red;
        Console.WriteLine($"{(ok ? ' ' : '!')} {t.Kind,-6} {CleanService.FormatBytes(t.Bytes),10}  {t.Path}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"           {t.Note}");
        Console.ResetColor();
    }
    Console.WriteLine();

    if (cleanResult.DryRun)
    {
        log.Info($"{cleanResult.Removed.Count} item(s), {CleanService.FormatBytes(total)} - dry run, nothing was deleted.");
        log.Info("Re-run without --dry-run to remove them.");
    }
    else
    {
        log.Success($"Removed {cleanResult.Removed.Count} item(s), freed {CleanService.FormatBytes(cleanResult.Bytes)}.");
        if (cleanResult.Failed.Count > 0)
            log.Warn($"{cleanResult.Failed.Count} item(s) could not be removed (in use?).");
    }
    if (!cli.All)
        log.Info("Job history was kept. Add --all to clear finished job records and logs too.");

    return cleanResult.Failed.Count > 0 ? 1 : 0;
}

// ============================================================================
//  --status
// ============================================================================
if (!string.IsNullOrEmpty(cli.Status))
{
    var jobs = new JobService(log, AppPaths.JobsDir);
    if (cli.Json)
        Console.WriteLine(jobs.ReadStatusJson(cli.Status));
    else
        jobs.PrintStatus(cli.Status);
    return 0;
}

// ============================================================================
//  --list
// ============================================================================
if (cli.List)
{
    var dbSvc = new GameDbService(log);
    var pcsx2 = new Pcsx2Service(log);
    var db    = dbSvc.Load(gameIndexPath, AppPaths.GameDbCache);
    var packs = pcsx2.GetInstalledPacks(texturesPath, gamesettingsPath, db);

    if (cli.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(packs,
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    if (packs.Count == 0)
    {
        log.Warn($"No texture packs found under {texturesPath}");
        return 0;
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Installed PS2 texture packs:");
    Console.WriteLine();
    Console.ResetColor();

    const int w1 = 12, w2 = 40, w3 = 8, w4 = 8, w5 = 13;
    Console.WriteLine(
        $"{"Serial",-w1}  {"Game",-w2}  {"Textures",w3}  {"MB",w4}  {"INI OK?",w5}");
    Console.WriteLine(new string('-', w1 + w2 + w3 + w4 + w5 + 8));
    foreach (var p in packs)
    {
        Console.WriteLine(
            $"{p.Serial,-w1}  {p.GameName[..Math.Min(p.GameName.Length, w2)],-w2}  " +
            $"{p.TextureCount,w3}  {p.SizeMb,w4:F1}  {p.IniConfigured,w5}");
    }
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Total: {packs.Count} pack(s)");
    Console.ResetColor();
    return 0;
}

// ============================================================================
//  --browse  (list every thread on the GBAtemp forum, download nothing)
// ============================================================================
if (cli.Browse)
{
    log.Info($"Listing every thread on GBAtemp node {nodeId} — nothing is downloaded");

    await using var fetcher = new PlaywrightFetcher(log);
    var gbatemp = new GbatempService(fetcher, log);
    var threads = await gbatemp.ListAllThreadsAsync(nodeId, cli.Refresh);

    if (threads.Count == 0)
    {
        log.Warn("No threads found");
        return 1;
    }

    if (cli.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            nodeId,
            threadCount = threads.Count,
            threads = threads.Select(t => new { threadId = t.ThreadId, slug = t.Slug, title = t.Title, url = t.Url }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"GBAtemp PCSX2 HD Texture Pack forum (node {nodeId}) — {threads.Count} thread(s):");
    Console.WriteLine();
    Console.ResetColor();
    for (int i = 0; i < threads.Count; i++)
    {
        Console.WriteLine($"{i + 1,4}. {threads[i].Title}");
        Console.WriteLine($"       {threads[i].Url}");
    }
    Console.WriteLine();
    return 0;
}

// ============================================================================
//  --search  (every match + link details, download nothing)
// ============================================================================
if (!string.IsNullOrEmpty(cli.Search))
{
    var query = cli.Search;
    log.Info($"Searching GBAtemp for '{query}' — nothing is downloaded");

    var match = await ResolveQueryAsync(query, cli.Interactive);
    if (match is null)
    {
        log.Error($"Could not resolve '{query}' to a PS2 serial");
        return 1;
    }
    log.Success($"Searching GBAtemp for {match.DisplayName} [{match.Serial}]");
    var matchName = match.DisplayName;

    // Bonus: the Archive.org index is checked first during a real download, so surface
    // a hit here too. Cached, so this costs nothing extra.
    var archiveHit = await new ArchiveOrgIndexService(log, AppPaths.ArchiveIndexCache)
        .FindBySerialAsync(match.Serial);
    if (archiveHit is not null)
        log.Info($"Archive.org index hit: {archiveHit.Url}");

    await using var fetcher = new PlaywrightFetcher(log);
    var gbatemp = new GbatempService(fetcher, log);
    var scored = await gbatemp.SearchThreadsAsync(match.Serial, matchName, nodeId, userQuery: query);

    if (scored.Count == 0)
    {
        log.Warn($"No matching threads on GBAtemp for '{matchName}' [{match.Serial}]");
        log.Info($"Browse manually: https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.{nodeId}/");
        return 1;
    }

    // Pull the download links out of every matching thread so each result shows its
    // details. Only the thread pages are fetched; no file is downloaded.
    log.Info($"Fetching download links for {scored.Count} matching thread(s)...");
    var sem = new SemaphoreSlim(3, 3);
    var details = await Task.WhenAll(scored.Select(async s =>
    {
        await sem.WaitAsync();
        try
        {
            // dumpMissingHtml: false - a search is not a failed download, so no
            // triage dumps land in the missing-links cache.
            return (s.Thread, s.Score,
                Links: await gbatemp.GetDownloadLinksAsync(s.Thread.Url, s.Thread,
                    dumpMissingHtml: false));
        }
        catch (Exception ex)
        {
            // A Cloudflare block on one thread must not sink the whole search;
            // report it and show the rest of the results.
            log.Warn($"Could not read thread \"{s.Thread.Title}\": {ex.Message}");
            return (s.Thread, s.Score, Links: new List<DownloadLink>());
        }
        finally { sem.Release(); }
    }));

    var results = details
        .Select(d => new
        {
            title = d.Thread.Title,
            url   = d.Thread.Url,
            score = d.Score,
            links = d.Links.Select(l => new { host = l.Host, url = l.Url }).ToList(),
        })
        .ToList();

    if (cli.Json)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            query,
            serial     = match.Serial,
            game       = matchName,
            region     = match.Region,
            archiveOrg = archiveHit?.Url,
            results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"Matches for \"{query}\" -> {matchName} [{match.Serial}] ({match.Region}) — {results.Count} result(s):");
    Console.WriteLine();
    Console.ResetColor();
    for (int i = 0; i < results.Count; i++)
    {
        var r = results[i];
        Console.WriteLine($"{i + 1,4}. {r.title}  (score {r.score})");
        Console.WriteLine($"       {r.url}");
        if (r.links.Count == 0)
        {
            Console.WriteLine("       No download links found");
        }
        else
        {
            Console.WriteLine($"       {r.links.Count} link(s):");
            foreach (var l in r.links)
                Console.WriteLine($"         {l.host,-9} {l.url}");
        }
        Console.WriteLine();
    }
    return 0;
}

// ============================================================================
//  --query  (download)
// ============================================================================
if (string.IsNullOrEmpty(cli.Query))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine(
        "Missing argument.  Usage:\n" +
        "  ps2tex --query \"God of War\"\n" +
        "  ps2tex --search \"God of War\"   (all matches + links, nothing downloaded)\n" +
        "  ps2tex --browse                 (every GBAtemp thread, nothing downloaded)\n" +
        "  ps2tex --list\n" +
        "  ps2tex --status <jobId>\n" +
        "  ps2tex --clean [--all] [--dry-run]");
    Console.ResetColor();
    return 1;
}

log.Info("Starting texture grabber");
log.Info($"  Query:        {cli.Query}");
log.Debug($"  Textures:     {texturesPath}");
log.Debug($"  Gamesettings: {gamesettingsPath}");

// 1. Resolve name -> serial (local install first, then GameIndex.yaml, then wiki)
var entry = await ResolveQueryAsync(cli.Query, cli.Interactive);
if (entry is null)
{
    log.Error($"Could not resolve '{cli.Query}' to a PS2 serial");
    return 1;
}

var gameName = entry.DisplayName;

// 2. Check Archive.org index first (no browser required)
var archiveLink = await new ArchiveOrgIndexService(log, AppPaths.ArchiveIndexCache)
    .FindBySerialAsync(entry.Serial);

List<DownloadLink> links       = [];
string?            threadUrl   = null;
string?            threadTitle = null;

if (archiveLink is not null)
{
    links       = [archiveLink];
    threadUrl   = archiveLink.Url;
    threadTitle = $"[Archive.org] {gameName}";
}
else
{
    // 3. Initialise Playwright browser (solves Cloudflare challenges natively)
    await using var fetcher = new PlaywrightFetcher(log);

    // 4. Find thread candidates (search-first, score-ordered)
    var gbatemp    = new GbatempService(fetcher, log);
    var candidates = await gbatemp.FindThreadCandidatesAsync(
        entry.Serial, gameName, nodeId, userQuery: cli.Query);

    if (candidates.Count == 0)
    {
        log.Error($"No matching texture pack found for '{gameName}' [{entry.Serial}] — nothing installed.");
        log.Info($"Browse manually: https://gbatemp.net/forums/pcsx2-hd-texture-pack-group.{nodeId}/");
        return 1;
    }

    // 5. Extract download links — try each candidate in score order
    ForumThread? winnerThread = null;
    foreach (var candidate in candidates)
    {
        links = await gbatemp.GetDownloadLinksAsync(candidate.Url, candidate);
        if (links.Count > 0) { winnerThread = candidate; break; }
        if (candidate != candidates[^1])
            log.Info($"No links in \"{candidate.Title}\" — trying next candidate...");
    }

    if (links.Count == 0)
    {
        log.Error($"No usable download links in any matching thread for '{gameName}' [{entry.Serial}] — nothing installed.");
        return 1;
    }

    threadUrl   = winnerThread!.Url;
    threadTitle = winnerThread.Title;
}

// 6. Spawn job - unless this is a dry run, where everything above (resolving the serial,
// picking a pack, finding usable links) was the point and the download is not.
if (cli.DryRun)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("Dry run - nothing downloaded or installed.");
    Console.ResetColor();
    Console.WriteLine($"  Game:      {gameName} [{entry.Serial}] {entry.Region}");
    Console.WriteLine($"  Pack:      {threadTitle}");
    Console.WriteLine($"  Source:    {threadUrl}");
    Console.WriteLine($"  Links:     {links.Count}");
    Console.WriteLine($"  Would install to: {texturesPath}");
    Console.WriteLine($"  Would write ini:  {gamesettingsPath}");
    Console.WriteLine();
    return 0;
}

var jobState = new JobState
{
    Query            = cli.Query,
    Serial           = entry.Serial,
    GameName         = gameName,
    Region           = entry.Region,
    ThreadUrl        = threadUrl,
    ThreadTitle      = threadTitle,
    DownloadLinks    = links,
    TexturesPath     = texturesPath,
    GamesettingsPath = gamesettingsPath,
};

var jobSvc = new JobService(log, AppPaths.JobsDir);
var result = jobSvc.Spawn(jobState);

Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Download job spawned.  It will continue in the background.");
Console.ResetColor();
Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine($"  Job ID:   {result.JobId}");
Console.ResetColor();
Console.WriteLine($"  Log:      {result.LogFile}");
Console.WriteLine($"  Check:    ps2tex --status {result.JobId}");
Console.WriteLine();

if (cli.Json)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        jobId   = result.JobId,
        serial  = entry.Serial,
        game    = gameName,
        thread  = threadUrl,
        links   = links,
        logFile = result.LogFile,
    }, new JsonSerializerOptions { WriteIndented = true }));
}

return 0;

// ============================================================================
//  Argument model
// ============================================================================
internal sealed class Args
{
    public string  Command          { get; private set; } = "";
    public string? Query            { get; private set; }
    public bool    List             { get; private set; }
    public string? Status           { get; private set; }
    public bool    Clean            { get; private set; }
    public bool    Version          { get; private set; }
    public bool    All              { get; private set; }
    public bool    DryRun           { get; private set; }
    public bool    Interactive      { get; private set; }
    public bool    Json             { get; private set; }
    public string? Search           { get; private set; }
    public bool    Browse           { get; private set; }
    public bool    Refresh          { get; private set; }
    public string? TexturesPath     { get; private set; }
    public string? GamesettingsPath { get; private set; }
    public string? GameIndexPath    { get; private set; }
    public int     NodeId           { get; private set; }
    public string? JobFile          { get; private set; }

    public static Args Parse(string[] argv)
    {
        var a = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            switch (argv[i].ToLowerInvariant())
            {
                case "worker":                  a.Command          = "worker"; break;
                case "--query":       case "-q": a.Query           = Next(argv, ref i); break;
                case "--list":        case "-l": a.List            = true; break;
                case "--status":      case "-s": a.Status          = Next(argv, ref i); break;
                case "--clean":       case "--clear": a.Clean      = true; break;
                case "--version":     case "-v": a.Version          = true; break;
                case "--all":                    a.All             = true; break;
                case "--dry-run":     case "--dryrun": case "-n": a.DryRun = true; break;
                case "--interactive": case "-i": a.Interactive     = true; break;
                case "--json":                   a.Json            = true; break;
                case "--search":                  a.Search          = Next(argv, ref i); break;
                case "--browse":      case "-b":  a.Browse          = true; break;
                case "--refresh":                  a.Refresh         = true; break;
                case "--textures-path":          a.TexturesPath    = Next(argv, ref i); break;
                case "--gamesettings-path":      a.GamesettingsPath= Next(argv, ref i); break;
                case "--game-index":             a.GameIndexPath   = Next(argv, ref i); break;
                case "--node-id":
                    if (int.TryParse(Next(argv, ref i), out int nid)) a.NodeId = nid; break;
                case "--job-file":               a.JobFile         = Next(argv, ref i); break;
            }
        }
        return a;
    }

    private static string? Next(string[] argv, ref int i)
        => ++i < argv.Length ? argv[i] : null;
}
