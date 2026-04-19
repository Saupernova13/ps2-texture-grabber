namespace Ps2TextureGrabber.Models;

/// <summary>A thread listing extracted from a GBAtemp forum page.</summary>
public sealed record ForumThread(
    string ThreadId,
    string Slug,
    string Title,
    string Url);
