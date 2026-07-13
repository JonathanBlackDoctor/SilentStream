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

        Assert.Equal(9, config.Version); // schema v9 (호실 자동 프로비저닝)
        Assert.False(string.IsNullOrWhiteSpace(config.Recording.Folder));
        Assert.Equal("Ctrl+Shift+F12", config.Hotkey);
        Assert.False(config.ShowStatusBox); // 방송 상태 박스는 기본 숨김
        // A true fresh install (no config file) seeds the room label from the machine name so each
        // PC starts distinguishable; the operator renames it to the 호실명 in settings.
        Assert.Equal(Environment.MachineName, config.DeviceName);
    }

    [Fact]
    public void Fresh_install_seeds_the_builtin_weekday_timetable()
    {
        var config = new ConfigStore(ConfigPath).Load();

        // Mon–Fri get the built-in 8-period day; weekends stay empty (operator adds them if needed).
        foreach (var day in new[] { "Mon", "Tue", "Wed", "Thu", "Fri" })
        {
            var entries = config.Periods.WeekdayDefaults[day];
            Assert.Equal(8, entries.Count);
            Assert.Equal("08:25:00", entries[0].Start);
            Assert.Equal("09:25:00", entries[0].End);
            // Lunch 12:25~13:25 is a plain gap between periods 4 and 5, not a period row.
            Assert.Equal("12:25:00", entries[3].End);
            Assert.Equal("13:25:00", entries[4].Start);
            Assert.Equal("17:25:00", entries[7].End);
        }
        Assert.False(config.Periods.WeekdayDefaults.ContainsKey("Sat"));
        Assert.False(config.Periods.WeekdayDefaults.ContainsKey("Sun"));
    }

    [Fact]
    public void Pre_v8_file_with_empty_timetable_is_seeded_on_migration()
    {
        File.WriteAllText(ConfigPath, """{ "version": 7 }""");

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(9, loaded.Version);
        Assert.Equal(8, loaded.Periods.WeekdayDefaults["Mon"].Count);
        Assert.True(loaded.Periods.RequireApproval);
        Assert.Equal(15, loaded.Periods.AutoApproveMinutes);
    }

    [Fact]
    public void Pre_v8_file_with_an_operator_schedule_is_never_reseeded()
    {
        // Any operator-entered row (even a single day) blocks the seed — the migration must not
        // silently overwrite or extend a schedule a deployed room is already cutting VODs from.
        File.WriteAllText(ConfigPath, """
            { "version": 7, "periods": { "weekdayDefaults": {
                "Mon": [ { "no": 1, "start": "09:00:00", "end": "09:50:00" } ] } } }
            """);

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(9, loaded.Version);
        var mon = Assert.Single(loaded.Periods.WeekdayDefaults["Mon"]);
        Assert.Equal("09:00:00", mon.Start);
        Assert.False(loaded.Periods.WeekdayDefaults.ContainsKey("Tue"));
    }

    [Fact]
    public void Explicit_split_approval_settings_are_preserved_on_load()
    {
        // AutoApproveMinutes null = wait forever (manual only); RequireApproval false = legacy
        // immediate cut. Both are deliberate operator choices and must survive a reload.
        File.WriteAllText(ConfigPath,
            """{ "version": 8, "periods": { "requireApproval": false, "autoApproveMinutes": null } }""");

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.False(loaded.Periods.RequireApproval);
        Assert.Null(loaded.Periods.AutoApproveMinutes);
    }

    [Fact]
    public void Existing_file_is_not_stamped_with_a_hostname_on_migration()
    {
        // A pre-v5 file (e.g. an already-deployed PC) must keep DeviceName empty — only a brand-new
        // install seeds the hostname. Bumping merely advances the version.
        File.WriteAllText(ConfigPath, """{ "version": 4 }""");

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(9, loaded.Version);
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
        config.Encoding.Adaptive.Enabled = false;
        config.Encoding.Adaptive.AutoRecover = false;
        config.Encoding.Adaptive.MaxLevel = 2;

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
        Assert.False(loaded.Encoding.Adaptive.Enabled);
        Assert.False(loaded.Encoding.Adaptive.AutoRecover);
        Assert.Equal(2, loaded.Encoding.Adaptive.MaxLevel);
    }

    [Fact]
    public void V6_file_gains_the_adaptive_section_with_defaults()
    {
        // A pre-v7 file (or one hand-edited to an explicit null section) must load with the
        // adaptive-quality defaults (자동 하강 켬 / 자동 복귀 켬 / 전체 사다리, D-AQ1/2).
        File.WriteAllText(ConfigPath, """{ "version": 6, "encoding": { "adaptive": null } }""");

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(9, loaded.Version);
        Assert.True(loaded.Encoding.Adaptive.Enabled);
        Assert.True(loaded.Encoding.Adaptive.AutoRecover);
        Assert.Equal(3, loaded.Encoding.Adaptive.MaxLevel);
    }

    [Fact]
    public void Out_of_range_adaptive_max_level_is_clamped_on_load()
    {
        File.WriteAllText(ConfigPath, """{ "version": 7, "encoding": { "adaptive": { "maxLevel": 9 } } }""");
        Assert.Equal(3, new ConfigStore(ConfigPath).Load().Encoding.Adaptive.MaxLevel);

        File.WriteAllText(ConfigPath, """{ "version": 7, "encoding": { "adaptive": { "maxLevel": -1 } } }""");
        Assert.Equal(0, new ConfigStore(ConfigPath).Load().Encoding.Adaptive.MaxLevel);
    }

    [Fact]
    public void V5_file_gains_the_notifications_section_with_defaults()
    {
        // A pre-v6 file (or one hand-edited to an explicit null section) must load with the
        // notification defaults instead of NRE-ing out of the auto-start path.
        File.WriteAllText(ConfigPath, """{ "version": 5, "notifications": null }""");

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(9, loaded.Version);
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

        Assert.Equal(9, config.Version); // defaults are schema v9
        Assert.True(File.Exists(ConfigPath + ".bak"));
        // A corrupted (but pre-existing) config must NOT be treated as a fresh install: the room
        // label stays empty rather than being silently stamped with this machine's hostname, and
        // no timetable is conjured (CreateDefault is already v8, so the seed block is skipped) —
        // otherwise a parse failure could quietly start cutting/uploading VODs.
        Assert.Equal(string.Empty, config.DeviceName);
        Assert.False(config.Periods.HasAnyWeekdayPeriods());
    }

    [Fact]
    public void Pre_v8_install_is_marked_complete_so_an_update_never_reopens_room_setup()
    {
        File.WriteAllText(ConfigPath, """{ "version": 8, "remote": { "mode": "cloudflare" } }""");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.Equal(9, config.Version);
        Assert.True(config.Provisioning.Completed);
        Assert.Equal(string.Empty, config.Provisioning.RoomId);
    }

    [Fact]
    public void Fresh_install_remains_eligible_for_room_provisioning()
    {
        var config = new ConfigStore(ConfigPath).Load();

        Assert.False(config.Provisioning.Completed);
        Assert.Equal(string.Empty, config.Provisioning.InstallationId);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
