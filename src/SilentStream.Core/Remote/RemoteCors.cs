namespace SilentStream.Core.Remote;

/// <summary>
/// CORS decisions for the remote-control API (원격 컨트롤러 개선 Phase 2 멀티 호실). The phone keeps a
/// registry of rooms and calls every room's <c>/api</c> from a page served by one of them, so the
/// server must answer cross-origin requests. Policy: reflect the request Origin (never a wildcard),
/// answer preflights before token auth runs (a preflight carries no token), and stamp the headers
/// on 401 responses too so the phone can tell "token expired" from a network failure. CORS only
/// makes responses readable — token auth stays exactly as strict as before.
/// </summary>
public static class RemoteCors
{
    /// <summary>Request headers a cross-origin caller may send (token + JSON body).</summary>
    public const string AllowHeaders = "Authorization, Content-Type, X-Device-Token";

    /// <summary>Methods the remote API uses.</summary>
    public const string AllowMethods = "GET, POST, PUT, DELETE, OPTIONS";

    /// <summary>
    /// Response metadata readable by the cross-origin phone UI. Download buttons use the
    /// Content-Disposition filename rather than exposing a server-side path.
    /// </summary>
    public const string ExposeHeaders = "Content-Disposition";

    /// <summary>Preflight cache lifetime (seconds) so 5–10 s grid polling isn't one OPTIONS per poll.</summary>
    public const string MaxAgeSeconds = "600";

    /// <summary>
    /// True when the response needs CORS headers: a cross-origin caller (Origin header present)
    /// hitting the API surface. Same-origin fetches send no Origin for GET, and non-API paths
    /// (the HTML page, websockets) are not CORS targets.
    /// </summary>
    public static bool AppliesTo(string? origin, string path) =>
        !string.IsNullOrEmpty(origin) && IsApiPath(path);

    /// <summary>
    /// True when the request is a CORS preflight that must be answered 204 immediately —
    /// crucially <b>before</b> the auth middleware, because preflights never carry a token.
    /// </summary>
    public static bool IsPreflight(string method, string? origin, string path) =>
        string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase) &&
        AppliesTo(origin, path);

    /// <summary>Segment-safe check for the protected API surface ("/api" or "/api/...").</summary>
    private static bool IsApiPath(string path) =>
        path.Equals("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
}
