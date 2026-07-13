using SilentStream.Core.Models;

namespace SilentStream.Core.Contracts;

/// <summary>
/// Shared health boundary for the OAuth credential used by YouTube live streaming and VOD uploads.
/// It exposes only expiry metadata and safe Korean status text, never token material.
/// </summary>
public interface IYouTubeAuthHealth
{
    /// <summary>Raised only when the operator-facing OAuth state changes.</summary>
    event EventHandler<YouTubeAuthHealthStatus>? StatusChanged;

    /// <summary>
    /// Records a successfully obtained credential's access-token expiry. This also clears a prior
    /// permanent OAuth failure because a fresh authorization or refresh succeeded.
    /// </summary>
    void ObserveAccessTokenExpiry(DateTime? expiresAtUtc);

    /// <summary>Records an OAuth client/token failure that cannot be solved by a normal retry.</summary>
    void ReportPermanentFailure(YouTubeAuthFailureKind failure);

    /// <summary>Evaluates the warning window. Called by the health monitor's existing poll loop.</summary>
    void Evaluate();
}
