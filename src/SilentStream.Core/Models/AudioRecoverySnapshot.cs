namespace SilentStream.Core.Models;

/// <summary>Observable state of one background YouTube audio-recovery job.</summary>
public sealed record AudioRecoverySnapshot(string AssetId, string Status, string? Message = null);

/// <summary>Stable wire values used by the remote controller.</summary>
public static class AudioRecoveryStatus
{
    public const string Idle = "idle";
    public const string Queued = "queued";
    public const string Downloading = "downloading";
    public const string Encoding = "encoding";
    public const string Available = "available";
    public const string Failed = "failed";

    public static bool IsActive(string status) =>
        status is Queued or Downloading or Encoding;
}
