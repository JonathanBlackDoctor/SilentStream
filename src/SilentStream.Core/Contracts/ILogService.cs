namespace SilentStream.Core.Contracts;

/// <summary>
/// Thin logging facade over NLog (file target with 180-day archive). See plan §3.9.
/// </summary>
public interface ILogService
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
