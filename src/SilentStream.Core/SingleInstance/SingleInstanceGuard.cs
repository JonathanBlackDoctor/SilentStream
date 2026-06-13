namespace SilentStream.Core.SingleInstance;

/// <summary>
/// Named-mutex single-instance guard (plan §3.1). The first process owns the mutex;
/// later launches see <see cref="IsPrimaryInstance"/> == false, optionally signal the
/// primary instance (Windows: named event), and exit.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string DefaultMutexName = "SilentStream_SingleInstance";
    private const string ShowUiEventName = "SilentStream_ShowUi";

    private readonly Mutex _mutex;
    private bool _disposed;

    public bool IsPrimaryInstance { get; }

    public SingleInstanceGuard(string mutexName = DefaultMutexName)
    {
        _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    /// <summary>
    /// Called by a secondary instance: pings the primary so it can surface the control UI.
    /// Named events are Windows-only; a no-op elsewhere.
    /// </summary>
    public static void SignalPrimaryInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (EventWaitHandle.TryOpenExisting(ShowUiEventName, out var handle))
        {
            using (handle)
            {
                handle.Set();
            }
        }
    }

    /// <summary>
    /// Called by the primary instance: registers the named event and invokes
    /// <paramref name="onSignal"/> whenever a secondary instance pings. Windows-only.
    /// </summary>
    public IDisposable? ListenForSignals(Action onSignal)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, ShowUiEventName);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            handle, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
        return new SignalListener(handle, registration);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (IsPrimaryInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Disposed from a different thread than the one that acquired it.
            }
        }
        _mutex.Dispose();
    }

    private sealed class SignalListener(EventWaitHandle handle, RegisteredWaitHandle registration)
        : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(handle);
            handle.Dispose();
        }
    }
}
