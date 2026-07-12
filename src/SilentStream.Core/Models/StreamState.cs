namespace SilentStream.Core.Models;

/// <summary>
/// Lifecycle states of the streaming pipeline. Drives the 6px status box colour
/// (Warmup/ConnectingYouTube/Retrying => 🟡/🔴, Live => 🟢) and the control UI badge.
/// See plan §4.1 (StreamState 상태머신).
/// </summary>
public enum StreamState
{
    /// <summary>Not started yet (process just launched / fully stopped).</summary>
    Idle,

    /// <summary>30s network-stabilisation delay before the start sequence runs.</summary>
    Warmup,

    /// <summary>Authenticating and creating the YouTube broadcast (🟡).</summary>
    ConnectingYouTube,

    /// <summary>Streaming live to YouTube (🟢).</summary>
    Live,

    /// <summary>Stream failed; retrying with exponential backoff (🔴). Recording keeps running.</summary>
    Retrying,

    /// <summary>Shutdown in progress: flushing the encoder and finalising the mp4.</summary>
    Stopping,

    /// <summary>
    /// Broadcast deliberately stopped (원격 "방송만 중지") while the local backup recording keeps
    /// running on a recording-only encoder. Unlike <see cref="Retrying"/> nothing reconnects the
    /// stream; going live again requires a full stop followed by a fresh start.
    /// </summary>
    RecordingOnly
}
