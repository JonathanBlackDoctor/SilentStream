using NLog;
using NLog.Config;
using NLog.Targets;

namespace SilentStream.Core.Logging;

/// <summary>
/// Programmatic NLog setup: daily file %AppData%\SilentStream\logs\SilentStream_yyyy-MM-dd.log
/// with a 180-day archive/cleanup policy (plan §3.9).
/// </summary>
public static class LogConfigurator
{
    public const int ArchiveDays = 180;

    public static void Configure(string? logDirectory = null)
    {
        var dir = logDirectory ?? AppPaths.LogsDir;
        Directory.CreateDirectory(dir);

        var file = new FileTarget("file")
        {
            FileName = Path.Combine(dir, "SilentStream_${shortdate}.log"),
            Layout = "${longdate} [${level:uppercase=true}] ${message}${onexception:inner= | ${exception:format=tostring}}",
            ArchiveEvery = FileArchivePeriod.Day,
            MaxArchiveDays = ArchiveDays,
            KeepFileOpen = false,
            ConcurrentWrites = true
        };

        var config = new LoggingConfiguration();
        config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, file);
        LogManager.Configuration = config;
    }

    public static void Flush() => LogManager.Flush();
}
