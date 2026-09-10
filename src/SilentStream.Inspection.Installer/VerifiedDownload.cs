using System.Net;
using System.Security.Cryptography;

namespace SilentStream.Inspection.Installer;

internal static class VerifiedDownload
{
    // Retry only idempotent downloads. Authentication and certificate failures fail closed.
    public static async Task RunAsync(HttpClient client, Uri uri, string destination, long length, string sha256,
        CancellationToken cancellationToken = default, TimeSpan? retryDelay = null)
    {
        if (length <= 0 || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("설치 패키지 검증 정보가 올바르지 않습니다.");
        for (var attempt = 1; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long declared && declared != length)
                    throw new InvalidDataException("설치 패키지 크기가 중앙 서버 기록과 일치하지 않습니다.");
                await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token))
                await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    while (true)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                        idle.CancelAfter(TimeSpan.FromSeconds(60));
                        var count = await source.ReadAsync(buffer, idle.Token);
                        if (count == 0) break;
                        received += count;
                        if (received > length) throw new InvalidDataException("설치 패키지가 허용된 크기를 초과했습니다.");
                        await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                    }
                    if (received != length) throw new IOException("설치 패키지 다운로드가 중간에 끊겼습니다.");
                }
                await using var stream = File.OpenRead(destination);
                var actual = await SHA256.HashDataAsync(stream, deadline.Token);
                if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(sha256)))
                    throw new CryptographicException("설치 패키지 SHA-256이 중앙 서버 기록과 일치하지 않습니다.");
                return;
            }
            catch (Exception ex) when (attempt < 3 && !cancellationToken.IsCancellationRequested && IsTransient(ex))
            {
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException http => http.HttpRequestError != HttpRequestError.SecureConnectionError &&
            (http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500),
        IOException => true,
        OperationCanceledException => true,
        _ => false
    };
}
