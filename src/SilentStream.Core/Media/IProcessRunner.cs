namespace SilentStream.Core.Media;

/// <summary>
/// Minimal process-execution seam so GPU detection logic is unit-testable
/// without a real ffmpeg binary.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs an executable to completion and returns (exitCode, stdout+stderr combined).
    /// </summary>
    Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct);
}
