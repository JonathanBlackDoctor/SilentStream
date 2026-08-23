using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public sealed class InspectionAuthenticator
{
    private readonly CoordinatorOptionsStore _options;
    private readonly object _pairingGate = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset BlockedUntil)> _pairingAttempts = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsageWrite = new();
    private int _globalPairingFailures;
    private DateTimeOffset _globalPairingBlockedUntil;

    public InspectionAuthenticator(CoordinatorOptionsStore options) => _options = options;

    public ClaimsPrincipal? Authenticate(HttpContext context)
    {
        var token = ExtractToken(context);
        if (string.IsNullOrWhiteSpace(token)) return null;
        var entry = _options.Current.Tokens.FirstOrDefault(e => InspectionToken.Matches(token, e.Hash));
        if (entry is null || entry.RevokedAtUtc is not null || entry.ExpiresAtUtc is { } expires && expires <= DateTimeOffset.UtcNow) return null;
        var now = DateTimeOffset.UtcNow;
        if (!_lastUsageWrite.TryGetValue(entry.Hash, out var last) || now - last >= TimeSpan.FromMinutes(1))
        {
            _lastUsageWrite[entry.Hash] = now;
            _options.MarkTokenUsed(entry.Hash);
        }
        var identity = new ClaimsIdentity("SilentStreamToken");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, entry.Hash[..Math.Min(12, entry.Hash.Length)]));
        identity.AddClaim(new Claim("inspection_token_hash", entry.Hash));
        identity.AddClaim(new Claim(ClaimTypes.Name, entry.Label));
        identity.AddClaim(new Claim(ClaimTypes.Role, entry.Role.ToString()));
        return new ClaimsPrincipal(identity);
    }

    public PairResponse? Pair(string remoteAddress, PairRequest request)
    {
        lock (_pairingGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_globalPairingBlockedUntil > now) return null;
            var state = _pairingAttempts.GetOrAdd(remoteAddress, _ => (0, DateTimeOffset.MinValue));
            if (state.BlockedUntil > now) return null;
            if (!CryptographicEquals(request.Pin, _options.Current.PairingPin))
            {
                var count = state.Count + 1;
                _pairingAttempts[remoteAddress] = count >= 5 ? (0, now.AddMinutes(10)) : (count, DateTimeOffset.MinValue);
                _globalPairingFailures++;
                if (_globalPairingFailures >= 20)
                {
                    _globalPairingFailures = 0;
                    _globalPairingBlockedUntil = now.AddMinutes(10);
                }
                return null;
            }

            _pairingAttempts.TryRemove(remoteAddress, out _);
            _globalPairingFailures = 0;
            _globalPairingBlockedUntil = DateTimeOffset.MinValue;
            var token = InspectionToken.Create();
            _options.Update(options => options.Tokens.Add(new InspectionTokenEntry(
                InspectionToken.Hash(token), InspectionRole.Operator,
                string.IsNullOrWhiteSpace(request.DeviceName) ? "paired-device" : request.DeviceName.Trim()[..Math.Min(80, request.DeviceName.Trim().Length)],
                now)));
            return new PairResponse(token, InspectionRole.Operator);
        }
    }

    public static bool HasRole(HttpContext context, InspectionRole minimum)
    {
        var value = context.User.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<InspectionRole>(value, out var role) && role >= minimum;
    }

    public bool IsActive(ClaimsPrincipal principal)
    {
        var hash = principal.FindFirstValue("inspection_token_hash");
        var entry = _options.Current.Tokens.FirstOrDefault(t => t.Hash == hash);
        return entry is not null && entry.RevokedAtUtc is null && (entry.ExpiresAtUtc is null || entry.ExpiresAtUtc > DateTimeOffset.UtcNow);
    }

    private static string? ExtractToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim();
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
        {
            if (protocol.StartsWith("bearer.", StringComparison.Ordinal)) return protocol[7..];
        }
        return null;
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(left ?? string.Empty);
        var b = System.Text.Encoding.UTF8.GetBytes(right ?? string.Empty);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public sealed class ControlLeaseManager
{
    private readonly object _gate = new();
    private readonly InspectionStore _store;
    private Lease? _current;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    public ControlLeaseManager(InspectionStore store) => _store = store;

    public ControlLeaseResponse? Acquire(string owner)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_current is not null && _current.ExpiresAtUtc <= now)
            {
                _store.SaveControlLease(_current.Id, _current.Owner, now, now, "expired");
                _current = null;
            }
            if (_current is not null && _current.ExpiresAtUtc > now && _current.Owner != owner) return null;
            _current = new Lease(Guid.NewGuid().ToString("N"), owner, now.Add(Lifetime));
            _store.SaveControlLease(_current.Id, owner, now, _current.ExpiresAtUtc, "active");
            return new ControlLeaseResponse(_current.Id, _current.ExpiresAtUtc);
        }
    }

    public ControlLeaseResponse? Heartbeat(string leaseId, string owner)
    {
        lock (_gate)
        {
            if (_current is null || _current.Id != leaseId || _current.Owner != owner || _current.ExpiresAtUtc <= DateTimeOffset.UtcNow) return null;
            _current = _current with { ExpiresAtUtc = DateTimeOffset.UtcNow.Add(Lifetime) };
            _store.SaveControlLease(_current.Id, owner, DateTimeOffset.UtcNow, _current.ExpiresAtUtc, "active");
            return new ControlLeaseResponse(_current.Id, _current.ExpiresAtUtc);
        }
    }

    public bool Validate(string leaseId, string owner)
    {
        lock (_gate)
        {
            if (_current is not null && _current.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _store.SaveControlLease(_current.Id, _current.Owner, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "expired");
                _current = null;
            }
            return _current is not null && _current.Id == leaseId && _current.Owner == owner;
        }
    }

    public bool Release(string leaseId, string owner)
    {
        lock (_gate)
        {
            if (_current?.Id != leaseId || _current.Owner != owner) return false;
            _current = null;
            _store.SaveControlLease(leaseId, owner, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "released");
            return true;
        }
    }

    private sealed record Lease(string Id, string Owner, DateTimeOffset ExpiresAtUtc);
}

public sealed class ConfirmationManager
{
    private readonly ConcurrentDictionary<string, Grant> _grants = new();

    public DestructiveConfirmationResponse Create(string owner, DestructiveConfirmationRequest request)
    {
        var id = InspectionToken.Create();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        _grants[id] = new Grant(owner, request.Action, request.RunId, expires);
        return new DestructiveConfirmationResponse(id, expires);
    }

    public bool Consume(string id, string owner, string action, string runId)
    {
        if (!_grants.TryRemove(id, out var grant)) return false;
        return grant.Owner == owner && grant.Action == action && grant.RunId == runId && grant.ExpiresAtUtc > DateTimeOffset.UtcNow;
    }

    private sealed record Grant(string Owner, string Action, string RunId, DateTimeOffset ExpiresAtUtc);
}
