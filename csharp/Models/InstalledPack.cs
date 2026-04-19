namespace Ps2TextureGrabber.Models;

public sealed record InstalledPack(
    string Serial,
    string GameName,
    int    TextureCount,
    double SizeMb,
    bool   IniConfigured);
