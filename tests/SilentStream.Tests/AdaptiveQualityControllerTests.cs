using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

/// <summary>
/// Policy tests for the adaptive-quality controller (확장계획서_적응형송출품질 §5/§10 A3).
/// Time is a mutable virtual LOCAL clock (HealthMonitor seam pattern): tests advance it and feed
/// synthetic 2s metrics ticks, so every dwell/cooldown/lockout runs without real time.
/// </summary>
public class AdaptiveQualityControllerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-aq-").FullName;
    private readonly ConfigStore _configStore;
    private readonly FakeSchedule _schedule = new();
    private readonly Clock _clock = new();
    private readonly List<QualityStatus> _requests = [];
    private readonly AdaptiveQualityController _controller;

    // Resolved base: 1080p60 @ 9000kbps → ladder 원본/절약1(5400)/절약2(30fps 3600)/안전(720p30 2500).
    private static readonly EncoderStartOptions Base =
        new("rtmp://x/key", "/rec/f.mp4", 9000, 1920, 1080, 60);

    public AdaptiveQualityControllerTests()
    {
        _configStore = new ConfigStore(Path.Combine(_dir, "config.json"));
        _configStore.Save(AppConfig.CreateDefault());
        _controller = new AdaptiveQualityController(
            _configStore, _schedule, new LogService(), () => _clock.Now);
        _controller.ChangeRequested += (_, s) => _requests.Add(s);
    }

    private void GoLive()
    {
        _controller.OnStateChanged(StreamState.Live);
        _controller.Apply(Base);
        _clock.Advance(TimeSpan.FromSeconds(61)); // past the settle window
    }

    /// <summary>One 2s metrics tick. <paramref name="drops"/> is the CUMULATIVE tee drop count.</summary>
    private void Tick(double fps, int drops = 0)
    {
        _clock.Advance(TimeSpan.FromSeconds(2));
        _controller.OnMetrics(new MetricsSnapshot(0, fps, 0, -1, _clock.Now, TeeDropCount: drops));
    }

    // ---- ladder construction ----

    [Fact]
    public void Ladder_derives_from_the_base_parameters()
    {
        var ladder = AdaptiveQualityController.BuildLadder(1920, 1080, 60, 9000);

        Assert.Equal(4, ladder.Count);
        Assert.Equal((1920, 1080, 60d, 9000), (ladder[0].Width, ladder[0].Height, ladder[0].Fps, ladder[0].VideoBitrateKbps));
        Assert.Equal((1920, 1080, 60d, 5400), (ladder[1].Width, ladder[1].Height, ladder[1].Fps, ladder[1].VideoBitrateKbps));
        Assert.Equal((1920, 1080, 30d, 3600), (ladder[2].Width, ladder[2].Height, ladder[2].Fps, ladder[2].VideoBitrateKbps));
        Assert.Equal((1280, 720, 30d, 2500), (ladder[3].Width, ladder[3].Height, ladder[3].Fps, ladder[3].VideoBitrateKbps));
        Assert.Equal("안전 모드", ladder[3].Name);
    }

    [Fact]
    public void Degenerate_rungs_are_skipped_for_an_already_small_base()
    {
        // 720p30 @ 2500: the 안전 모드 rung would equal 절약 2단계 — it must be dropped.
        var ladder = AdaptiveQualityController.BuildLadder(1280, 720, 30, 2500);

        Assert.Equal(3, ladder.Count);
        Assert.Equal(1500, ladder[1].VideoBitrateKbps);
        Assert.Equal(1000, ladder[2].VideoBitrateKbps);
    }

    [Fact]
    public void Safe_mode_fit_preserves_aspect_and_never_upscales()
    {
        Assert.Equal((1280, 540), AdaptiveQualityController.FitInto(2560, 1080, 1280, 720)); // 21:9
        Assert.Equal((1280, 720), AdaptiveQualityController.FitInto(1920, 1080, 1280, 720));
        Assert.Equal((960, 720), AdaptiveQualityController.FitInto(1024, 768, 1280, 720));   // 4:3 — height governs
        Assert.Equal((1024, 576), AdaptiveQualityController.FitInto(1024, 576, 1280, 720));  // already fits — no upscale
    }

    // ---- step-down triggers ----

    [Fact]
    public void Sustained_fps_deficit_steps_down_with_encode_overload()
    {
        GoLive();

        for (var i = 0; i < 10; i++)
        {
            Tick(fps: 30); // target 60 → far below the 0.85 deficit line
        }

        var request = Assert.Single(_requests);
        Assert.Equal(1, request.Level);
        Assert.Equal(QualityChangeReason.EncodeOverload, request.Reason);
        Assert.Equal("절약 1단계", request.LevelName);
        Assert.Equal(1, _controller.Level);
    }

    [Fact]
    public void Spread_fifo_drops_step_down_with_network_congestion_even_at_healthy_fps()
    {
        GoLive();

        // Drops on ticks 1/6/11 → 3 drop ticks spread over 20s; fps stays perfectly healthy.
        for (var i = 0; i < 11; i++)
        {
            var drops = i >= 10 ? 3 : i >= 5 ? 2 : 1;
            Tick(fps: 60, drops: drops);
        }

        var request = Assert.Single(_requests);
        Assert.Equal(QualityChangeReason.NetworkCongestion, request.Reason);
        Assert.Equal(1, request.Level);
    }

    [Fact]
    public void A_single_drop_burst_does_not_trigger()
    {
        GoLive();

        Tick(fps: 60, drops: 50); // one bad moment: a big burst on a single tick
        for (var i = 0; i < 14; i++)
        {
            Tick(fps: 60, drops: 50);
        }

        Assert.Empty(_requests);
    }

    [Fact]
    public void Settle_window_after_encoder_start_suppresses_triggers()
    {
        _controller.OnStateChanged(StreamState.Live);
        _controller.Apply(Base);

        for (var i = 0; i < 12; i++)
        {
            Tick(fps: 30); // 24s of deficit — still inside the 60s settle window
        }
        Assert.Empty(_requests);

        for (var i = 0; i < 19; i++)
        {
            Tick(fps: 30); // now past the settle window with a full deficit window
        }
        Assert.Single(_requests);
    }

    [Fact]
    public void Consecutive_downgrades_respect_the_cooldown()
    {
        GoLive();
        for (var i = 0; i < 10; i++)
        {
            Tick(fps: 20);
        }
        Assert.Single(_requests); // → 절약 1단계

        for (var i = 0; i < 20; i++)
        {
            Tick(fps: 20); // only 40s since the change — cooldown (90s) holds
        }
        Assert.Single(_requests);

        for (var i = 0; i < 26; i++)
        {
            Tick(fps: 20); // past the 90s cooldown with a fresh deficit window
        }
        Assert.Equal(2, _requests.Count);
        Assert.Equal(2, _requests[^1].Level);
    }

    // ---- recovery ----

    [Fact]
    public void Recovery_steps_back_up_after_a_healthy_dwell()
    {
        GoLive();
        for (var i = 0; i < 10; i++)
        {
            Tick(fps: 30);
        }
        Assert.Equal(1, _controller.Level);

        for (var i = 0; i < 150; i++)
        {
            Tick(fps: 60); // 5 minutes of healthy ticks
        }

        Assert.Equal(2, _requests.Count);
        Assert.Equal(0, _requests[^1].Level);
        Assert.Equal(QualityChangeReason.AutoRecover, _requests[^1].Reason);
    }

    [Fact]
    public void Recovery_waits_for_the_class_period_to_end()
    {
        // A period runs 09:00→12:00; the clock starts at 10:00, so recovery must hold.
        _schedule.Day = new DaySchedule([new SchoolPeriod(1, new TimeOnly(9, 0), new TimeOnly(12, 0))]);
        GoLive();
        for (var i = 0; i < 10; i++)
        {
            Tick(fps: 30);
        }
        Assert.Single(_requests);

        for (var i = 0; i < 160; i++)
        {
            Tick(fps: 60); // dwell satisfied, but 수업 중 — the up-swap would glitch a working broadcast
        }
        Assert.Single(_requests);

        _clock.Advance(TimeSpan.FromHours(2)); // past 12:00 — 쉬는 시간
        Tick(fps: 60);
        Assert.Equal(2, _requests.Count);
        Assert.Equal(QualityChangeReason.AutoRecover, _requests[^1].Reason);
    }

    [Fact]
    public void A_flapping_level_gets_locked_out_after_recovery_fails()
    {
        GoLive();
        for (var i = 0; i < 10; i++)
        {
            Tick(fps: 30); // → 절약 1단계
        }
        _clock.Advance(TimeSpan.FromSeconds(301));
        Tick(fps: 60); // dwell + healthy → 원본 복귀 (up)
        Assert.Equal(0, _controller.Level);

        for (var i = 0; i < 46; i++)
        {
            Tick(fps: 30); // 원본 can't hold: down again ~92s after the up (< 5min flap window)
        }
        Assert.Equal(1, _controller.Level);
        Assert.Equal(3, _requests.Count);

        _clock.Advance(TimeSpan.FromSeconds(301));
        Tick(fps: 60); // healthy again — but 원본 is locked out for 30 minutes
        Assert.Equal(1, _controller.Level);

        _clock.Advance(TimeSpan.FromMinutes(31));
        Tick(fps: 60);
        Assert.Equal(0, _controller.Level); // lockout expired → recovery allowed
    }

    [Fact]
    public void Hourly_change_cap_suspends_automatic_adjustment()
    {
        GoLive();
        for (var cycle = 0; cycle < 4; cycle++) // 4 × (down + up) = 8 automatic changes (~42 min)
        {
            _clock.Advance(TimeSpan.FromSeconds(301)); // past the cooldown AND the flap window
            for (var i = 0; i < 10; i++)
            {
                Tick(fps: 30);
            }
            _clock.Advance(TimeSpan.FromSeconds(301)); // recovery dwell
            Tick(fps: 60);
        }
        Assert.Equal(8, _requests.Count);

        _clock.Advance(TimeSpan.FromSeconds(301)); // still inside the rolling hour
        for (var i = 0; i < 10; i++)
        {
            Tick(fps: 30); // a 9th change is due — the safety cap must hold it
        }
        Assert.Equal(8, _requests.Count);
        Assert.Equal(0, _controller.Level);
    }

    // ---- manual / config gates ----

    [Fact]
    public void Manual_pin_before_any_session_uses_nominal_levels()
    {
        _controller.SetManual(2); // no ladder yet (never applied)

        var request = Assert.Single(_requests);
        Assert.Equal(QualityMode.ManualHold, request.Mode);
        Assert.Equal(2, request.Level);
        Assert.Equal("절약 2단계", request.LevelName);
        Assert.Null(request.Applied);
    }

    [Fact]
    public void Manual_hold_suspends_automatic_changes_until_set_auto()
    {
        GoLive();
        _controller.SetManual(1);
        Assert.Single(_requests);

        for (var i = 0; i < 15; i++)
        {
            Tick(fps: 20); // heavy overload — but the operator pinned the level
        }
        Assert.Single(_requests);

        _controller.SetAuto();
        Assert.Equal(2, _requests.Count);
        for (var i = 0; i < 46; i++)
        {
            Tick(fps: 20); // cooldown from SetAuto passes → automatic control resumes
        }
        Assert.Equal(3, _requests.Count);
        Assert.Equal(2, _requests[^1].Level);
    }

    [Fact]
    public void Disabled_adaptive_config_blocks_auto_but_not_manual()
    {
        _configStore.Update(c => c.Encoding.Adaptive.Enabled = false);
        GoLive();

        for (var i = 0; i < 15; i++)
        {
            Tick(fps: 20);
        }
        Assert.Empty(_requests);

        _controller.SetManual(1); // 수동 원격 조정은 enabled와 무관하게 항상 가능 (§7.1)
        Assert.Single(_requests);
    }

    [Fact]
    public void Max_level_caps_automatic_degradation_depth()
    {
        _configStore.Update(c => c.Encoding.Adaptive.MaxLevel = 1);
        GoLive();

        for (var i = 0; i < 10; i++)
        {
            Tick(fps: 20);
        }
        Assert.Equal(1, _controller.Level);

        for (var i = 0; i < 60; i++)
        {
            Tick(fps: 20); // far past the cooldown — but level 2 is beyond MaxLevel
        }
        Assert.Single(_requests);
    }

    [Fact]
    public void Idle_resets_to_original_auto_session_scoped_hold()
    {
        _controller.SetManual(2);

        _controller.OnStateChanged(StreamState.Idle);

        Assert.Equal(2, _requests.Count);
        var reset = _requests[^1];
        Assert.Equal(0, reset.Level);
        Assert.Equal(QualityMode.Auto, reset.Mode);
        Assert.Equal(QualityChangeReason.SessionReset, reset.Reason);
    }

    [Fact]
    public void Apply_clamps_the_desired_level_to_the_real_ladder()
    {
        _controller.SetManual(3); // nominal 안전 모드, pinned before the session

        // 720p30 @ 2500 builds a 3-rung ladder — level 3 must clamp to its deepest rung (2).
        var options = _controller.Apply(new EncoderStartOptions("rtmp://x/k", "/r/f.mp4", 2500, 1280, 720, 30));

        Assert.Equal(2, _controller.Level);
        Assert.Equal(1000, options.VideoBitrateKbps);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- fakes ----

    private sealed class Clock
    {
        public DateTime Now = new(2026, 7, 13, 10, 0, 0); // local wall clock (Monday 10:00)

        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class FakeSchedule : IPeriodScheduleStore
    {
        public DaySchedule Day = DaySchedule.Empty;

        public DaySchedule GetWeekdayDefault(DayOfWeek day) => Day;

        public void SetWeekdayDefault(DayOfWeek day, DaySchedule schedule) { }

        public DaySchedule? GetOverride(DateOnly date) => null;

        public void SetOverride(DateOnly date, DaySchedule schedule) { }

        public void ClearOverride(DateOnly date) { }

        public DaySchedule ResolveForDate(DateOnly date) => Day;
    }
}
