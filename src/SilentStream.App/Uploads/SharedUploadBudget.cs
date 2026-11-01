using System.IO;
using System.Net.Http;
using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Inspection.Installer;

namespace SilentStream.App.Uploads;

/// <summary>No constructor I/O and no dependency on capture, encoder, live API, or updater.</summary>
public sealed class SharedUploadBudget : IUploadBudget, IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<HttpMethod, Uri, HttpRequestMessage> _sign;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public SharedUploadBudget() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }),
        (method, uri) => PrivateUpdateIdentity.Current.Request(method, uri)) { }

    internal SharedUploadBudget(HttpClient client, Func<HttpMethod, Uri, HttpRequestMessage> sign)
    {
        _client = client;
        _client.Timeout = TimeSpan.FromSeconds(10);
        _sign = sign;
    }

    public async Task<UploadBudgetDecision> ReserveAsync(DateOnly recordingDate, string attemptId, CancellationToken ct)
    {
        using var response = await SendAsync($"op=reserve&date={recordingDate:yyyy-MM-dd}&attempt={Uri.EscapeDataString(attemptId)}", ct);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var decision = JsonSerializer.Deserialize<UploadBudgetDecision>(body, Json)
            ?? throw new InvalidDataException("공유 업로드 예산 응답이 없습니다.");
        if (decision.RetryAtUtc <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(decision.Reason))
            throw new InvalidDataException("공유 업로드 예산 응답이 만료되었거나 올바르지 않습니다.");
        return decision;
    }

    public async Task ReportPauseAsync(string kind, DateTimeOffset untilUtc, CancellationToken ct)
    {
        using var response = await SendAsync($"op=pause&kind={Uri.EscapeDataString(kind)}&until={untilUtc.ToUnixTimeSeconds()}", ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string query, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = _sign(HttpMethod.Post, new Uri("https://agent.silentstream.win/api/upload-budget?" + query));
        // ResponseContentRead also bounds the body read, not just response headers.
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"공유 업로드 예산 연결 실패 ({code}). 업로드만 대기합니다.");
        }
        return response;
    }

    public void Dispose() => _client.Dispose();
}
