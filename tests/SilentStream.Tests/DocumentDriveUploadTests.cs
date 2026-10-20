using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.UsbDocuments;
using Xunit;

namespace SilentStream.Tests;

public sealed class DocumentDriveUploadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "drive-doc-test-" + Guid.NewGuid().ToString("N"));
    private readonly Config _config = new();
    private readonly Client _client = new();
    private readonly UsbDocumentArchive _archive;
    private string State => Path.Combine(_root, "uploads.json");
    public DocumentDriveUploadTests()
    {
        Directory.CreateDirectory(_root);
        _config.Value.UsbDocuments.Folder = Path.Combine(_root, "archive");
        _config.Value.Provisioning.RoomId = "m112";
        _config.Value.UsbDocuments.Drive = new() { Enabled = true, Account = "class@example.test", FolderId = "test-folder-12345",
            RoomId = "m112", ReaderEmail = "reader@example.test", PolicyRevision = new string('a', 32), AppliedRevision = new string('a', 32) };
        _archive = new(_config, Path.Combine(_root, "catalog.json"), 100_000_000);
    }
    private DocumentDriveUpload Worker() => new(_archive, _config, _client, State, startWorker: false);
    private async Task Add(string name = "slides.pdf", byte[]? data = null)
    {
        var path = Path.Combine(_root, name); File.WriteAllBytes(path, data ?? [1, 2, 3]);
        _archive.Observe(new(path, "usb", 1, DateTimeOffset.UtcNow));
        using var deadline = new CancellationTokenSource(5000);
        while (!_archive.Snapshot().Items.Any(x => x.Name == name && x.Status is "ready" or "duplicate"))
            await Task.Delay(10, deadline.Token);
    }
    [Fact]
    public async Task CompletedFilesUploadOnceAcrossDuplicatesAndRestarts()
    {
        await Add(); await Add("copy.pdf");
        using (var worker = Worker()) { await worker.RunOnceAsync(); await worker.RunOnceAsync(); }
        using (var worker = Worker()) await worker.RunOnceAsync();
        Assert.Equal(1, _client.Uploads); Assert.Equal(1, _client.Allocations);
        Assert.Equal("uploaded", JsonSerializer.Deserialize<DocumentDriveUploadRecord[]>(File.ReadAllText(State))![0].Status);
    }
    [Fact]
    public async Task DisabledModeDoesNotAuthenticateOrUpload()
    {
        await Add(); _config.Value.UsbDocuments.Drive.Enabled = false;
        using var worker = Worker(); await worker.RunOnceAsync();
        Assert.Equal(0, _client.Validations); Assert.False(File.Exists(State));
    }
    [Fact]
    public async Task FailuresPersistAndExplicitRetryReusesPreallocatedId()
    {
        await Add(); _client.FailUpload = true;
        using (var worker = Worker()) { await worker.RunOnceAsync(); await worker.RunOnceAsync(); }
        using var restored = Worker(); await restored.RunOnceAsync();
        Assert.Equal(1, _client.Uploads); Assert.Equal(1, _client.Allocations);
        _client.FailUpload = false; restored.RetryFailed(); await restored.RunOnceAsync();
        Assert.Equal(2, _client.Uploads); Assert.Equal(1, _client.Allocations);
        Assert.Single(_client.Ids.Distinct());
    }
    [Fact]
    public async Task IdentityFailurePreventsReadingAndAllocatingRemoteFile()
    {
        await Add(); _client.WrongAccount = true;
        using var worker = Worker(); await worker.RunOnceAsync(); await worker.RunOnceAsync();
        Assert.Equal(1, _client.Validations); Assert.Equal(0, _client.Allocations); Assert.Equal(0, _client.Uploads);
        Assert.Contains("다릅니다", worker.Status);
    }
    [Fact]
    public async Task CorruptStateIsPreservedAndStopsUploads()
    {
        await Add(); File.WriteAllText(State, "{bad");
        using var worker = Worker(); await worker.RunOnceAsync(); worker.RetryFailed(); await worker.RunOnceAsync();
        Assert.Equal("{bad", File.ReadAllText(State)); Assert.Equal(0, _client.Validations);
    }
    [Fact]
    public async Task ChangedArchiveIsRejectedEvenWhenLengthAndTimestampMatch()
    {
        await Add(); var path = Directory.GetFiles(_config.Value.UsbDocuments.Folder, "*.pdf", SearchOption.AllDirectories).Single();
        var stamp = File.GetLastWriteTimeUtc(path); File.WriteAllBytes(path, [9, 8, 7]); File.SetLastWriteTimeUtc(path, stamp);
        using var worker = Worker(); await worker.RunOnceAsync();
        Assert.Equal(0, _client.Uploads); Assert.Equal(0, _client.Allocations);
    }
    [Fact]
    public async Task TargetChangeDoesNotReuseOldSuccessOrRemoteId()
    {
        await Add(); using var worker = Worker(); await worker.RunOnceAsync();
        _config.Value.UsbDocuments.Drive.FolderId = "second-folder-12345";
        await worker.RunOnceAsync(); Assert.Equal(2, _client.Uploads); Assert.Equal(2, _client.Allocations);
    }
    [Fact]
    public async Task InteractiveConnectionMustValidateBeforeEnabling()
    {
        using var worker = Worker(); _config.Value.UsbDocuments.Drive.Enabled = false; _client.WrongAccount = true;
        await Assert.ThrowsAsync<DriveDocumentException>(() => worker.ConfigureAsync(true, "class@example.test", "https://drive.google.com/drive/u/2/folders/test-folder-12345", default));
        Assert.False(worker.Settings.Enabled); Assert.Equal(1, _client.Connections);
    }
    [Theory]
    [InlineData("https://evil.example/folders/test-folder-12345")]
    [InlineData("https://drive.google.com/file/d/test-folder-12345/view")]
    [InlineData("../secret")]
    public void RejectsInvalidFolderTargets(string input) => Assert.Throws<ArgumentException>(() => DocumentDriveUpload.FolderId(input));

    [Fact]
    public async Task GoogleClientChecksAccountBeforeInspectingTarget()
    {
        var requests = 0;
        using var client = Google(async request =>
        {
            requests++; await Task.Yield();
            return Json(new { user = new { emailAddress = "personal@example.test" } });
        });
        await Assert.ThrowsAsync<DriveDocumentException>(() => client.ValidateAsync(_config.Value.UsbDocuments.Drive, default));
        Assert.Equal(1, requests);
    }
    [Theory]
    [InlineData("other@example.test", false, true)]
    [InlineData("class@example.test", true, true)]
    [InlineData("class@example.test", false, false)]
    public async Task GoogleClientRejectsForeignTrashedOrReadOnlyFolders(string owner, bool trashed, bool canAddChildren)
    {
        using var client = Google(request => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("about")
            ? Json(new { user = new { emailAddress = "class@example.test" } })
            : Json(new { mimeType = "application/vnd.google-apps.folder", trashed, owners = new[] { new { emailAddress = owner } }, capabilities = new { canAddChildren } })));
        await Assert.ThrowsAsync<DriveDocumentException>(() => client.ValidateAsync(_config.Value.UsbDocuments.Drive, default));
    }
    [Fact]
    public async Task ExistingVerifiedRemoteIdFinishesWithoutUploadingAgain()
    {
        var requests = 0; var file = FileInfo();
        using var client = Google(request => { requests++; return Task.FromResult(Json(Remote(file))); });
        await client.UploadAsync(_config.Value.UsbDocuments.Drive, file, new MemoryStream([1, 2, 3]), "remote-123", "md5", default);
        Assert.Equal(1, requests);
    }
    [Fact]
    public async Task RemoteChecksumMismatchDoesNotOverwriteExistingFile()
    {
        var file = FileInfo(); var requests = 0;
        using var client = Google(request => { requests++; return Task.FromResult(Json(Remote(file))); });
        await Assert.ThrowsAsync<DriveDocumentException>(() => client.UploadAsync(_config.Value.UsbDocuments.Drive, file, new MemoryStream([1, 2, 3]), "remote-123", "wrong-md5", default));
        Assert.Equal(1, requests);
    }
    [Fact]
    public async Task ResumableUploadPreservesIdAndBytesThenVerifiesReadback()
    {
        var file = FileInfo(); var requests = 0; var uploaded = false;
        using var client = Google(async request =>
        {
            requests++; Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            if (request.Method == HttpMethod.Get) return uploaded ? Json(Remote(file)) : new(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post)
            {
                var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                Assert.Equal("remote-123", metadata.GetProperty("id").GetString());
                Assert.Equal("test-folder-12345", metadata.GetProperty("parents")[0].GetString());
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri("https://www.googleapis.com/upload/drive/v3/files?upload_id=test"); return response;
            }
            Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content!.ReadAsByteArrayAsync());
            uploaded = true; return Json(new { id = "remote-123" });
        });
        await client.UploadAsync(_config.Value.UsbDocuments.Drive, file, new MemoryStream([1, 2, 3]), "remote-123", "md5", default);
        Assert.True(uploaded); Assert.Equal(4, requests);
    }
    [Fact]
    public async Task ForeignUploadLocationNeverReceivesTokenOrDocumentBytes()
    {
        var requests = 0;
        using var client = Google(request =>
        {
            requests++;
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Headers = { Location = new Uri("https://evil.example/upload") } };
            return Task.FromResult(response);
        });
        await Assert.ThrowsAsync<DriveDocumentException>(() => client.UploadAsync(_config.Value.UsbDocuments.Drive, FileInfo(), new MemoryStream([1, 2, 3]), "remote-123", "md5", default));
        Assert.Equal(2, requests);
    }
    [Fact]
    public async Task CentralModeDisablesLocalCredentialsAndPermissionWorkAcrossRestart()
    {
        await Add();
        using (var worker = Worker())
        {
            await worker.ApplyPolicyAsync(new("m112", "class@example.test", "test-folder-12345", "reader@example.test", new string('c',32), Central:true), default);
            await worker.RunOnceAsync();
            Assert.False(worker.Settings.Enabled); Assert.True(worker.PolicyStatus.Central);
            await Assert.ThrowsAsync<ArgumentException>(()=>worker.ConfigureAsync(true,"class@example.test","test-folder-12345",default));
        }
        using (var worker = Worker()) await worker.RunOnceAsync();
        Assert.Equal(0,_client.Connections); Assert.Equal(0,_client.Restrictions); Assert.Equal(0,_client.Uploads);
    }
    [Fact]
    public async Task SameRevisionCannotSilentlySwitchBetweenCentralAndLocalAuthentication()
    {
        using var worker=Worker();
        await Assert.ThrowsAsync<ArgumentException>(()=>worker.ApplyPolicyAsync(new("m112","class@example.test","test-folder-12345","reader@example.test",new string('a',32),Central:true),default));
    }
    [Fact]
    public async Task LegacyFolderCannotUploadWithoutCentralDevicePolicy()
    {
        await Add(); _config.Value.UsbDocuments.Drive.PolicyRevision = "";
        using var worker = Worker(); await worker.RunOnceAsync();
        Assert.Equal(0, _client.Uploads); Assert.Equal(0, _client.Validations);
        await Assert.ThrowsAsync<ArgumentException>(()=>worker.ConfigureAsync(true,"class@example.test","test-folder-12345",default));
    }
    [Fact]
    public async Task PolicyMustMatchProvisionedDeviceAndCannotReplaceItsFolder()
    {
        using var worker = Worker();
        await Assert.ThrowsAsync<ArgumentException>(()=>worker.ApplyPolicyAsync(new("m111","class@example.test","test-folder-12345","reader@example.test",new string('b',32)),default));
        await Assert.ThrowsAsync<ArgumentException>(()=>worker.ApplyPolicyAsync(new("m112","class@example.test","other-folder-12345","reader@example.test",new string('b',32)),default));
        Assert.Equal(new string('a',32),worker.Settings.PolicyRevision);
    }
    [Fact]
    public async Task PendingPermissionsBlockUploadAndPersistFailureUntilExplicitRetry()
    {
        await Add(); using var worker = Worker();
        await worker.ApplyPolicyAsync(new("m112","class@example.test","test-folder-12345","next@example.test",new string('b',32)),default);
        _client.FailPermissions=true;
        await worker.RunOnceAsync();await worker.RunOnceAsync();
        Assert.Equal(1,_client.Restrictions);Assert.Equal(0,_client.Uploads);
        using (var restored=Worker())await restored.RunOnceAsync();
        Assert.Equal(1,_client.Restrictions);
        _client.FailPermissions=false;worker.RetryFailed();await worker.RunOnceAsync();
        Assert.Equal(2,_client.Restrictions);Assert.Equal(1,_client.Uploads);
        Assert.Equal(new string('b',32),worker.PolicyStatus.AppliedRevision);
    }
    [Fact]
    public async Task ClearingReaderRevokesAccessEvenWhenUploadsAreDisabled()
    {
        await Add();using var worker=Worker();
        await worker.ApplyPolicyAsync(new("m112","class@example.test","test-folder-12345","",new string('b',32)),default);
        await worker.RunOnceAsync();
        Assert.False(worker.Settings.Enabled);Assert.Equal(1,_client.Restrictions);Assert.Equal(0,_client.Uploads);
        Assert.Equal(new string('b',32),worker.PolicyStatus.AppliedRevision);
    }
    [Fact]
    public async Task ExplicitReconnectRechecksPreviouslyAppliedPermissions()
    {
        using var worker = Worker();
        await worker.ConfigureAsync(true, "class@example.test", "test-folder-12345", default);
        Assert.Equal(1, _client.Restrictions);
    }
    [Fact]
    public async Task LocalSettingsCannotOverrideCentralFolder()
    {
        using var worker=Worker();
        await Assert.ThrowsAsync<ArgumentException>(()=>worker.ConfigureAsync(true,"personal@example.test","test-folder-12345",default));
        await Assert.ThrowsAsync<ArgumentException>(()=>worker.ConfigureAsync(true,"class@example.test","other-folder-12345",default));
        Assert.Equal(0,_client.Connections);
    }
    [Fact]
    public async Task GoogleAclRemovesPublicAndForeignAccessAndGrantsOnlyReaderWithoutEmail()
    {
        var permissions=new List<object>{
            new{id="owner",type="user",role="owner",emailAddress="class@example.test"},
            new{id="public",type="anyone",role="reader",emailAddress=""},
            new{id="old",type="user",role="writer",emailAddress="old@example.test"}};
        var deleted=new List<string>();var grants=0;
        using var client=Google(async request=>{
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("about"))return Json(new{user=new{emailAddress="class@example.test"}});
            if(!path.Contains("permissions"))return Json(new{mimeType="application/vnd.google-apps.folder",trashed=false,
                owners=new[]{new{emailAddress="class@example.test"}},capabilities=new{canAddChildren=true}});
            if(request.Method==HttpMethod.Get)return Json(new{permissions});
            if(request.Method==HttpMethod.Delete){
                var id=path.Split('/').Last();deleted.Add(id);
                permissions.RemoveAll(p=>JsonSerializer.SerializeToElement(p).GetProperty("id").GetString()==id);
                return new(HttpStatusCode.NoContent);
            }
            Assert.Equal(HttpMethod.Post,request.Method);Assert.Contains("sendNotificationEmail=false",request.RequestUri.Query);
            var grant=JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal("reader@example.test",grant.GetProperty("emailAddress").GetString());
            Assert.Equal("reader",grant.GetProperty("role").GetString());grants++;
            permissions.Add(new{id="reader",type="user",role="reader",emailAddress="reader@example.test"});
            return Json(new{id="reader"});
        });
        await client.RestrictAccessAsync(_config.Value.UsbDocuments.Drive,default);
        Assert.Equal(new[]{"public","old"},deleted);Assert.Equal(1,grants);
        await client.ValidateAsync(_config.Value.UsbDocuments.Drive,default);
    }
    [Fact]
    public async Task GoogleAclFailsClosedOnInheritedPublicPermission()
    {
        var mutations=0;
        using var client=Google(request=>{
            if(request.Method!=HttpMethod.Get)mutations++;
            if(request.RequestUri!.AbsolutePath.EndsWith("about"))return Task.FromResult(Json(new{user=new{emailAddress="class@example.test"}}));
            if(request.RequestUri.AbsolutePath.Contains("permissions"))return Task.FromResult(Json(new{permissions=new object[]{
                new{id="owner",type="user",role="owner",emailAddress="class@example.test"},
                new{id="public",type="anyone",role="reader",permissionDetails=new[]{new{inherited=true}}}}}));
            return Task.FromResult(Json(new{mimeType="application/vnd.google-apps.folder",trashed=false,
                owners=new[]{new{emailAddress="class@example.test"}},capabilities=new{canAddChildren=true}}));
        });
        await Assert.ThrowsAsync<DriveDocumentException>(()=>client.RestrictAccessAsync(_config.Value.UsbDocuments.Drive,default));
        Assert.Equal(0,mutations);
    }

    private static UsbDocumentInfo FileInfo() => new("id", "slides.pdf", "2026-10-20", "ready", 3, 3, "sha", null, DateTimeOffset.UtcNow);
    private static object Remote(UsbDocumentInfo file) => new { id = "remote-123", size = "3", md5Checksum = "md5", parents = new[] { "test-folder-12345" }, trashed = false, appProperties = new { silentStreamSha256 = file.Sha256 } };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static GoogleDocumentDriveClient Google(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(new Protector(), new HttpClient(new Handler(send)), _ => Task.FromResult("test-token"));
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
    private sealed class Protector : ITokenProtector
    { public string Protect(string value) => value; public string Unprotect(string value) => value; }
    private sealed class Client : IDocumentDriveClient
    {
        public int Connections, Validations, Allocations, Uploads;
        public bool WrongAccount, FailUpload;
        public readonly List<string> Ids = [];
        public Task ConnectAsync(DocumentDriveConfig settings, CancellationToken ct) { Connections++; return ValidateAsync(settings, ct); }
        public int Restrictions;
        public bool FailPermissions;
        public Task RestrictAccessAsync(DocumentDriveConfig settings, CancellationToken ct)
        { Restrictions++; if (FailPermissions) throw new DriveDocumentException("공유 권한 확인 실패"); return Task.CompletedTask; }
        public Task ValidateAsync(DocumentDriveConfig settings, CancellationToken ct)
        { Validations++; if (WrongAccount) throw new DriveDocumentException("계정이 다릅니다."); return Task.CompletedTask; }
        public Task<string> AllocateIdAsync(CancellationToken ct) { Allocations++; return Task.FromResult("remote-" + Allocations); }
        public Task UploadAsync(DocumentDriveConfig settings, UsbDocumentInfo file, Stream content, string remoteId, string md5, CancellationToken ct)
        { Uploads++; Ids.Add(remoteId); if (FailUpload) throw new IOException("network"); return Task.CompletedTask; }
    }
    private sealed class Config : IConfigStore
    { public AppConfig Value = new(); public AppConfig Load() => Value; public void Save(AppConfig value) => Value = value; public void Update(Action<AppConfig> update) => update(Value); }
    public void Dispose() { _archive.Dispose(); Directory.Delete(_root, true); }
}
