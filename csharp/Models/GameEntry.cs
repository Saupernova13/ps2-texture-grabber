namespace Ps2TextureGrabber.Models;

/// <summary>One row from PCSX2's GameIndex.yaml.</summary>
public sealed record GameEntry(
    string  Serial,
    string? Name,
    string? NameEn,
    string? Region)
{
    public string DisplayName => NameEn ?? Name ?? Serial;
}
