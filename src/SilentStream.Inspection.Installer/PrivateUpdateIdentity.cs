using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace SilentStream.Inspection.Installer;

/// <summary>A PC update identity. Only its public key is reported through the enrolled coordinator.</summary>
internal sealed class PrivateUpdateIdentity : IDisposable
{
    public const string BaseUrl = "https://agent.silentstream.win/api/updates";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SilentStream.PrivateUpdates.v1");
    private static readonly Lazy<PrivateUpdateIdentity> Identity = new(Create);
    private readonly ECDsa _key;
    private readonly object _gate = new();
    public string KeyId { get; }
    public string PublicKey { get; }
    internal PrivateUpdateIdentity(ECDsa key)
    {
        _key = key;
        var publicBytes = key.ExportSubjectPublicKeyInfo();
        KeyId = Convert.ToHexString(SHA256.HashData(publicBytes)).ToLowerInvariant();
        PublicKey = Convert.ToBase64String(publicBytes);
    }
    public static PrivateUpdateIdentity Current => Identity.Value;
    public static DateTimeOffset? LastAuthenticatedFeedUtc { get; set; }
    public static object? PublicInfo()
    {
        try { var key = Current; return new { keyId = key.KeyId, publicKey = key.PublicKey, protocol = 1,
            lastAuthenticatedFeedUtc = LastAuthenticatedFeedUtc }; }
        catch { return null; }
    }
    private static PrivateUpdateIdentity Create()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Device update keys require Windows DPAPI.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaCaptureHelper");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "private-update-key.dat");
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(file))
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
            try { key.ImportPkcs8PrivateKey(bytes, out _); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        else
        {
            var bytes = key.ExportPkcs8PrivateKey();
            try
            {
                var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
                using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(encrypted); output.Flush(true);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return new PrivateUpdateIdentity(key);
    }
    public HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "agent.silentstream.win" || !uri.IsDefaultPort
            || !uri.AbsolutePath.StartsWith("/api/updates/", StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("비공개 업데이트 주소가 올바르지 않습니다.");
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        var message = Encoding.UTF8.GetBytes(string.Join("\n", method.Method, uri.PathAndQuery, time, nonce));
        byte[] signature;
        lock (_gate) signature = _key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-Update-Key", KeyId);
        request.Headers.Add("X-Update-Time", time);
        request.Headers.Add("X-Update-Nonce", nonce);
        request.Headers.Add("X-Update-Signature", Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        return request;
    }
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        using var request = Current.Request(HttpMethod.Get, uri);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri?.Host != uri.Host)
        { response.Dispose(); throw new InvalidDataException("업데이트 주소가 변경되었습니다."); }
        response.EnsureSuccessStatusCode();
        return response;
    }
    public void Dispose() => _key.Dispose();
}
