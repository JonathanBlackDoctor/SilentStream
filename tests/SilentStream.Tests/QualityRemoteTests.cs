using SilentStream.Core.Contracts;
using SilentStream.Core.Models;
using SilentStream.Core.Remote;
using Xunit;

namespace SilentStream.Tests;

/// <summary>Wire-contract tests for GET/PUT /api/quality (확장계획서_적응형송출품질 §7.2/§10 A4).</summary>
public class QualityRemoteTests
{
    private readonly FakeQuality _controller = new();

    private static readonly QualityStep Step1 = new(1, "절약 1단계", 1920, 1080, 60, 5400);

    [Fact]
    public void Dto_carries_desired_and_applied_truths_separately()
    {
        _controller.LadderValue = AdaptiveLadder();
        _controller.StatusValue = QualityStatus.Original with
        {
            Mode = QualityMode.ManualHold, Level = 1, LevelName = "절약 1단계"
        };
        var applied = QualityStatus.Original with { Applied = Step1 };

        var dto = QualityRemote.BuildDto(_controller, applied, adaptiveEnabled: true);

        Assert.Equal("manual", dto.Mode);
        Assert.Equal(1, dto.Level);
        Assert.Equal("절약 1단계", dto.LevelName);
        Assert.Equal(4, dto.Ladder.Count);
        Assert.Equal(9000, dto.Base!.VideoBitrateKbps);   // 원본 rung
        Assert.Equal(5400, dto.Current!.VideoBitrateKbps); // applied truth
        Assert.True(dto.AdaptiveEnabled);
    }

    [Fact]
    public void Dto_before_any_session_has_no_ladder_or_current()
    {
        var dto = QualityRemote.BuildDto(_controller, QualityStatus.Original, adaptiveEnabled: true);

        Assert.Equal("auto", dto.Mode);
        Assert.Empty(dto.Ladder);
        Assert.Null(dto.Base);
        Assert.Null(dto.Current);
    }

    [Fact]
    public void Put_auto_clears_the_manual_hold()
    {
        var result = QualityRemote.Apply(new QualityRemote.PutRequest("auto", null),
            _controller, QualityStatus.Original, adaptiveEnabled: true, live: true);

        Assert.True(result.Ok);
        Assert.True(_controller.SetAutoCalled);
        Assert.Equal("swapped", result.Applied);
    }

    [Fact]
    public void Put_manual_pins_the_level_and_reports_deferred_when_not_live()
    {
        var result = QualityRemote.Apply(new QualityRemote.PutRequest("manual", 2),
            _controller, QualityStatus.Original, adaptiveEnabled: true, live: false);

        Assert.True(result.Ok);
        Assert.Equal(2, _controller.ManualLevel);
        Assert.Equal("deferred", result.Applied); // picked up by the next encoder start
    }

    [Theory]
    [InlineData(null, null)]     // no body
    [InlineData("manual", null)] // manual without a level
    [InlineData("manual", -1)]   // negative level
    [InlineData("faster", 1)]    // unknown mode
    public void Put_rejects_malformed_requests(string? mode, int? level)
    {
        var request = mode is null && level is null ? null : new QualityRemote.PutRequest(mode, level);

        var result = QualityRemote.Apply(request,
            _controller, QualityStatus.Original, adaptiveEnabled: true, live: true);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Equal("none", result.Applied);
        Assert.False(_controller.SetAutoCalled);
        Assert.Null(_controller.ManualLevel);
    }

    private static IReadOnlyList<QualityStep> AdaptiveLadder() =>
    [
        new(0, "원본", 1920, 1080, 60, 9000),
        Step1,
        new(2, "절약 2단계", 1920, 1080, 30, 3600),
        new(3, "안전 모드", 1280, 720, 30, 2500)
    ];

    private sealed class FakeQuality : IAdaptiveQualityController
    {
        public QualityStatus StatusValue = QualityStatus.Original;
        public IReadOnlyList<QualityStep> LadderValue = [];
        public bool SetAutoCalled;
        public int? ManualLevel;

        public QualityMode Mode => StatusValue.Mode;
        public int Level => StatusValue.Level;
        public IReadOnlyList<QualityStep> Ladder => LadderValue;
        public QualityStatus Status => StatusValue;

#pragma warning disable CS0067
        public event EventHandler<QualityStatus>? ChangeRequested;
#pragma warning restore CS0067

        public EncoderStartOptions Apply(EncoderStartOptions baseOptions) => baseOptions;

        public void OnMetrics(MetricsSnapshot metrics) { }

        public void OnStateChanged(StreamState state) { }

        public void SetManual(int level) => ManualLevel = level;

        public void SetAuto() => SetAutoCalled = true;
    }
}
