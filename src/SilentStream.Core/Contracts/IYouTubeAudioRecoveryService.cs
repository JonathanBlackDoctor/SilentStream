using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Rebuilds a missing local period-audio asset from the YouTube video id already stored in the
/// trusted period catalogue. Recovery runs in the background and is deduplicated per asset.
/// </summary>
public interface IYouTubeAudioRecoveryService
{
    /// <summary>
    /// Starts or joins recovery for <paramref name="assetId"/> and returns immediately with the
    /// current state. A failed terminal state is replaced by a fresh attempt on the next call.
    /// </summary>
    AudioRecoverySnapshot Start(string assetId);

    /// <summary>Returns the current in-memory state, or derives availability from the local cache.</summary>
    AudioRecoverySnapshot GetStatus(string assetId);
}
