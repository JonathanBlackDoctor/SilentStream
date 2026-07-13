using System.Globalization;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.YouTube;

namespace SilentStream.Core.Remote;

/// <summary>
/// The phone remote's split-approval contract: concrete DTOs + request handling for
/// GET <c>/api/splits</c> and the approve/merge/skip/cancel-merge POSTs. Lives in Core (not the
/// App endpoint lambdas) so the wire shape is unit-tested — QualityRemote pattern. Serialized
/// camelCase by the remote server's JSON options.
/// </summary>
public static class SplitRemote
{
    /// <summary>One split on the wire (pending, approved-awaiting-cut, or recent terminal).</summary>
    /// <param name="Label">Display/file label, e.g. "1교시" / "1~2교시".</param>
    /// <param name="Start">Proposed cut start, ISO local ("yyyy-MM-ddTHH:mm:ss").</param>
    /// <param name="End">Proposed cut end, ISO local.</param>
    /// <param name="ProposedTitle">The upload title the current window would produce.</param>
    /// <param name="AutoApproveInSec">Server-computed countdown (immune to phone-PC clock skew); null = 무한 대기.</param>
    /// <param name="ExecuteAt">For approved future-end cuts: when the cut will run, ISO local.</param>
    /// <param name="CanMerge">True when 연강 with the upcoming period is still possible.</param>
    /// <param name="Title">Final upload title once done.</param>
    public sealed record PendingDto(
        string Id,
        string Date,
        IReadOnlyList<int> Periods,
        string Label,
        string Start,
        string End,
        string ProposedTitle,
        int? AutoApproveInSec,
        string? ExecuteAt,
        bool CanMerge,
        string Status,
        string? Title,
        string? FailReason);

    /// <summary>The open 연강 chain (merge armed, waiting for the next period end).</summary>
    public sealed record ChainDto(IReadOnlyList<int> Periods, string Label, string Start, int? NextPeriod);

    /// <summary>GET /api/splits response body.</summary>
    public sealed record SplitsDto(
        bool RequireApproval,
        int? AutoApproveMinutes,
        IReadOnlyList<PendingDto> Pending,
        ChainDto? Chain,
        IReadOnlyList<PendingDto> Recent);

    /// <summary>POST /api/splits/{id}/approve body: times as "HH:mm[:ss]" or ISO local; both optional.</summary>
    public sealed record ApproveRequest(string? Start, string? End);

    /// <summary>
    /// Action outcome + fresh state so the phone re-renders from one response.
    /// <paramref name="Applied"/> mirrors Approve: "cut" (runs now) / "scheduled" (future end).
    /// </summary>
    public sealed record ActionResult(bool Ok, string? Error, string? Applied, SplitsDto Splits);

    private const int MaxRecentOnWire = 10;

    /// <summary>Builds the GET body: active splits (pending/approved), open chain, recent history.</summary>
    public static SplitsDto BuildDto(
        ISplitApprovalService splits, IPeriodScheduleStore schedule, PeriodsConfig config,
        string? roomName, DateTime nowLocal)
    {
        var all = splits.Snapshot();
        var chain = splits.OpenChain();

        var active = all
            .Where(s => s.Status is PendingSplitStatus.Pending or PendingSplitStatus.Approved)
            .Select(s => ToDto(s, schedule, config, roomName, nowLocal, chainOpen: chain is not null))
            .ToList();
        var recent = all
            .Where(s => s.Status is PendingSplitStatus.Done or PendingSplitStatus.Skipped
                or PendingSplitStatus.Failed)
            .TakeLast(MaxRecentOnWire)
            .Reverse() // newest first for the phone list
            .Select(s => ToDto(s, schedule, config, roomName, nowLocal, chainOpen: chain is not null))
            .ToList();

        return new SplitsDto(
            config.RequireApproval,
            config.AutoApproveMinutes,
            active,
            chain is null ? null : ToDto(chain, schedule),
            recent);
    }

