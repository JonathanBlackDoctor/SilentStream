using NLog;
using SilentStream.Core.Contracts;
using SilentStream.Core.Logging;

namespace SilentStream.Core.Implementations;

/// <summary>
/// NLog-backed logging facade. File target + 180-day archive are configured via
/// <see cref="LogConfigurator"/>; every line is also mirrored into
/// <see cref="InMemoryLogSink"/> for the control-UI log viewer.
/// </summary>
public sealed class LogService : ILogService
{
    private static readonly Logger Log = LogManager.GetLogger("SilentStream");

    public void Debug(string message)
    {
        Log.Debug(message);
        Mirror("DEBUG", message);
    }

    public void Info(string message)
    {
        Log.Info(message);
        Mirror("INFO", message);
    }

    public void Warn(string message)
    {
        Log.Warn(message);
        Mirror("WARN", message);
    }

    public void Error(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            Log.Error(message);
            Mirror("ERROR", message);
        }
        else
        {
            Log.Error(exception, message);
            Mirror("ERROR", $"{message} | {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Mirror(string level, string message) =>
        InMemoryLogSink.Append($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
}
