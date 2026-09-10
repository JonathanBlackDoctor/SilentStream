using System.Net;
using System.Security.Cryptography;
using SilentStream.Inspection.Installer;
using Xunit;

namespace SilentStream.Inspection.Tests;

public sealed class VerifiedDownloadTests
{
    private static readonly byte[] Payload = "verified package"u8.ToArray();
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Payload));

    [Fact]
    public async Task Retries_server_failure_and_verifies_complete_file()
    {
        var handler = new Handler(n => n == 1 ? new(HttpStatusCode.ServiceUnavailable) : Success(Payload));
        await Run(handler, async (client, path) => {
            await VerifiedDownload.RunAsync(client, new("https://test.invalid/package"), path, Payload.Length, Hash, retryDelay: TimeSpan.Zero);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, handler.Calls);
        });
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(302)]
    public async Task Does_not_retry_authentication_or_redirect(int status)
    {
        var handler = new Handler(_ => new((HttpStatusCode)status));
        await Run(handler, async (client, path) => {
            await Assert.ThrowsAsync<HttpRequestException>(() => VerifiedDownload.RunAsync(client, new("https://test.invalid/package"), path, Payload.Length, Hash, retryDelay: TimeSpan.Zero));
            Assert.Equal(1, handler.Calls);
        });
    }

    [Fact]
    public async Task Corrupt_same_size_package_is_rejected_without_retry()
    {
        var handler = new Handler(_ => Success(new byte[Payload.Length]));
        await Run(handler, async (client, path) => {
            await Assert.ThrowsAsync<CryptographicException>(() => VerifiedDownload.RunAsync(client, new("https://test.invalid/package"), path, Payload.Length, Hash));
            Assert.Equal(1, handler.Calls);
        });
    }

    [Fact]
    public async Task Truncated_stream_is_replaced_not_appended_on_retry()
    {
        var handler = new Handler(n => {
            var response = Success(n == 1 ? Payload[..3] : Payload);
            response.Content.Headers.ContentLength = Payload.Length;
            return response;
        });
        await Run(handler, async (client, path) => {
            await VerifiedDownload.RunAsync(client, new("https://test.invalid/package"), path, Payload.Length, Hash, retryDelay: TimeSpan.Zero);
            Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, handler.Calls);
        });
    }

    [Fact]
    public async Task Persistent_failure_stops_after_three_attempts()
    {
        var handler = new Handler(_ => new(HttpStatusCode.BadGateway));
        await Run(handler, async (client, path) => {
            await Assert.ThrowsAsync<HttpRequestException>(() => VerifiedDownload.RunAsync(client, new("https://test.invalid/package"), path, Payload.Length, Hash, retryDelay: TimeSpan.Zero));
            Assert.Equal(3, handler.Calls);
        });
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        var handler = new Handler(_ => throw new OperationCanceledException());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Run(handler, async (client, path) => {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifiedDownload.RunAsync(client, new("https://test.invalid/package"), path, Payload.Length, Hash, cancellation.Token));
            Assert.InRange(handler.Calls, 0, 1);
        });
    }

    private static HttpResponseMessage Success(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static async Task Run(Handler handler, Func<HttpClient, string, Task> test)
    {
        using var client = new HttpClient(handler);
        var path = Path.Combine(Path.GetTempPath(), "SilentStream-download-test-" + Guid.NewGuid().ToString("N"));
        try { await test(client, path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private sealed class Handler(Func<int, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(++Calls));
    }
}
