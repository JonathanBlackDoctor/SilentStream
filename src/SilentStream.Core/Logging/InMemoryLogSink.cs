using System.Collections.Concurrent;

namespace SilentStream.Core.Logging;

/// <summary>
/// Ring buffer of the most recent log lines for the control-UI log viewer
/// (최근 1000줄, plan §3.8). Fed by <see cref="Implementations.LogService"/>.
/// </summary>
public static class InMemoryLogSink
{
    public const int Capacity = 1000;

    private static readonly ConcurrentQueue<string> Lines = new();

    /// <summary>Raised on every appended line (UI subscribes for live tailing).</summary>
    public static event Action<string>? LineAdded;

    public static void Append(string line)
    {
        Lines.Enqueue(line);
        while (Lines.Count > Capacity && Lines.TryDequeue(out _))
        {
        }
        LineAdded?.Invoke(line);
    }

    public static IReadOnlyList<string> Snapshot() => Lines.ToArray();

    public static void Clear()
    {
        while (Lines.TryDequeue(out _))
        {
        }
    }
}
