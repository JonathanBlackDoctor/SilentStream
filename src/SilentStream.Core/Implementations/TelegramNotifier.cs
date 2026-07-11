using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilentStream.Core.Contracts;
using SilentStream.Core.Models;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Telegram push transport (원격 컨트롤러 개선 Phase 1): POSTs one message to
/// https://api.telegram.org/bot{token}/sendMessage. The bot token follows the same
/// paste-once → DPAPI-encrypt → wipe-plaintext lifecycle as the Cloudflare tunnel token
/// (RemoteControlServer.ResolveCloudflareToken); the chat id is not a secret. Unconfigured or
/// failing delivery returns false and logs — it never throws into the health pipeline. The
/// HttpClient is injectable so tests stub the Telegram API without touching the network.
/// </summary>
public sealed class TelegramNotifier : INotifier, IDisposable
{
    // Property names serialize as-is (Telegram wants snake_case, pinned via JsonPropertyName).
    // Relaxed escaping keeps the Korean message text readable on the wire instead of \uXXXX —
    // safe here: the body is an HTTPS JSON payload, never embedded in HTML.
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IConfigStore _configStore;
    private readonly ITokenProtector _tokenProtector;
    private readonly ILogService _log;
    private readonly HttpClient _http;

    /// <summary>Production constructor (DI selects this — HttpClient is not registered).</summary>
    public TelegramNotifier(IConfigStore configStore, ITokenProtector tokenProtector, ILogService log)
        : this(configStore, tokenProtector, log, new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
    {
    }

    /// <summary>Test seam: inject an HttpClient over a stub handler so tests assert the request offline.</summary>
    public TelegramNotifier(
        IConfigStore configStore, ITokenProtector tokenProtector, ILogService log, HttpClient http)
    {
        _configStore = configStore;
        _tokenProtector = tokenProtector;
        _log = log;
        _http = http;
    }

    public async Task<bool> SendAsync(string message, CancellationToken ct)
    {
        var notif = _configStore.Load().Notifications;
        if (!notif.Enabled)
        {
            return false;
        }

        var token = ResolveBotToken(notif);
        var chatId = (notif.TelegramChatId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token) || chatId.Length == 0)
        {
            return false; // unconfigured — silently a no-op so the health pipeline stays quiet
        }

        try
        {
            var body = JsonSerializer.Serialize(new SendMessagePayload(chatId, message), Json);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http
                .PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Never log the token; the status code is enough to diagnose (401=token, 400=chat id).
                _log.Warn($"텔레그램 전송 실패: HTTP {(int)response.StatusCode} — 봇 토큰/채팅 ID를 확인하세요.");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false; // shutdown or timeout — nothing to report
        }
        catch (Exception ex)
        {
            _log.Warn($"텔레그램 전송 오류: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Encrypts a hand-pasted plaintext token at rest right away (called once at startup), instead
    /// of waiting for the first send — with notifications disabled a send may never happen, and the
    /// documented guarantee is that plaintext never survives past the first app run.
    /// </summary>
    public void MigratePlaintextTokenAtRest() => ResolveBotToken(_configStore.Load().Notifications);

    /// <summary>
    /// Resolves the bot token, mirroring RemoteControlServer.ResolveCloudflareToken: freshly pasted
    /// plaintext is DPAPI-encrypted into the *Enc field and wiped so it is never left at rest;
    /// otherwise the at-rest ciphertext is decrypted for use.
    /// </summary>
    private string? ResolveBotToken(NotificationsConfig notif)
    {
        if (!string.IsNullOrWhiteSpace(notif.TelegramBotToken))
        {
            var plain = notif.TelegramBotToken.Trim();
            try
            {
                var enc = _tokenProtector.Protect(plain);
                _configStore.Update(c =>
                {
                    // Re-validate under the store lock: if a concurrent settings save replaced or
                    // deleted the token since our snapshot, don't clobber it with the stale value.
                    if ((c.Notifications.TelegramBotToken ?? string.Empty).Trim() == plain)
                    {
                        c.Notifications.TelegramBotTokenEnc = enc;
                        c.Notifications.TelegramBotToken = string.Empty;
                    }
                });
                _log.Info("텔레그램 봇 토큰을 DPAPI로 암호화 저장했습니다(평문 제거).");
            }
            catch (Exception ex)
            {
                _log.Error("텔레그램 봇 토큰 암호화 실패 — 이번 실행은 평문 토큰으로 진행합니다.", ex);
            }
            return plain;
        }

        if (!string.IsNullOrWhiteSpace(notif.TelegramBotTokenEnc))
        {
            try
            {
                return _tokenProtector.Unprotect(notif.TelegramBotTokenEnc);
            }
            catch (Exception ex)
            {
                _log.Error("텔레그램 봇 토큰 복호화 실패 — 알림을 보낼 수 없습니다.", ex);
                return null;
            }
        }

        return null;
    }

    public void Dispose() => _http.Dispose();

    private sealed record SendMessagePayload(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text);
}
