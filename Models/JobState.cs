using System.Text.Json.Serialization;

namespace Ps2TextureGrabber.Models;

public sealed class JobState
{
    // ---- identity ----
    public string  Id      { get; set; } = string.Empty;

    // ---- lifecycle ----
    public string  Status  { get; set; } = "pending";  // pending | running | complete | failed
    public string  Step    { get; set; } = "pending";  // pending | downloading | extracting | installing | configuring | complete | failed
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
    public string? JobFile          { get; set; }
    public string? LogFile          { get; set; }
}