    /// <summary>Parses + validates the request times, then delegates to the service.</summary>
    public static ActionResult Approve(
        ISplitApprovalService svc, string id, ApproveRequest? request, Func<SplitsDto> rebuild)
    {
        var item = svc.Snapshot().FirstOrDefault(s => s.Id == id);
        if (item is null)
        {
            return new ActionResult(false, "승인 대기 항목을 찾을 수 없습니다.", null, rebuild());
        }

        DateTime? start = null, end = null;
        if (!string.IsNullOrWhiteSpace(request?.Start))
        {
            if (!TryParseLocalTime(request.Start, item.Date, out var parsed))
            {
                return new ActionResult(false, "시작 시각 형식이 올바르지 않습니다 (HH:mm).", null, rebuild());
            }
            start = parsed;
        }
        if (!string.IsNullOrWhiteSpace(request?.End))
        {
            if (!TryParseLocalTime(request.End, item.Date, out var parsed))
            {
                return new ActionResult(false, "종료 시각 형식이 올바르지 않습니다 (HH:mm).", null, rebuild());
            }
            end = parsed;
        }

        var outcome = svc.Approve(id, start, end);
        return new ActionResult(outcome.Ok, outcome.Error, outcome.Applied, rebuild());
    }

    public static ActionResult Merge(ISplitApprovalService svc, string id, Func<SplitsDto> rebuild)
    {
        var outcome = svc.Merge(id);
        return new ActionResult(outcome.Ok, outcome.Error, outcome.Applied, rebuild());
    }

    public static ActionResult Skip(ISplitApprovalService svc, string id, Func<SplitsDto> rebuild)
    {
        var outcome = svc.Skip(id);
        return new ActionResult(outcome.Ok, outcome.Error, outcome.Applied, rebuild());
    }

    public static ActionResult CancelMerge(ISplitApprovalService svc, Func<SplitsDto> rebuild)
    {
        var outcome = svc.CancelMerge();
        return new ActionResult(outcome.Ok, outcome.Error, outcome.Applied, rebuild());
    }

    /// <summary>"HH:mm[:ss]" (bound to <paramref name="date"/>) or a full ISO local timestamp.</summary>
    internal static bool TryParseLocalTime(string raw, DateOnly date, out DateTime value)
    {
        var text = raw.Trim();
        if (TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
        {
            value = date.ToDateTime(time);
            return true;
        }
        if (DateTime.TryParseExact(text, ["yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            return true;
        }
        value = default;
        return false;
    }

    private static PendingDto ToDto(
        PendingSplit s, IPeriodScheduleStore schedule, PeriodsConfig config, string? roomName,
        DateTime nowLocal, bool chainOpen)
    {
        var pending = s.Status == PendingSplitStatus.Pending;
        int? autoApproveInSec = pending && s.AutoApproveAtLocal is { } at
            ? (int)Math.Max(0, (at - nowLocal).TotalSeconds)
            : null;

        // 연강 gate mirrored from the service (defense in depth — the service revalidates): a next
        // period must exist today and its end boundary must not have fired yet.
        var canMerge = false;
        if (pending && !chainOpen)
        {
            var next = schedule.ResolveForDate(s.Date).Periods
                .Where(p => p.Number > s.Periods[^1])
                .OrderBy(p => p.Number)
                .FirstOrDefault();
            canMerge = next is not null && nowLocal < s.Date.ToDateTime(next.End);
        }

        var start = s.ResolvedStartLocal ?? s.StartLocal;
        var end = s.ResolvedEndLocal ?? s.EndLocal;
        return new PendingDto(
            s.Id,
            s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            s.Periods,
            PeriodLabel.FileBase(s.Periods),
            Iso(start),
            Iso(end),
            TitleTemplater.Expand(config.TitleTemplate, start, s.Periods, roomName),
            autoApproveInSec,
            s.ExecuteAtLocal is { } exec ? Iso(exec) : null,
            canMerge,
            s.Status,
            s.Title,
            s.FailReason);
    }

    private static ChainDto ToDto(MergeChain chain, IPeriodScheduleStore schedule)
    {
        var next = schedule.ResolveForDate(chain.Date).Periods
            .Where(p => p.Number > chain.Periods[^1])
            .OrderBy(p => p.Number)
            .FirstOrDefault();
        return new ChainDto(
            chain.Periods, PeriodLabel.FileBase(chain.Periods), Iso(chain.StartLocal), next?.Number);
    }

    private static string Iso(DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
}
