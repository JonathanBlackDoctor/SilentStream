using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SilentStream.Core.UsbDocuments;

namespace SilentStream.App.Remote;

internal static class UsbDocumentEndpoints
{
    public static void Map(WebApplication app, UsbDocumentArchive archive,
        Func<HttpContext, bool> bridge, Func<IResult?> blockSettings, DocumentDriveUpload? drive = null)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/usb-documents")
                || context.Request.Path.StartsWithSegments("/usb-download"))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            }
            await next();
        });
        var tickets = new ConcurrentDictionary<string, (string Id, DateTimeOffset Expires)>();
        app.MapGet("/api/usb-documents/enabled", () => Results.Json(new { enabled = archive.Settings.Enabled }));
        app.MapGet("/api/usb-documents", (string? date, int? skip) => Results.Json(archive.Snapshot(date, skip ?? 0)));
        app.MapPost("/api/usb-documents/settings", (SettingsRequest request) =>
        {
            if (blockSettings() is { } blocked) return blocked;
            archive.Configure(request.Enabled);
            return Results.Json(archive.Snapshot());
        });
        app.MapPost("/api/usb-documents/retry", (IdRequest request) =>
            blockSettings() ?? (archive.Retry(request.Id) ? Results.Json(new { ok = true }) : Results.Conflict()));
        app.MapPost("/api/usb-documents/detector", (HttpContext context, UsbDetectorBatch batch) =>
        {
            if (!bridge(context)) return Results.StatusCode(403);
            if (batch.Events is null || batch.Events.Length > 64) return Results.BadRequest();
            archive.Heartbeat(batch.Status, batch.Dropped);
            foreach (var notice in batch.Events) archive.Observe(notice);
            return Results.Ok();
        });
        app.MapPost("/api/usb-documents/list", async Task<IResult> (ListRequest request, HttpContext context) =>
        {
            if (request.DrivePolicy is not null)
            {
                if (!bridge(context)) return Results.StatusCode(403);
                if (drive is null) return Results.Conflict();
                try { await drive.ApplyPolicyAsync(request.DrivePolicy, context.RequestAborted); }
                catch (ArgumentException) { return Results.BadRequest(); }
            }
            var snapshot = archive.Snapshot(request.Date, request.Skip);
            return Results.Json(new { snapshot.Supported, snapshot.Enabled, snapshot.DetectorStatus, snapshot.Dropped, snapshot.Items,
                snapshot.Total, drive = drive?.PolicyStatus });
        });
        app.MapPost("/api/usb-documents/chunk", async Task<IResult> (ChunkRequest request, HttpContext context) =>
        {
            try
            {
                var chunk = await archive.ReadChunkAsync(request.Id, request.Offset, context.RequestAborted);
                return chunk is null ? Results.NotFound() : Results.Json(chunk);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Conflict(); }
        });
        app.MapGet("/api/usb-documents/{id}/download", (string id, HttpContext context) => Download(archive, id, context));
        app.MapPost("/api/usb-documents/{id}/ticket", (string id) =>
        {
            try
            {
                var opened = archive.OpenDownload(id);
                if (opened is null) return Results.NotFound();
                opened.Value.Stream.Dispose();
                var now = DateTimeOffset.UtcNow;
                foreach (var entry in tickets.Where(e => e.Value.Expires < now)) tickets.TryRemove(entry.Key, out _);
                if (tickets.Count >= 32) return Results.StatusCode(429);
                var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                tickets[ticket] = (id, now.AddMinutes(1));
                return Results.Json(new { url = "/usb-download/" + ticket });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Conflict(); }
        });
        // Narrow, short-lived, one-use capability keeps reusable device credentials out of URLs.
        app.MapGet("/usb-download/{ticket}", (string ticket, HttpContext context) =>
            tickets.TryRemove(ticket, out var value) && value.Expires > DateTimeOffset.UtcNow
                ? Download(archive, value.Id, context) : Results.NotFound());
    }

    private static IResult Download(UsbDocumentArchive archive, string id, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            var opened = archive.OpenDownload(id);
            if (opened is null) return Results.NotFound();
            return Results.File(opened.Value.Stream, "application/octet-stream", opened.Value.Info.Name,
                enableRangeProcessing: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Results.Conflict(); }
    }
    internal sealed record SettingsRequest(bool Enabled);
    internal sealed record IdRequest(string Id);
    internal sealed record ListRequest(string? Date, int Skip = 0, DocumentDrivePolicy? DrivePolicy = null);
    internal sealed record ChunkRequest(string Id, long Offset);
}
