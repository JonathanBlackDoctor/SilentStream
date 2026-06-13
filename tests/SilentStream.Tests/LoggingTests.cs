using SilentStream.Core.Implementations;
using SilentStream.Core.Logging;
using Xunit;

namespace SilentStream.Tests;

public class LoggingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sstream-log-").FullName;

    [Fact]
    public void Log_lines_reach_the_daily_file_and_the_in_memory_sink()
    {
        LogConfigurator.Configure(_dir);
        InMemoryLogSink.Clear();
        var log = new LogService();

        log.Info("스트림 시작 테스트");
        log.Error("오류 테스트", new InvalidOperationException("boom"));
        LogConfigurator.Flush();

        var file = Path.Combine(_dir, $"SilentStream_{DateTime.Now:yyyy-MM-dd}.log");
        Assert.True(File.Exists(file));
        var content = File.ReadAllText(file);
        Assert.Contains("스트림 시작 테스트", content);
        Assert.Contains("boom", content);

        var lines = InMemoryLogSink.Snapshot();
        Assert.Contains(lines, l => l.Contains("스트림 시작 테스트"));
        Assert.Contains(lines, l => l.Contains("InvalidOperationException"));
    }

    [Fact]
    public void In_memory_sink_keeps_at_most_1000_lines()
    {
        InMemoryLogSink.Clear();
        for (var i = 0; i < 1200; i++)
        {
            InMemoryLogSink.Append($"line {i}");
        }

        var lines = InMemoryLogSink.Snapshot();
        Assert.True(lines.Count <= InMemoryLogSink.Capacity);
        Assert.Equal("line 1199", lines[^1]);
    }

    public void Dispose()
    {
        NLog.LogManager.Configuration = null;
        Directory.Delete(_dir, recursive: true);
    }
}
