using NLog;
using SilentStream.Core.Contracts;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Thin NLog-backed logging facade. The file target + 180-day archive policy are
/// configured in Phase 1; until then this forwards to whatever NLog config is loaded.
/// </summary>
public sealed class LogService : ILogService
{
    private static readonly Logger Log = LogManager.GetLogger("SilentStream");

    public void Debug(string message) => Log.Debug(message);

    public void Info(string message) => Log.Info(message);

    public void Warn(string message) => Log.Warn(message);

    public void Error(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            Log.Error(message);
        }
        else
        {
            Log.Error(exception, message);
        }
    }
}
