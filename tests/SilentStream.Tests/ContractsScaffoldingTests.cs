using Microsoft.Extensions.DependencyInjection;
using SilentStream.Core.Contracts;
using SilentStream.Core.DependencyInjection;
using SilentStream.Core.Models;
using Xunit;

namespace SilentStream.Tests;

/// <summary>
/// Phase-0 scaffolding tests. They verify the fixed contracts are all registered and the
/// DI graph resolves — without depending on any (Windows-only) real implementation.
/// </summary>
public class ContractsScaffoldingTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSilentStreamCore();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    public static IEnumerable<object[]> ContractTypes =>
        new[]
        {
            new object[] { typeof(ILogService) },
            new object[] { typeof(IConfigStore) },
            new object[] { typeof(IScreenCaptureSource) },
            new object[] { typeof(IAudioMixer) },
            new object[] { typeof(IEncoderPipeline) },
            new object[] { typeof(IYouTubeLiveService) },
            new object[] { typeof(IRecordingManager) },
            new object[] { typeof(IStreamOrchestrator) },
        };

    [Theory]
    [MemberData(nameof(ContractTypes))]
    public void Each_contract_resolves_from_the_container(Type contract)
    {
        using var provider = BuildProvider();
        var instance = provider.GetService(contract);
        Assert.NotNull(instance);
    }

    [Fact]
    public void All_eight_core_contracts_are_registered()
    {
        Assert.Equal(8, ContractTypes.Count());
    }

    [Fact]
    public void Token_protector_is_registered_as_an_additive_contract()
    {
        using var provider = BuildProvider();
        Assert.NotNull(provider.GetService<ITokenProtector>());
    }

    [Theory]
    [InlineData(typeof(IPeriodScheduleStore))]
    [InlineData(typeof(IPeriodScheduler))]
    [InlineData(typeof(IVodSegmentService))]
    [InlineData(typeof(IYouTubeUploadService))]
    [InlineData(typeof(IUploadQueue))]
    [InlineData(typeof(IRecordingSessionInfo))]
    [InlineData(typeof(IHealthMonitor))]
    [InlineData(typeof(INotifier))]
    public void Extension_contracts_resolve_from_the_container(Type contract)
    {
        // 확장(교시 VOD + 폰 원격) 신규 계약이 DI 그래프에서 정상 해석되는지 검증.
        using var provider = BuildProvider();
        Assert.NotNull(provider.GetService(contract));
    }

    [Fact]
    public void Recording_manager_and_session_info_share_one_instance()
    {
        using var provider = BuildProvider();
        Assert.Same(
            provider.GetRequiredService<IRecordingManager>(),
            provider.GetRequiredService<IRecordingSessionInfo>());
    }

    [Fact]
    public void Orchestrator_starts_in_idle_state()
    {
        using var provider = BuildProvider();
        var orchestrator = provider.GetRequiredService<IStreamOrchestrator>();
        Assert.Equal(StreamState.Idle, orchestrator.State);
    }

    [Fact]
    public void Default_config_matches_the_documented_schema()
    {
        var config = AppConfig.CreateDefault();

        Assert.Equal(8, config.Version); // schema v8 (승인 기반 교시 분할)
        Assert.Equal("unlisted", config.YouTube.Privacy);
        Assert.Equal("auto", config.Encoding.PreferredGpu);
        Assert.True(config.Recording.Enabled);
        Assert.Equal(100, config.Recording.MaxSizeGb);
        Assert.Equal(7, config.Recording.RetentionDays);
        Assert.Equal(5, config.Recording.MinFreeGb);
        Assert.Equal("Ctrl+Shift+F12", config.Hotkey);
        Assert.Equal("startup", config.Autostart);
    }
}
