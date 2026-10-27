using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SilentStream.Inspection.Installer;

/// <summary>Downloads only matching immutable assets from the application's official release.</summary>
internal static class OfficialRelease
{
    internal sealed record Asset(Uri Url, long Length, string Sha256);
    public static string Version => typeof(OfficialRelease).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}" : throw new InvalidDataException("앱 버전을 확인하지 못했습니다.");

    public static async Task<Asset> FindAsync(HttpClient client, string version, bool package, CancellationToken ct = default,
        Func<HttpMethod, Uri, HttpRequestMessage>? requestFactory = null)
    {
        if (!Regex.IsMatch(version, @"^0[.][0-9]+[.][0-9]+$")) throw new InvalidDataException("올바르지 않은 버전입니다.");
        var name = package ? $"SilentStream-prepared-package-v{version}.zip" : $"SilentStream-Central-Installer-v{version}.exe";
        var metadata = new Uri($"{PrivateUpdateIdentity.BaseUrl}/v{version}/manifest.json");
        using var request = (requestFactory ?? PrivateUpdateIdentity.Current.Request)(HttpMethod.Get, metadata);
        request.Headers.UserAgent.ParseAdd("SilentStream-Updater/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        // Metadata is bounded independently of the much larger streamed package.
        await response.Content.LoadIntoBufferAsync(1024 * 1024);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var release = json.RootElement;
        if (release.GetProperty("version").GetString() != version)
            throw new InvalidDataException("공식 정식 릴리스를 확인하지 못했습니다.");
        var matches = release.GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("이 버전의 도우미 설치 파일이 아직 준비되지 않았습니다.");
        var asset = matches[0];
        var url = asset.GetProperty("url").GetString();
        var expected = $"{PrivateUpdateIdentity.BaseUrl}/{name}";
        var length = asset.GetProperty("length").GetInt64();
        var digest = asset.GetProperty("sha256").GetString() ?? "";
        if (url != expected || length <= 0 || length > 1024L * 1024 * 1024
            || !Regex.IsMatch(digest, "^[a-f0-9]{64}$"))
            throw new InvalidDataException("공식 파일의 주소·크기·해시가 올바르지 않습니다.");
        return new(new Uri(expected), length, digest);
    }

    public static async Task DownloadAsync(HttpClient client, string version, bool package, string target, CancellationToken ct = default)
    {
        var asset = await FindAsync(client, version, package, ct);
        await VerifiedDownload.RunAsync(client, asset.Url, target, asset.Length, asset.Sha256, ct,
            requestFactory: uri => PrivateUpdateIdentity.Current.Request(HttpMethod.Get, uri));
    }
}
