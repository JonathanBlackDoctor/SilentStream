using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SilentStream.App.Remote;
using SilentStream.Core.Implementations;
using SilentStream.Core.UsbDocuments;
using Xunit;

namespace SilentStream.Tests;

public sealed class UsbDocumentEndpointTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("silentstream-usb-api-").FullName;
    private WebApplication _app = null!;
    private UsbDocumentArchive _archive = null!;
    private HttpClient _client = null!;
    private bool _locked;
    public async Task InitializeAsync()
    {
        var config = new ConfigStore(Path.Combine(_root, "config.json"));
        _archive = new(config, Path.Combine(_root, "catalog.json"), 100_000_000);
        _archive.Configure(true, Path.Combine(_root, "documents"));
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders(); _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers.Authorization != "Bearer paired")
            { ctx.Response.StatusCode = 401; return; }
            await next();
        });
        UsbDocumentEndpoints.Map(_app, _archive, context => context.Request.Headers["X-Bridge"] == "service",
            () => _locked ? Results.StatusCode(403) : null);
        await _app.StartAsync(); _client = new() { BaseAddress = new Uri(_app.Urls.Single()) };
        _client.DefaultRequestHeaders.Authorization = new("Bearer", "paired");
    }
    private async Task<UsbDocumentInfo> Document()
    {
        var path = Path.Combine(_root, "수업.pdf"); File.WriteAllBytes(path, [1, 2, 3, 4]);
        _archive.Observe(new(path, "test-usb", 100, DateTimeOffset.UtcNow));
        using var timeout = new CancellationTokenSource(5000);
        while (_archive.Snapshot().Items[0].Status is "queued" or "copying") await Task.Delay(10, timeout.Token);
        return _archive.Snapshot().Items[0];
    }
    [Fact]
    public async Task AuthenticationAndBridgeCannotBeBypassedByPairedPhone()
    {
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/usb-documents")).StatusCode);
        var batch = new UsbDetectorBatch("fake", 0, 0, []);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/usb-documents/detector", batch)).StatusCode);
        _client.DefaultRequestHeaders.Add("X-Bridge", "service");
        Assert.True((await _client.PostAsJsonAsync("/api/usb-documents/detector", batch)).IsSuccessStatusCode);
        _locked = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync("/api/usb-documents/settings", new { enabled = false })).StatusCode);
        Assert.True(_archive.Settings.Enabled);
    }
    [Fact]
    public async Task PairedBrowserCannotInjectCentralDrivePolicy()
    {
        var response = await _client.PostAsJsonAsync("/api/usb-documents/list", new { date = (string?)null,
            drivePolicy = new DocumentDrivePolicy("m112", "owner@example.test", "test-folder-12345", "reader@example.test", new string('a',32)) });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var list = await _client.PostAsJsonAsync("/api/usb-documents/list", new { date = (string?)null });
        Assert.True(list.IsSuccessStatusCode);
    }
    [Fact]
    public async Task TicketIsScopedOneUseAndStreamsOriginalBytes()
    {
        var item = await Document();
        var ticket = await (await _client.PostAsync("/api/usb-documents/" + item.Id + "/ticket", null))
            .Content.ReadFromJsonAsync<Dictionary<string, string>>();
        using var anonymous = new HttpClient { BaseAddress = _client.BaseAddress };
        var response = await anonymous.GetAsync(ticket!["url"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(ticket["url"])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/usb-download/invalid")).StatusCode);
    }
    [Fact]
    public async Task ListOmitsPathsAndDownloadRejectsUnknownOrChangedFile()
    {
        var item = await Document();
        Assert.DoesNotContain(_root, await _client.GetStringAsync("/api/usb-documents"));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/usb-documents/" + new string('a',32) + "/download")).StatusCode);
        var response = await _client.PostAsJsonAsync("/api/usb-documents/chunk", new { id = item.Id, offset = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = Directory.GetFiles(Path.Combine(_root,"documents"), "*.pdf", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(stored, [9, 8, 7, 6, 5]);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.GetAsync("/api/usb-documents/" + item.Id + "/download")).StatusCode);
    }
    public async Task DisposeAsync()
    {
        _client.Dispose(); await _app.StopAsync(); await _app.DisposeAsync(); _archive.Dispose();
        Directory.Delete(_root, true);
    }
}
