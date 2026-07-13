using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Thread-safe OAuth health state shared by the live and VOD services. Google refresh-token expiry
/// is not observable by the client, so the proactive signal deliberately uses the short-lived access
/// token while a rejected token refresh is surfaced as a persistent action-required condition.
/// </summary>
public sealed class YouTubeAuthHealth : IYouTubeAuthHealth
{
    internal static readonly TimeSpan WarningWindow = TimeSpan.FromMinutes(10);

    private static readonly YouTubeAuthHealthStatus Healthy = new(
        YouTubeAuthHealthState.Healthy,
        "YouTube 인증이 정상으로 복구되었습니다.");

    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;
    private DateTime? _expiresAtUtc;
    private YouTubeAuthFailureKind? _failure;
    private YouTubeAuthHealthStatus _status = Healthy;

    /// <summary>
    /// The optional clock is a test seam. Dependency injection uses the default UTC clock because
    /// no <see cref="Func{TResult}"/> registration exists in the production container.
    /// </summary>
    public YouTubeAuthHealth(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public event EventHandler<YouTubeAuthHealthStatus>? StatusChanged;

    public void ObserveAccessTokenExpiry(DateTime? expiresAtUtc)
    {
        YouTubeAuthHealthStatus? changed;
        lock (_gate)
        {
            _expiresAtUtc = expiresAtUtc?.ToUniversalTime();
            _failure = null;
            changed = SetStatusLocked(EvaluateLocked());
        }
        Raise(changed);
    }

    public void ReportPermanentFailure(YouTubeAuthFailureKind failure)
    {
        YouTubeAuthHealthStatus? changed;
        lock (_gate)
        {
            _failure = failure;
            changed = SetStatusLocked(EvaluateLocked());
        }
        Raise(changed);
    }

    public void Evaluate()
    {
        YouTubeAuthHealthStatus? changed;
        lock (_gate)
        {
            changed = SetStatusLocked(EvaluateLocked());
        }
        Raise(changed);
    }

    private YouTubeAuthHealthStatus EvaluateLocked()
    {
        if (_failure is YouTubeAuthFailureKind.MissingClientSecret)
        {
            return new YouTubeAuthHealthStatus(
                YouTubeAuthHealthState.ActionRequired,
                "YouTube OAuth 클라이언트 설정 파일이 없습니다. 이 PC에서 client_secret.json을 확인하세요.");
        }

        if (_failure is YouTubeAuthFailureKind.TokenRejected)
        {
            return new YouTubeAuthHealthStatus(
                YouTubeAuthHealthState.ActionRequired,
                "YouTube 인증을 갱신할 수 없습니다. 이 PC에서 Google 계정에 다시 로그인하세요.");
        }

        if (_expiresAtUtc is DateTime expiry && expiry - _utcNow() <= WarningWindow)
        {
            return new YouTubeAuthHealthStatus(
                YouTubeAuthHealthState.Expiring,
                "YouTube 로그인 토큰이 10분 이내에 만료됩니다. 자동 갱신 상태를 확인하세요.");
        }

        return Healthy;
    }

    private YouTubeAuthHealthStatus? SetStatusLocked(YouTubeAuthHealthStatus next)
    {
        if (_status == next)
        {
            return null;
        }
        _status = next;
        return next;
    }

    private void Raise(YouTubeAuthHealthStatus? changed)
    {
        if (changed is not null)
        {
            StatusChanged?.Invoke(this, changed);
        }
    }
}
