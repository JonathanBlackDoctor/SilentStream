using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

public class RemoteSettingsLockTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-settings-lock-").FullName;

    [Fact]
    public void Schedule_only_mode_allows_only_timetable_edits()
    {
        Assert.True(RemoteSettingsLock.Allows(RemoteSettingsLock.ScheduleOnly, RemoteSettingsSection.Schedule));
        Assert.False(RemoteSettingsLock.Allows(RemoteSettingsLock.ScheduleOnly, RemoteSettingsSection.Other));
    }

    [Fact]
    public void Locked_mode_blocks_every_settings_section()
    {
        Assert.False(RemoteSettingsLock.Allows(RemoteSettingsLock.Locked, RemoteSettingsSection.Schedule));
        Assert.False(RemoteSettingsLock.Allows(RemoteSettingsLock.Locked, RemoteSettingsSection.Other));
    }

    [Theory]
    [InlineData("unlocked", RemoteSettingsLock.Unlocked)]
    [InlineData("SCHEDULEONLY", RemoteSettingsLock.ScheduleOnly)]
    [InlineData("locked", RemoteSettingsLock.Locked)]
    public void Submitted_modes_are_normalized_case_insensitively(string submitted, string expected)
    {
        Assert.True(RemoteSettingsLock.TryParse(submitted, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Unknown_persisted_mode_fails_closed()
    {
        Assert.Equal(RemoteSettingsLock.Locked, RemoteSettingsLock.Normalize("everything"));
        Assert.False(RemoteSettingsLock.Allows("everything", RemoteSettingsSection.Schedule));
    }

    [Fact]
    public void V9_config_gains_an_unlocked_settings_lock_mode()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{ "version": 9, "remote": { "mode": "cloudflare" } }""");

        var config = new ConfigStore(path).Load();

        Assert.Equal(12, config.Version);
        Assert.Equal(RemoteSettingsLock.Unlocked, config.Remote.SettingsLockMode);
    }

    [Fact]
    public void V11_config_keeps_automatic_operation_enabled()
    {
        var path = Path.Combine(_dir, "automatic-operation.json");
        File.WriteAllText(path, """{ "version": 11 }""");

        var config = new ConfigStore(path).Load();

        Assert.Equal(12, config.Version);
        Assert.True(config.AutomaticOperationEnabled);
    }

    [Fact]
    public void Automatic_operation_preference_survives_a_reload()
    {
        var path = Path.Combine(_dir, "automatic-operation-preference.json");
        var store = new ConfigStore(path);
        var config = AppConfig.CreateDefault();
        config.AutomaticOperationEnabled = false;

        store.Save(config);

        Assert.False(store.Load().AutomaticOperationEnabled);
    }

    [Fact]
    public void Default_config_is_open_until_the_operator_enables_a_lock()
    {
        Assert.Equal(RemoteSettingsLock.Unlocked, AppConfig.CreateDefault().Remote.SettingsLockMode);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
