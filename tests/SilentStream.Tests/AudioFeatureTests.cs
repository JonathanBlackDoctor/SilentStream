using SilentStream.Core.Implementations;
using SilentStream.Core.Media;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

/// <summary>
/// Covers the schema-v3 audio additions that live in Core: the FFmpeg master-filter chain,
/// the config↔runtime source mapper, and the v2→v3 config migration. The Windows-only mixer
/// (gain/mute/gate/metering) is verified by the build + manual runs (see CLAUDE.md).
/// </summary>
public class FfmpegAudioFilterTests
{
    private static EncoderSessionSpec Spec(AudioFilterSettings? filters) =>
        new(1920, 1080, 30, GpuEncoder.Nvenc, 6000, 160,
            "rtmp://a/key", @"C:\rec\out.mp4", @"\\.\pipe\silentstream_audio",
            AudioFilters: filters);

    [Fact]
    public void No_filters_emit_no_af_and_keep_plain_aac()
    {
        var none = FfmpegArgumentsBuilder.Build(Spec(null), 8);
        var off = FfmpegArgumentsBuilder.Build(Spec(AudioFilterSettings.None), 8);

        Assert.DoesNotContain("-af", none);
        Assert.DoesNotContain("-af", off);
        Assert.Contains("-c:a aac -b:a 160k", none);
    }

    [Fact]
    public void Enabled_filters_compose_the_af_chain_in_order()
    {
        var filters = new AudioFilterSettings(
            NoiseSuppressionEnabled: true, NoiseSuppressionDb: 18,
            CompressorEnabled: true, LimiterEnabled: true, MasterGainDb: 3);

        var args = FfmpegArgumentsBuilder.Build(Spec(filters), 8);

        Assert.Contains("-af \"", args);
        Assert.Contains("afftdn=nr=18", args);
        Assert.Contains("acompressor", args);
        Assert.Contains("alimiter=limit=0.95", args);
        Assert.Contains("volume=3dB", args);
        // denoise precedes compressor precedes limiter precedes make-up gain
        Assert.Matches(@"afftdn.*acompressor.*alimiter.*volume", args);
        // the filter chain is still followed by the AAC encode
        Assert.Contains("-c:a aac -b:a 160k", args);
    }

    [Fact]
    public void Limiter_only_emits_just_the_limiter()
    {
        var args = FfmpegArgumentsBuilder.Build(
            Spec(new AudioFilterSettings(LimiterEnabled: true)), 8);

        Assert.Contains("-af \"alimiter=limit=0.95\"", args);
        Assert.DoesNotContain("afftdn", args);
        Assert.DoesNotContain("acompressor", args);
    }

    [Fact]
    public void Noise_suppression_strength_is_clamped()
    {
        var args = FfmpegArgumentsBuilder.Build(
            Spec(new AudioFilterSettings(NoiseSuppressionEnabled: true, NoiseSuppressionDb: 500)), 8);

        Assert.Contains("afftdn=nr=97", args); // clamped to the afftdn max
    }
}

public class AudioConfigMapperTests
{
    [Theory]
    [InlineData("system", null, "system")]
    [InlineData("mic", null, "mic:default")]
    [InlineData("mic", "abc", "mic:abc")]
    public void SourceId_is_deterministic(string kind, string? deviceId, string expected)
    {
        Assert.Equal(expected, AudioConfigMapper.SourceId(kind, deviceId));
    }

    [Fact]
    public void Maps_sources_with_kinds_gains_and_dedup()
    {
        var audio = new AudioConfig
        {
            Sources =
            [
                new AudioSourceConfig { Kind = "system", Gain = 0.8 },
                new AudioSourceConfig { Kind = "mic", DeviceId = "m1", Gain = 2.0, Muted = true },
                new AudioSourceConfig { Kind = "mic", DeviceId = "m1", Gain = 1.0 } // duplicate id ignored
            ]
        };

        var settings = AudioConfigMapper.ToSourceSettings(audio);

        Assert.Equal(2, settings.Count);
        var system = Assert.Single(settings, s => s.Kind == AudioSourceKind.System);
        Assert.Equal(0.8, system.Gain);
        var mic = Assert.Single(settings, s => s.Kind == AudioSourceKind.Microphone);
        Assert.Equal("mic:m1", mic.Id);
        Assert.Equal(2.0, mic.Gain);
        Assert.True(mic.Muted);
    }

