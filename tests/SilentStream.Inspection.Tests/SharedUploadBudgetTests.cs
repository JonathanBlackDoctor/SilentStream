using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using SilentStream.App.Uploads;
using SilentStream.Inspection.Installer;
using Xunit;

namespace SilentStream.Inspection.Tests;

public sealed class SharedUploadBudgetTests
{
    [Fact]
    public async Task AdmissionUsesSignedPostAndBindsAllParameters()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = new PrivateUpdateIdentity(key);
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/upload-budget?op=reserve&date=2026-11-01&attempt=" + new string('a', 32), request.RequestUri!.PathAndQuery);
            string Header(string name) => request.Headers.GetValues(name).Single();
            var signature = Header("X-Update-Signature").Replace('-', '+').Replace('_', '/') + "==";
            var message = string.Join("\n", request.Method, request.RequestUri.PathAndQuery, Header("X-Update-Time"), Header("X-Update-Nonce"));
            Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(message), Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            return Json(true, DateTimeOffset.UtcNow.AddMinutes(1));
        });
        using var budget = new SharedUploadBudget(new HttpClient(handler), identity.Request);
        Assert.True((await budget.ReserveAsync(new DateOnly(2026, 11, 1), new string('a', 32), CancellationToken.None)).Allowed);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(302)]
    public async Task UnavailableOrOldServerNeverGrantsUpload(int status)
    {
        using var budget = Client(new Handler(_ => new HttpResponseMessage((HttpStatusCode)status)));
        await Assert.ThrowsAsync<HttpRequestException>(() => budget.ReserveAsync(new DateOnly(2026, 11, 1), "attempt", CancellationToken.None));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"allowed\":true,\"retryAtUtc\":\"2020-01-01T00:00:00Z\",\"reason\":\"reserved\"}")]
    public async Task IncompleteOrExpiredDecisionNeverGrantsUpload(string json)
    {
        using var budget = Client(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => budget.ReserveAsync(new DateOnly(2026, 11, 1), "attempt", CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsHangingBudgetRequest()
    {
        using var budget = Client(new HangingHandler());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.ReserveAsync(new DateOnly(2026, 11, 1), "attempt", cancel.Token));
    }

    [Fact]
    public async Task SharedPauseCarriesAbsoluteDeadlineSoRetriesCannotExtendIt()
    {
        var until = DateTimeOffset.Parse("2026-11-02T01:00:00Z");
        var targets = new List<string>();
        using var budget = Client(new Handler(request =>
        {
            targets.Add(request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await budget.ReportPauseAsync("channel", until, CancellationToken.None);
        await budget.ReportPauseAsync("channel", until, CancellationToken.None);
        Assert.Equal(targets[0], targets[1]);
        Assert.Contains("until=" + until.ToUnixTimeSeconds(), targets[0]);
    }

    [Theory]
    [InlineData("https://agent.silentstream.win/api/upload-budget/other")]
    [InlineData("https://agent.silentstream.win/api/device/enroll")]
    [InlineData("https://other.invalid/api/upload-budget")]
    public void BudgetIdentityCannotSignOtherMutationEndpoints(string uri)
    {
        using var identity = new PrivateUpdateIdentity(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        Assert.Throws<InvalidDataException>(() => identity.Request(HttpMethod.Post, new Uri(uri)));
    }

    private static SharedUploadBudget Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), (method, uri) => new HttpRequestMessage(method, uri));
    private static HttpResponseMessage Json(bool allowed, DateTimeOffset retry) =>
        new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { allowed, retryAtUtc = retry, reason = "reserved" })) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }
}
