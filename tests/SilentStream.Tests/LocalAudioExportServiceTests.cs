using System.Text.RegularExpressions;
using SilentStream.Core.Contracts;
using SilentStream.Core.Implementations;
using SilentStream.Core.Media;
using Xunit;

namespace SilentStream.Tests;

public sealed class LocalAudioExportServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-audio-export-").FullName;
    private readonly FakeRunner _runner = new();

    [Fact]
    public async Task Reencodes_local_period_audio_as_compact_mono_aac()
    {
        var source = Path.Combine(_dir, "period.mp4");
        File.WriteAllBytes(source, new byte[128]);
        var service = new LocalAudioExportService(_runner, new LogService(), Path.Combine(_dir, "audio"));

        var result = await service.ExportAsync(source, "asset-1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("asset-1.m4a", Path.GetFileName(result));
        Assert.True(File.Exists(result));
        Assert.Contains("-map 0:a:0", _runner.LastArgs);
        Assert.Contains("-c:a aac", _runner.LastArgs);
        Assert.Contains("-b:a 48k", _runner.LastArgs);
        Assert.Contains("-ac 1", _runner.LastArgs);
        Assert.DoesNotContain("-c:a copy", _runner.LastArgs);
        Assert.Contains(source, _runner.LastArgs);
    }

    [Fact]
    public async Task Removes_partial_audio_when_ffmpeg_fails()
    {
        var source = Path.Combine(_dir, "period.mp4");
        File.WriteAllBytes(source, new byte[128]);
        _runner.Fail = true;
        var outputDir = Path.Combine(_dir, "audio");
        var service = new LocalAudioExportService(_runner, new LogService(), outputDir);

        var result = await service.ExportAsync(source, "asset-2", CancellationToken.None);

        Assert.Null(result);
        Assert.False(File.Exists(Path.Combine(outputDir, "asset-2.m4a")));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeRunner : IProcessRunner
    {
        public string LastArgs { get; private set; } = string.Empty;
        public bool Fail { get; set; }

        public Task<(int ExitCode, string Output)> RunAsync(
            string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
        {
            LastArgs = arguments;
            var quoted = Regex.Matches(arguments, "\"([^\"]+)\"");
            var outputPath = quoted[^1].Groups[1].Value;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, new byte[64]);
            return Task.FromResult(Fail ? (1, "failed") : (0, "ok"));
        }
    }
}