    [Fact]
    public void Synthesises_a_system_source_when_missing()
    {
        var audio = new AudioConfig
        {
            Sources = [new AudioSourceConfig { Kind = "mic", DeviceId = "m1" }]
        };

        var settings = AudioConfigMapper.ToSourceSettings(audio);

        Assert.Contains(settings, s => s.Kind == AudioSourceKind.System && s.Id == "system");
    }

    [Fact]
    public void Maps_filter_settings()
    {
        var filters = AudioConfigMapper.ToFilterSettings(new AudioFiltersConfig
        {
            NoiseSuppressionEnabled = true,
            NoiseSuppressionDb = 20,
            CompressorEnabled = true,
            LimiterEnabled = true,
            MasterGainDb = -2
        });

        Assert.True(filters.NoiseSuppressionEnabled);
        Assert.Equal(20, filters.NoiseSuppressionDb);
        Assert.True(filters.CompressorEnabled);
        Assert.True(filters.LimiterEnabled);
        Assert.Equal(-2, filters.MasterGainDb);
        Assert.True(filters.HasAny);
    }

    [Fact]
    public void SeedMicrophoneSources_adds_all_real_mics_and_excludes_loopback()
    {
        var audio = new AudioConfig(); // fresh: MicsSeeded=false, empty Sources
        var devices = new List<AudioDeviceInfo>
        {
            new("mA", "USB Microphone", false),
            new("mB", "Headset Microphone", false),
            new("sx", "스테레오 믹스", true) // loopback monitor — excluded
        };

        var changed = AudioConfigMapper.SeedMicrophoneSources(audio, devices);

        Assert.True(changed);
        Assert.True(audio.MicsSeeded);
        Assert.Equal(3, audio.Sources.Count); // system + 2 real mics, no loopback
        Assert.Single(audio.Sources, s => s.Kind == "system");
        Assert.Equal(new[] { "mA", "mB" },
            audio.Sources.Where(s => s.Kind == "mic").Select(s => s.DeviceId).ToArray());
        Assert.DoesNotContain(audio.Sources, s => s.DeviceId == "sx");
        // 2개 이상이면 유휴 마이크 히스 억제를 위해 게이트 ON
        Assert.All(audio.Sources.Where(s => s.Kind == "mic"), s => Assert.True(s.GateEnabled));
    }

    [Fact]
    public void SeedMicrophoneSources_single_mic_leaves_gate_off()
    {
        var audio = new AudioConfig();

        AudioConfigMapper.SeedMicrophoneSources(audio, [new AudioDeviceInfo("only", "Microphone", false)]);

        var mic = Assert.Single(audio.Sources, s => s.Kind == "mic");
        Assert.Equal("only", mic.DeviceId);
        Assert.False(mic.GateEnabled); // 마이크가 하나뿐이면 게이트로 음성이 잘릴 위험 회피
    }

    [Fact]
    public void SeedMicrophoneSources_falls_back_to_default_mic_when_no_real_mic()
    {
        var audio = new AudioConfig();

        // only a loopback device present → no real mic
        AudioConfigMapper.SeedMicrophoneSources(audio, [new AudioDeviceInfo("sx", "Stereo Mix", true)]);

        Assert.Equal(2, audio.Sources.Count); // system + single default-device mic
        var mic = Assert.Single(audio.Sources, s => s.Kind == "mic");
        Assert.Null(mic.DeviceId); // null = system default capture endpoint
        Assert.False(mic.GateEnabled);
    }

