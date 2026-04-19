using System.Text.Json.Serialization;

namespace Ps2TextureGrabber.Models;

/// <summary>
/// Persistent state for a background download job.  Written to data/jobs/{id}.json.
/// Every field is public get/set so System.Text.Json can round-trip it without
/// a custom converter.
/// </summary>
public sealed class JobState
{
    // ---- identity ----
    public string  Id      { get; set; } = string.Empty;

    // ---- lifecycle ----
    /// <summary>pending | running | complete | failed</summary>
    public string  Status  { get; set; } = "pending";
    /// <summary>pending | downloading | extracting | installing | configuring | complete | failed</summary>
    public string  Step    { get; set; } = "pending";
    public int     Progress         { get; set; }
    public string? Message          { get; set; }
    public string? LastUpdate       { get; set; }
    public string? CreatedAt        { get; set; }
    public string? StartedAt        { get; set; }
    public string? CompletedAt      { get; set; }

    // ---- download progress ----
    public long    BytesDownloaded  { get; set; }
    public long    TotalBytes       { get; set; }
    public string? CurrentLink      { get; set; }
    public string? ServedBy         { get; set; }

    // ---- game / thread context ----
    public string? Query            { get; set; }
    public string? Serial           { get; set; }
    public string? GameName         { get; set; }
    public string? Region           { get; set; }
    public string? ThreadUrl        { get; set; }
    public string? ThreadTitle      { get; set; }
    public List<DownloadLink> DownloadLinks { get; set; } = [];

    // ---- paths ----
    public string? TexturesPath     { get; set; }
    public string? GamesettingsPath { get; set; }
    public string? FlareSolverrUrl  { get; set; }
    public string? JobFile          { get; set; }
    public string? LogFile          { get; set; }
}
