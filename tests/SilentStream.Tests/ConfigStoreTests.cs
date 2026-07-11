using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-cfg-").FullName;

    private string ConfigPath => Path.Combine(_dir, "config.json");

    [Fact]
    public void Load_without_file_returns_defaults_with_recording_folder_resolved()
    {
        var store = new ConfigStore(ConfigPath);
        var config = store.Load();

        Assert.Equal(6, config.Version); // schema v6 (폰 푸시 알림)
        Assert.False(string.IsNullOrWhiteSpace(config.Recording.Folder));
        Assert.Equal("Ctrl+Shift+F12", config.Hotkey);
        Assert.False(config.ShowStatusBox); // 방송 상태 박스는 기본 숨김
        // A true fresh install (no config file) seeds the room label from the machine name so each
        // PC starts distinguishable; the operator renames it to the 호실명 in settings.
        Assert.Equal(Environment.MachineName, config.DeviceName);
    }

    [Fact]
    public void Existing_file_is_not_stamped_with_a_hostname_on_migration()
    {
        // A pre-v5 file (e.g. an already-deployed PC) must keep DeviceName empty — only a brand-new
        // install seeds the hostname. Bumping merely advances the version.
        File.WriteAllText(ConfigPath, """{ "version": 4 }""");

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(6, loaded.Version);
        Assert.Equal(string.Empty, loaded.DeviceName);
    }

    [Fact]
    public void Save_then_load_roundtrips_all_sections()
    {
        var store = new ConfigStore(ConfigPath);
        var config = AppConfig.CreateDefault();
        config.YouTube.RefreshTokenEnc = "BLOB==";
        config.YouTube.TitleTemplate = "라이브 - {yyyy-MM-dd}";
        config.Encoding.PreferredGpu = "nvenc";
        config.Encoding.ResourceLimit = "50";
        config.Audio.SystemVolume = 0.8;
        config.Audio.MicVolume = 0.5;
        config.Audio.MicDeviceId = "mic-42";
        config.Recording.Folder = Path.Combine(_dir, "rec");
        config.Recording.MaxSizeGb = 250;
        config.Recording.RetentionDays = 3;
        config.Hotkey = "Ctrl+Alt+F9";
        config.Autostart = "scheduler";
        config.DeviceName = "201호";
        config.ShowStatusBox = true;
        config.Notifications.Enabled = false;
        config.Notifications.TelegramBotTokenEnc = "TGBLOB==";
        config.Notifications.TelegramChatId = "12345";
        config.Notifications.NotifyLevel = "critical";

        store.Save(config);
        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal("BLOB==", loaded.YouTube.RefreshTokenEnc);
        Assert.Equal("라이브 - {yyyy-MM-dd}", loaded.YouTube.TitleTemplate);
        Assert.Equal("nvenc", loaded.Encoding.PreferredGpu);
        Assert.Equal("50", loaded.Encoding.ResourceLimit);
        Assert.Equal(0.8, loaded.Audio.SystemVolume);
        Assert.Equal(0.5, loaded.Audio.MicVolume);
        Assert.Equal("mic-42", loaded.Audio.MicDeviceId);
        Assert.Equal(Path.Combine(_dir, "rec"), loaded.Recording.Folder);
        Assert.Equal(250, loaded.Recording.MaxSizeGb);
        Assert.Equal(3, loaded.Recording.RetentionDays);
        Assert.Equal("Ctrl+Alt+F9", loaded.Hotkey);
        Assert.Equal("scheduler", loaded.Autostart);
        Assert.Equal("201호", loaded.DeviceName);
        Assert.True(loaded.ShowStatusBox);
        Assert.False(loaded.Notifications.Enabled);
        Assert.Equal("TGBLOB==", loaded.Notifications.TelegramBotTokenEnc);
        Assert.Equal("12345", loaded.Notifications.TelegramChatId);
        Assert.Equal("critical", loaded.Notifications.NotifyLevel);
    }

    [Fact]
    public void V5_file_gains_the_notifications_section_with_defaults()
    {
        // A pre-v6 file (or one hand-edited to an explicit null section) must load with the
        // notification defaults instead of NRE-ing out of the auto-start path.
        File.WriteAllText(ConfigPath, """{ "version": 5, "notifications": null }""");

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(6, loaded.Version);
        Assert.True(loaded.Notifications.Enabled);
        Assert.Equal(string.Empty, loaded.Notifications.TelegramBotTokenEnc);
        Assert.Equal("warn", loaded.Notifications.NotifyLevel);
    }

    [Fact]
    public void Blank_notify_level_is_normalized_to_warn_on_load()
    {
        File.WriteAllText(ConfigPath, """{ "version": 5, "notifications": { "notifyLevel": "" } }""");
        Assert.Equal("warn", new ConfigStore(ConfigPath).Load().Notifications.NotifyLevel);

        File.WriteAllText(ConfigPath, """{ "version": 5, "notifications": { "notifyLevel": "critical" } }""");
        Assert.Equal("critical", new ConfigStore(ConfigPath).Load().Notifications.NotifyLevel);
    }

    [Fact]
    public void Save_writes_camelCase_json_matching_documented_schema()
    {
        var store = new ConfigStore(ConfigPath);
        store.Save(AppConfig.CreateDefault());

        var json = File.ReadAllText(ConfigPath);
        Assert.Contains("\"youtube\"", json);
        Assert.Contains("\"refreshTokenEnc\"", json);
        Assert.Contains("\"recording\"", json);
        Assert.Contains("\"maxSizeGb\"", json);
        Assert.Contains("\"retentionDays\"", json);
    }

    [Fact]
    public void Default_cloudflare_protocol_is_http2()
    {
        var config = new ConfigStore(ConfigPath).Load();

        // http2 (TCP/443) is the default so UDP-blocked networks still reach the tunnel (2nd field test).
        Assert.Equal("http2", config.Remote.CloudflareProtocol);
    }

    [Fact]
    public void Blank_cloudflare_protocol_is_normalized_to_http2_on_load()
    {
        // A config written before the field existed leaves it blank; it must not pass an empty
        // --protocol. An explicit "quic" is preserved.
        File.WriteAllText(ConfigPath, """{ "version": 4, "remote": { "mode": "cloudflare", "cloudflareProtocol": "" } }""");
        Assert.Equal("http2", new ConfigStore(ConfigPath).Load().Remote.CloudflareProtocol);

        File.WriteAllText(ConfigPath, """{ "version": 4, "remote": { "mode": "cloudflare", "cloudflareProtocol": "quic" } }""");
        Assert.Equal("quic", new ConfigStore(ConfigPath).Load().Remote.CloudflareProtocol);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_returned()
    {
        File.WriteAllText(ConfigPath, "{ not json !!!");
        var store = new ConfigStore(ConfigPath);

        var config = store.Load();

        Assert.Equal(6, config.Version); // defaults are schema v6
        Assert.True(File.Exists(ConfigPath + ".bak"));
        // A corrupted (but pre-existing) config must NOT be treated as a fresh install: the room
        // label stays empty rather than being silently stamped with this machine's hostname.
        Assert.Equal(string.Empty, config.DeviceName);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
