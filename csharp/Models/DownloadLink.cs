namespace Ps2TextureGrabber.Models;

/// <summary>A download link extracted from a thread's opening post.</summary>
public sealed record DownloadLink(string Host, string Url);
