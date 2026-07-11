using System.Net;
using System.Text;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

/// <summary>
/// TelegramNotifier tests. HTTP is stubbed with a capturing handler (no network) and DPAPI is
/// replaced by a reversible Base64 fake (the real DpapiTokenProtector is Windows-only), so the
/// request URL/body and the paste-once → encrypt → wipe token lifecycle are asserted offline.
/// </summary>
public class TelegramNotifierTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-tg-").FullName;
    private readonly ConfigStore _configStore;
    private readonly FakeHandler _handler = new();
    private readonly Base64Protector _protector = new();

    public TelegramNotifierTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        _configStore.Save(AppConfig.CreateDefault());
    }

    private TelegramNotifier Create() =>
        new(_configStore, _protector, new LogService(), new HttpClient(_handler));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Sends_to_the_bot_url_with_chat_id_and_text()
    {
        _configStore.Update(c =>
        {
            c.Notifications.TelegramBotTokenEnc = _protector.Protect("123:ABC");
            c.Notifications.TelegramChatId = "42";
        });

        var ok = await Create().SendAsync("무음 감지", CancellationToken.None);

        Assert.True(ok);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", request.Uri);
        Assert.Contains("\"chat_id\":\"42\"", request.Body);
        Assert.Contains("무음 감지", request.Body);
    }

    [Fact]
    public async Task Unconfigured_returns_false_without_calling_http()
    {
        var ok = await Create().SendAsync("x", CancellationToken.None);

        Assert.False(ok);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Disabled_returns_false_even_when_configured()
    {
        _configStore.Update(c =>
        {
            c.Notifications.Enabled = false;
            c.Notifications.TelegramBotTokenEnc = _protector.Protect("123:ABC");
            c.Notifications.TelegramChatId = "42";
        });

        Assert.False(await Create().SendAsync("x", CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Pasted_plaintext_token_is_encrypted_and_wiped_on_first_send()
    {
        // The config.json hand-edit path: plaintext pasted once, encrypted + wiped on first use,
        // mirroring the Cloudflare tunnel token lifecycle.
        _configStore.Update(c =>
        {
            c.Notifications.TelegramBotToken = "123:PLAIN";
            c.Notifications.TelegramChatId = "42";
        });

        var ok = await Create().SendAsync("x", CancellationToken.None);

        Assert.True(ok);
        Assert.Contains("/bot123:PLAIN/", Assert.Single(_handler.Requests).Uri);
        var saved = _configStore.Load().Notifications;
        Assert.Equal(string.Empty, saved.TelegramBotToken);            // plaintext wiped
        Assert.Equal("123:PLAIN", _protector.Unprotect(saved.TelegramBotTokenEnc)); // at-rest form usable
    }

    [Fact]
    public void Startup_migration_encrypts_and_wipes_plaintext_even_when_disabled()
    {
        // 알림이 꺼져 있어도(전송이 없어도) 평문 토큰은 첫 기동에서 즉시 암호화돼야 한다.
        _configStore.Update(c =>
        {
            c.Notifications.Enabled = false;
            c.Notifications.TelegramBotToken = "123:PLAIN";
        });

        Create().MigratePlaintextTokenAtRest();

        var saved = _configStore.Load().Notifications;
        Assert.Equal(string.Empty, saved.TelegramBotToken);
        Assert.Equal("123:PLAIN", _protector.Unprotect(saved.TelegramBotTokenEnc));
        Assert.Empty(_handler.Requests); // 네트워크 호출 없음
    }

    [Fact]
    public async Task Http_error_status_returns_false()
    {
        _configStore.Update(c =>
        {
            c.Notifications.TelegramBotTokenEnc = _protector.Protect("123:ABC");
            c.Notifications.TelegramChatId = "42";
        });
        _handler.StatusCode = HttpStatusCode.Unauthorized;

        Assert.False(await Create().SendAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task Network_exception_returns_false_instead_of_throwing()
    {
        _configStore.Update(c =>
        {
            c.Notifications.TelegramBotTokenEnc = _protector.Protect("123:ABC");
            c.Notifications.TelegramChatId = "42";
        });
        _handler.Throw = new HttpRequestException("연결 실패");

        Assert.False(await Create().SendAsync("x", CancellationToken.None));
    }

    // ---- fakes ----

    /// <summary>Captures every request (URL + body) and returns a canned response.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Requests { get; } = [];
        public HttpStatusCode StatusCode = HttpStatusCode.OK;
        public Exception? Throw;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add((request.RequestUri!.ToString(), body));
            }
            return new HttpResponseMessage(StatusCode) { Content = new StringContent("{\"ok\":true}") };
        }
    }

    /// <summary>Reversible stand-in for DPAPI so the token lifecycle is testable off-Windows.</summary>
    private sealed class Base64Protector : ITokenProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string ciphertext) => Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext));
    }
}
