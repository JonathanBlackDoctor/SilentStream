using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using SilentStream.Inspection.Installer;
using SilentStream.Inspection;

Application.EnableVisualStyles();
var root = AppContext.BaseDirectory;
var legacyPreseed = Path.Combine(root, "deployment-preseed.json");

try
{
    if (File.Exists(legacyPreseed))
    {
        var output = await RunPackageInstallAsync(root, null, installAppFirst: false);
        MessageBox.Show($"설치가 완료되었습니다.\n\n{output}", "SilentStream 자동 설치",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
    }

    var room = RoomFromFileName(Environment.ProcessPath);
    if (room is null)
    {
        MessageBox.Show(
            "호실 전용 설치 페이지에서 다시 다운로드해 주세요.\n\n파일명은 SilentStream-Install-m112.exe처럼 호실명을 포함해야 합니다.",
            "SilentStream 자동 설치", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return;
    }

    var controlPlane = ResolveControlPlane(root);
    var enrollment = await AuthorizeAndEnrollAsync(controlPlane, room);
    var temporary = Path.Combine(Path.GetTempPath(), "SilentStreamCentralInstall-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    try
    {
        var archive = Path.Combine(temporary, "prepared-package.zip");
        await DownloadPackageAsync(enrollment, archive);
        var packageRoot = Path.Combine(temporary, "package");
        ZipFile.ExtractToDirectory(archive, packageRoot);
        packageRoot = FindPackageRoot(packageRoot);
        var enrollmentFile = Path.Combine(packageRoot, "central-enrollment.json");
        await File.WriteAllTextAsync(enrollmentFile, JsonSerializer.Serialize(enrollment,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var output = await RunPackageInstallAsync(packageRoot, enrollmentFile, installAppFirst: true);
        await WaitForCentralReportAsync(enrollment);
        MessageBox.Show($"설치·중앙 연결·첫 상태 보고가 모두 확인되었습니다.\n\n{output}", "SilentStream 자동 설치",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    finally
    {
        try
        {
            var full = Path.GetFullPath(temporary);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("SilentStreamCentralInstall-", StringComparison.Ordinal))
                Directory.Delete(full, true);
        }
        catch { }
    }
}
catch (Exception ex)
{
    MessageBox.Show($"설치하지 못했습니다.\n\n{DescribeFailure(ex)}", "SilentStream 자동 설치",
        MessageBoxButtons.OK, MessageBoxIcon.Error);
}

static async Task<CentralEnrollmentResponse> AuthorizeAndEnrollAsync(Uri controlPlane, string room)
{
    var port = ReserveLoopbackPort();
    var callback = $"http://127.0.0.1:{port}/callback/";
    var state = Token(32);
    using var listener = new HttpListener();
    listener.Prefixes.Add(callback);
    listener.Start();

    var authorize = new Uri(controlPlane,
        $"/api/install/authorize?room={Uri.EscapeDataString(room)}&state={Uri.EscapeDataString(state)}&callback={Uri.EscapeDataString(callback)}");
    Process.Start(new ProcessStartInfo(authorize.AbsoluteUri) { UseShellExecute = true });

    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
    var returnedState = context.Request.QueryString["state"];
    var returnedRoom = context.Request.QueryString["room"];
    var code = context.Request.QueryString["code"];
    var enroll = context.Request.QueryString["enroll"];
    var safeEnrollmentUri = SafeHttpsUri(enroll, out var enrollmentUri);
    var ok = CryptographicEquals(state, returnedState) && string.Equals(room, returnedRoom, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(code) && safeEnrollmentUri;
    var html = Encoding.UTF8.GetBytes(ok
        ? "<!doctype html><meta charset=utf-8><title>설치 승인 완료</title><h1>설치 승인이 완료되었습니다.</h1><p>이 창을 닫아도 됩니다. 설치 프로그램이 나머지를 자동으로 진행합니다.</p>"
        : "<!doctype html><meta charset=utf-8><title>설치 승인 실패</title><h1>설치 요청을 확인할 수 없습니다.</h1>");
    context.Response.StatusCode = ok ? 200 : 400;
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.ContentLength64 = html.Length;
    await context.Response.OutputStream.WriteAsync(html);
    context.Response.Close();
    listener.Stop();
    if (!ok) throw new InvalidOperationException("Google 설치 승인 응답이 올바르지 않습니다.");

    var installationId = LoadOrCreateInstallationId();
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    HttpResponseMessage response;
    try
    {
        response = await client.PostAsJsonAsync(enrollmentUri,
            new CentralEnrollmentRedeemRequest(room, installationId, Environment.MachineName, code!), timeout.Token);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        throw new HttpRequestException($"중앙 등록 서버({enrollmentUri.Host})에 연결하지 못했습니다.", ex);
    }
    using (response)
    {
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException($"일회용 설치 승인을 기기 등록으로 교환하지 못했습니다. (HTTP {(int)response.StatusCode})");
        return await response.Content.ReadFromJsonAsync<CentralEnrollmentResponse>(cancellationToken: timeout.Token)
            ?? throw new InvalidDataException("중앙 서버의 기기 등록 응답이 비어 있습니다.");
    }
}

static async Task DownloadPackageAsync(CentralEnrollmentResponse enrollment, string destination)
{
    if (!Uri.TryCreate(enrollment.PackageDownloadUri, UriKind.Absolute, out var packageUri)
        || packageUri.Scheme != Uri.UriSchemeHttps && !packageUri.IsLoopback)
        throw new InvalidDataException("설치 패키지 주소가 안전하지 않습니다.");
    using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Device",
        $"{enrollment.RoomId}:{enrollment.DeviceId}:{enrollment.DeviceSecret}");
    HttpResponseMessage response;
    try
    {
        response = await client.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        throw new HttpRequestException($"설치 패키지 서버({packageUri.Host})에 연결하지 못했습니다.", ex);
    }
    using (response)
    {
    response.EnsureSuccessStatusCode();
    await using (var source = await response.Content.ReadAsStreamAsync())
    await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await source.CopyToAsync(output);
    }
    var info = new FileInfo(destination);
    if (enrollment.PackageLength <= 0 || info.Length != enrollment.PackageLength)
        throw new InvalidDataException("설치 패키지 크기가 중앙 서버 기록과 일치하지 않습니다.");
    await using var stream = File.OpenRead(destination);
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    if (!CryptographicEquals(hash, enrollment.PackageSha256))
        throw new CryptographicException("설치 패키지 SHA-256이 중앙 서버 기록과 일치하지 않습니다.");
}

static async Task<string> RunPackageInstallAsync(string packageRoot, string? enrollmentFile, bool installAppFirst)
{
    var script = Path.Combine(packageRoot, "Install-InspectionServices.ps1");
    var source = Path.Combine(packageRoot, "bin");
    var preseed = Path.Combine(packageRoot, "deployment-preseed.json");
    var publicKey = Directory.EnumerateFiles(packageRoot, "*.public-key.json", SearchOption.TopDirectoryOnly).FirstOrDefault()
        ?? Path.Combine(packageRoot, "public-key.json");
    if (!File.Exists(script) || !Directory.Exists(source) || !File.Exists(publicKey) || !File.Exists(preseed))
        throw new InvalidDataException("서명된 설치 패키지의 필수 파일이 누락되었습니다.");

    var setup = Directory.EnumerateFiles(packageRoot, "*Setup*.exe", SearchOption.TopDirectoryOnly)
        .FirstOrDefault(path => !Path.GetFileName(path).Contains("Inspection", StringComparison.OrdinalIgnoreCase));
    if (installAppFirst && setup is not null)
    {
        using var setupProcess = Process.Start(new ProcessStartInfo(setup)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "--silent"
        }) ?? throw new InvalidOperationException("앱 설치 프로그램을 시작하지 못했습니다.");
        await setupProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        if (setupProcess.ExitCode != 0) throw new InvalidOperationException($"앱 설치가 종료 코드 {setupProcess.ExitCode}로 실패했습니다.");
    }

    var appConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaCaptureHelper", "config.json");
    var appExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MediaCaptureHelper", "current", "MediaCaptureHelper.exe");
    if (installAppFirst && !File.Exists(appConfig))
    {
        if (!File.Exists(appExecutable)) throw new FileNotFoundException("설치된 SilentStream 앱을 찾을 수 없습니다.", appExecutable);
        Process.Start(new ProcessStartInfo(appExecutable) { UseShellExecute = true });
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (!File.Exists(appConfig) && DateTimeOffset.UtcNow < deadline) await Task.Delay(500);
        if (!File.Exists(appConfig))
            throw new InvalidOperationException("앱의 Google 로그인 또는 초기 설정이 완료되지 않았습니다. 앱 화면을 완료한 뒤 설치를 다시 실행하세요.");
    }

    var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");
    var start = new ProcessStartInfo(powershell)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
    start.ArgumentList.Add("-ExecutionPolicy"); start.ArgumentList.Add("RemoteSigned");
    start.ArgumentList.Add("-WindowStyle"); start.ArgumentList.Add("Hidden");
    start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
    start.ArgumentList.Add("-PublicKeyFile"); start.ArgumentList.Add(publicKey);
    start.ArgumentList.Add("-SourceRoot"); start.ArgumentList.Add(source);
    start.ArgumentList.Add("-PreseedFile"); start.ArgumentList.Add(preseed);
    if (setup is not null) { start.ArgumentList.Add("-CachedInstallerPath"); start.ArgumentList.Add(setup); }
    if (!string.IsNullOrWhiteSpace(enrollmentFile))
    {
        start.ArgumentList.Add("-CentralEnrollmentFile"); start.ArgumentList.Add(enrollmentFile);
    }
    using var process = Process.Start(start) ?? throw new InvalidOperationException("점검 서비스 설치 프로세스를 시작하지 못했습니다.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = (await stdout).Trim();
    var error = (await stderr).Trim();
    if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
    return output;
}

static async Task WaitForCentralReportAsync(CentralEnrollmentResponse enrollment)
{
    var packageUri = new Uri(enrollment.PackageDownloadUri, UriKind.Absolute);
    var statusUri = new Uri(packageUri, "/api/device/self");
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Device",
        $"{enrollment.RoomId}:{enrollment.DeviceId}:{enrollment.DeviceSecret}");
    var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            using var response = await client.GetAsync(statusUri);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                var root = document.RootElement;
                if (root.TryGetProperty("connected", out var connected) && connected.GetBoolean()
                    && root.TryGetProperty("reporting", out var reporting) && reporting.GetBoolean()) return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
    throw new InvalidOperationException("서비스 설치는 완료됐지만 2분 안에 중앙 연결과 첫 상태 보고를 확인하지 못했습니다. 네트워크를 확인한 뒤 설치파일을 다시 실행해 주세요.");
}

static string FindPackageRoot(string extracted)
{
    if (File.Exists(Path.Combine(extracted, "deployment-preseed.json"))) return extracted;
    var matches = Directory.EnumerateFiles(extracted, "deployment-preseed.json", SearchOption.AllDirectories).Take(2).ToArray();
    if (matches.Length != 1) throw new InvalidDataException("압축 패키지의 루트를 한 곳으로 확인할 수 없습니다.");
    return Path.GetDirectoryName(matches[0])!;
}

static Uri ResolveControlPlane(string root)
{
    var configured = Environment.GetEnvironmentVariable("SILENTSTREAM_CONTROL_PLANE_URI");
    var file = Path.Combine(root, "control-plane.json");
    if (string.IsNullOrWhiteSpace(configured) && File.Exists(file))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        configured = document.RootElement.TryGetProperty("baseUri", out var value) ? value.GetString() : null;
    }
    configured ??= "https://admin.silentstream.win";
    if (!Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
        || uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        throw new InvalidDataException("중앙 관리 서버 주소는 HTTPS여야 합니다.");
    return uri;
}

static string? RoomFromFileName(string? path)
{
    return InstallerFileName.RoomFromPath(path);
}

static int ReserveLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static string LoadOrCreateInstallationId()
{
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MediaCaptureHelper", "Bootstrap");
    Directory.CreateDirectory(root);
    var path = Path.Combine(root, "installation-id.txt");
    if (File.Exists(path))
    {
        var existing = File.ReadAllText(path).Trim();
        if (Guid.TryParseExact(existing, "N", out _)) return existing;
    }
    var created = Guid.NewGuid().ToString("N");
    File.WriteAllText(path, created);
    return created;
}

static string Token(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
    .TrimEnd('=').Replace('+', '-').Replace('/', '_');

static bool CryptographicEquals(string? left, string? right)
{
    if (left is null || right is null) return false;
    var a = Encoding.UTF8.GetBytes(left);
    var b = Encoding.UTF8.GetBytes(right);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

static bool SafeHttpsUri(string? value, out Uri uri)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out uri!) || !string.IsNullOrEmpty(uri.UserInfo)
        || !string.IsNullOrEmpty(uri.Fragment)) return false;
    return uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp;
}

static string DescribeFailure(Exception exception)
{
    var messages = new List<string>();
    var sslFailure = false;
    for (var current = exception; current is not null && messages.Count < 6; current = current.InnerException)
    {
        if (!string.IsNullOrWhiteSpace(current.Message)
            && !messages.Contains(current.Message, StringComparer.Ordinal)) messages.Add(current.Message.Trim());
        sslFailure |= current is AuthenticationException
            || current.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || current.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase);
    }
    var detail = string.Join("\n→ ", messages);
    if (sslFailure)
        detail += "\n\nWindows 날짜·시간이 정확한지 확인하고, 학교망의 HTTPS 검사 또는 인증서 차단 여부를 확인해 주세요.";
    return detail;
}
