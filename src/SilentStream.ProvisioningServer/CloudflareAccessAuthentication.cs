using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

public sealed class CloudflareAccessAuthentication
{
    private readonly RequestDelegate _next;

    public CloudflareAccessAuthentication(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, CloudflareAccessJwtValidator validator,
        IOptions<ControlPlaneOptions> configured, IHostEnvironment environment)
    {
        if (!NeedsUserIdentity(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var options = configured.Value;
        var assertion = context.Request.Headers["Cf-Access-Jwt-Assertion"].ToString();
        ClaimsPrincipal? principal = null;
        if (!string.IsNullOrWhiteSpace(assertion))
            principal = await validator.ValidateAsync(assertion, context.RequestAborted).ConfigureAwait(false);

        if (principal is null && environment.IsDevelopment() && options.AllowDevelopmentIdentity
            && IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None))
        {
            var email = context.Request.Headers["X-SilentStream-Dev-Email"].ToString();
            if (IsSafeEmail(email)) principal = Principal(email.Trim().ToLowerInvariant(), "local-development");
        }

        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Google 로그인이 필요합니다." }, context.RequestAborted);
            return;
        }

        context.User = principal;
        await _next(context);
    }

    private static bool NeedsUserIdentity(PathString path) =>
        path.StartsWithSegments("/control") || path.StartsWithSegments("/install")
        || path.StartsWithSegments("/api/control") || path.StartsWithSegments("/api/install");

    private static bool IsSafeEmail(string value) => value.Length is > 3 and <= 254
        && value.Contains('@') && value.All(character => !char.IsControl(character));

    internal static ClaimsPrincipal Principal(string email, string subject)
    {
        var identity = new ClaimsIdentity("CloudflareAccess", ClaimTypes.Email, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
        identity.AddClaim(new Claim(ClaimTypes.Email, email));
        identity.AddClaim(new Claim(ClaimTypes.Name, email));
        return new ClaimsPrincipal(identity);
    }
}

public sealed class CloudflareAccessJwtValidator
{
    private readonly IHttpClientFactory _clients;
    private readonly ControlPlaneOptions _options;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, RSAParameters> _keys = new(StringComparer.Ordinal);
    private DateTimeOffset _keysExpireAtUtc;

    public CloudflareAccessJwtValidator(IHttpClientFactory clients, IOptions<ControlPlaneOptions> options)
    {
        _clients = clients;
        _options = options.Value;
    }

    public async Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        var audiences = _options.AccessAudiences
            .Append(_options.AccessAudience)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (string.IsNullOrWhiteSpace(_options.AccessTeamDomain) || audiences.Length == 0)
            return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var header = JsonDocument.Parse(Decode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "RS256") return null;
            var kid = header.RootElement.GetProperty("kid").GetString();
            if (string.IsNullOrWhiteSpace(kid)) return null;
            if (!_keys.TryGetValue(kid, out var parameters) || _keysExpireAtUtc <= DateTimeOffset.UtcNow)
            {
                await RefreshKeysAsync(cancellationToken).ConfigureAwait(false);
                if (!_keys.TryGetValue(kid, out parameters)) return null;
            }

            using var rsa = RSA.Create();
            rsa.ImportParameters(parameters);
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!rsa.VerifyData(signed, Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return null;

            using var payload = JsonDocument.Parse(Decode(parts[1]));
            var root = payload.RootElement;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!root.TryGetProperty("exp", out var exp) || exp.GetInt64() < now) return null;
            if (root.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now + 60) return null;
            var issuer = root.TryGetProperty("iss", out var iss) ? iss.GetString() : null;
            if (!string.Equals(NormalizeIssuer(issuer), NormalizeIssuer(ExpectedIssuer()), StringComparison.OrdinalIgnoreCase)) return null;
            if (!audiences.Any(expected => HasAudience(root, expected))) return null;
            var email = root.TryGetProperty("email", out var emailValue) ? emailValue.GetString() : null;
            var subject = root.TryGetProperty("sub", out var subValue) ? subValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(subject)) return null;
            return CloudflareAccessAuthentication.Principal(email.Trim().ToLowerInvariant(), subject);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException
                                   or KeyNotFoundException or InvalidOperationException or HttpRequestException)
        {
            return null;
        }
    }

    private async Task RefreshKeysAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys.Count > 0 && _keysExpireAtUtc > DateTimeOffset.UtcNow) return;
            var uri = $"{ExpectedIssuer()}/cdn-cgi/access/certs";
            using var response = await _clients.CreateClient(nameof(CloudflareAccessJwtValidator))
                .GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            var next = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
            foreach (var key in document.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (key.GetProperty("kty").GetString() != "RSA") continue;
                var kid = key.GetProperty("kid").GetString();
                var modulus = key.GetProperty("n").GetString();
                var exponent = key.GetProperty("e").GetString();
                if (string.IsNullOrWhiteSpace(kid) || modulus is null || exponent is null) continue;
                next[kid] = new RSAParameters { Modulus = Decode(modulus), Exponent = Decode(exponent) };
            }
            _keys.Clear();
            foreach (var pair in next) _keys[pair.Key] = pair.Value;
            _keysExpireAtUtc = DateTimeOffset.UtcNow.AddHours(6);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private string ExpectedIssuer()
    {
        var domain = _options.AccessTeamDomain.Trim().TrimEnd('/');
        if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return domain;
        if (!domain.Contains('.')) domain += ".cloudflareaccess.com";
        return "https://" + domain;
    }

    private static string NormalizeIssuer(string? issuer) => (issuer ?? string.Empty).TrimEnd('/');

    private static bool HasAudience(JsonElement root, string expected)
    {
        if (!root.TryGetProperty("aud", out var audience)) return false;
        return audience.ValueKind switch
        {
            JsonValueKind.String => audience.GetString() == expected,
            JsonValueKind.Array => audience.EnumerateArray().Any(value => value.GetString() == expected),
            _ => false
        };
    }

    internal static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }
}