    [Fact]
    public void SeedMicrophoneSources_is_noop_once_seeded()
    {
        var audio = new AudioConfig
        {
            MicsSeeded = true,
            Sources = [new AudioSourceConfig { Kind = "system" }]
        };

        var changed = AudioConfigMapper.SeedMicrophoneSources(
            audio, [new AudioDeviceInfo("m", "Mic", false)]);

        Assert.False(changed);
        Assert.Single(audio.Sources); // user's current list left untouched
    }

    [Fact]
    public void SeedMicrophoneSources_preserves_existing_system_settings()
    {
        var audio = new AudioConfig
        {
            Sources = [new AudioSourceConfig { Kind = "system", Gain = 0.5, Muted = true }]
        };

        AudioConfigMapper.SeedMicrophoneSources(audio, [new AudioDeviceInfo("m", "Mic", false)]);

        var system = Assert.Single(audio.Sources, s => s.Kind == "system");
        Assert.Equal(0.5, system.Gain);
        Assert.True(system.Muted);
    }
}

public class AudioDeviceClassifierTests
{
    [Theory]
    [InlineData("스테레오 믹스 (Realtek(R) Audio)")]
    [InlineData("Stereo Mix (Realtek High Definition Audio)")]
    [InlineData("What U Hear (Sound Blaster)")]
    [InlineData("Wave Out Mix")]
    [InlineData("CABLE Output (VB-Audio Loopback)")]
    public void Loopback_monitors_are_flagged(string name)
    {
        Assert.True(AudioDeviceClassifier.IsLoopbackName(name));
    }

    [Theory]
    [InlineData("Microphone (USB Audio Device)")]
    [InlineData("마이크(Realtek(R) Audio)")]
    [InlineData("Headset Microphone")]
    [InlineData("")]
    [InlineData(null)]
    public void Real_microphones_are_not_flagged(string? name)
    {
        Assert.False(AudioDeviceClassifier.IsLoopbackName(name));
    }
}

public class AudioMigrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-audio-").FullName;

    private string ConfigPath => Path.Combine(_dir, "config.json");

    [Fact]
    public void V2_config_migrates_legacy_volumes_into_sources()
    {
        var store = new ConfigStore(ConfigPath);
        var v2 = AppConfig.CreateDefault();
        v2.Version = 2;
        v2.Audio.Sources.Clear();
        v2.Audio.SystemVolume = 0.7;
        v2.Audio.MicVolume = 0.3;
        v2.Audio.MicDeviceId = "dev-1";
        store.Save(v2);

        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(6, loaded.Version);
        Assert.True(loaded.Audio.MicsSeeded); // 기존 설정은 시드된 것으로 표시(전체 마이크 자동확장 방지)
        Assert.Equal(string.Empty, loaded.DeviceName); // 기존 파일 마이그레이션은 호실명을 비워 둔다(호스트명 미각인)
        Assert.Equal(2, loaded.Audio.Sources.Count);
        var system = Assert.Single(loaded.Audio.Sources, s => s.Kind == "system");
        Assert.Equal(0.7, system.Gain);
        var mic = Assert.Single(loaded.Audio.Sources, s => s.Kind == "mic");
        Assert.Equal(0.3, mic.Gain);
        Assert.Equal("dev-1", mic.DeviceId);
        Assert.NotNull(loaded.Audio.Filters);
        Assert.NotNull(loaded.Capture);
    }

    [Fact]
    public void Default_config_seeds_system_plus_default_mic_on_first_load()
    {
        var loaded = new ConfigStore(ConfigPath).Load();

        Assert.Equal(2, loaded.Audio.Sources.Count);
        Assert.Contains(loaded.Audio.Sources, s => s.Kind == "system");
        Assert.Contains(loaded.Audio.Sources, s => s.Kind == "mic");
        Assert.False(loaded.Audio.MicsSeeded); // 신규 설치는 미시드 → 앱이 첫 실행에 전체 마이크로 확장
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
