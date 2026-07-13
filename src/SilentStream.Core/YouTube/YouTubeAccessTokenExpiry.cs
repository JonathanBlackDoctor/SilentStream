using Google.Apis.Auth.OAuth2.Responses;

namespace SilentStream.Core.YouTube;

/// <summary>Extracts only safe access-token lifetime metadata from Google's token response.</summary>
internal static class YouTubeAccessTokenExpiry
{
    public static DateTime? Resolve(TokenResponse token)
    {
        if (token.ExpiresInSeconds is not long lifetimeSeconds || token.IssuedUtc == default)
        {
            return null;
        }

        try
        {
            return token.IssuedUtc.AddSeconds(lifetimeSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
