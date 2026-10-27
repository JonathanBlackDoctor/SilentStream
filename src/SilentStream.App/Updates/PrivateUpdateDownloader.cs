using System.IO;
using System.Net.Http;
using System.Text;
using SilentStream.Inspection.Installer;
using Velopack.Sources;

namespace SilentStream.App.Updates;

internal sealed class PrivateUpdateDownloader : IFileDownloader
{
    private static HttpClient Client(double timeout) => new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(Math.Clamp(timeout, 0.1, 30)) };
    public async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        using var client = Client(timeout);
        using var response = await PrivateUpdateIdentity.SendAsync(client, new Uri(url), cancelToken).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false);
        await using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[128 * 1024]; long total = 0;
        var length = response.Content.Headers.ContentLength;
        int count;
        while ((count = await source.ReadAsync(buffer, cancelToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, count), cancelToken).ConfigureAwait(false);
            total += count;
            if (length is > 0) progress((int)Math.Min(100, total * 100 / length.Value));
        }
        if (length.HasValue && total != length.Value) throw new InvalidDataException("업데이트 파일이 완전히 내려받아지지 않았습니다.");
        progress(100);
    }
    public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        using var client = Client(timeout);
        using var response = await PrivateUpdateIdentity.SendAsync(client, new Uri(url), CancellationToken.None).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(1024 * 1024).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (new Uri(url).AbsolutePath.EndsWith("/releases.win.json", StringComparison.Ordinal))
            PrivateUpdateIdentity.LastAuthenticatedFeedUtc = DateTimeOffset.UtcNow;
        return bytes;
    }
    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        => Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));
}
