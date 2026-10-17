using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;


namespace SilentStream.Inspection.Coordinator;

/// <summary>Authenticated Broker → Coordinator → user-context App metadata delivery.</summary>
public sealed class UsbDocumentBridge : BackgroundService
{
    private readonly IBrokerClient _broker;
    private readonly CoordinatorOptionsStore _options;
    private readonly IHttpClientFactory _clients;
    public UsbDocumentBridge(IBrokerClient broker, CoordinatorOptionsStore options, IHttpClientFactory clients)
    { _broker = broker; _options = options; _clients = clients; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long cursor = 0;
        var retryDelay = 3000;
        while (!stoppingToken.IsCancellationRequested)
        {
            var pollDelay = 250;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var client = _clients.CreateClient(nameof(UsbDocumentBridge));
                var token = _options.UnprotectMachine(_options.Current.AppApiTokenMachineEnc);
                using var settingsRequest = Request(HttpMethod.Get, "/api/usb-documents/enabled", token);
                using var settingsResponse = await client.SendAsync(settingsRequest, timeout.Token).ConfigureAwait(false);
                settingsResponse.EnsureSuccessStatusCode();
                using var settings = JsonDocument.Parse(await settingsResponse.Content.ReadAsStringAsync(timeout.Token));
                var enabled = settings.RootElement.GetProperty("enabled").GetBoolean();
                pollDelay = enabled ? 250 : 3000;
                var result = await _broker.SendAsync("usb-documents-poll", new { enabled, cursor },
                    "usb-documents-lease", timeout.Token).ConfigureAwait(false);
                if (!result.Ok || result.Payload is null) throw new IOException("Detector unavailable.");
                var batch = result.Payload.Value;
                using var delivery = Request(HttpMethod.Post, "/api/usb-documents/detector", token);
                delivery.Headers.TryAddWithoutValidation("X-SilentStream-Inspection-Bridge", token);
                delivery.Content = new StringContent(JsonSerializer.Serialize(batch, InspectionProtocol.Json), Encoding.UTF8, "application/json");
                using var delivered = await client.SendAsync(delivery, timeout.Token).ConfigureAwait(false);
                delivered.EnsureSuccessStatusCode();
                retryDelay = 3000;
                cursor = batch.GetProperty("cursor").GetInt64(); // Only acknowledge after app persistence.
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
                or JsonException or System.Security.Cryptography.CryptographicException or TimeoutException
                or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                // App reports stale heartbeat. No filename/secret is written to service logs.
                if (stoppingToken.IsCancellationRequested) break;
                pollDelay = retryDelay;
                retryDelay = Math.Min(30000, retryDelay * 2);
            }
            await Task.Delay(pollDelay, stoppingToken).ConfigureAwait(false);
        }
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, "http://127.0.0.1:8787" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
