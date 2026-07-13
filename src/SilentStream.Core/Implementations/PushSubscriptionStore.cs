using System.Text.Json;
using SilentStream.Core.Contracts;
using SilentStream.Core.Remote.WebPush;

namespace SilentStream.Core.Implementations;

/// <summary>
/// Persists Web Push subscriptions as a JSON array in a dedicated file (원격 컨트롤러 개선 Phase 3),
/// kept out of config.json so enabling notifications never rewrites operator settings. Writes are
/// atomic (temp + move) and the set is deduplicated by endpoint. Thread-safe.
/// </summary>
public sealed class PushSubscriptionStore : IPushSubscriptionStore
{
    private readonly string _storeFile;
    private readonly object _gate = new();
    private List<StoredPushSubscription>? _cache;

    public PushSubscriptionStore(string storeFile) => _storeFile = storeFile;

    public IReadOnlyList<StoredPushSubscription> GetAll()
    {
        lock (_gate)
        {
            return Load().ToList(); // copy — safe to enumerate outside the lock
        }
    }

    public void Add(StoredPushSubscription subscription)
    {
        lock (_gate)
        {
            var list = Load();
            list.RemoveAll(s => s.Endpoint == subscription.Endpoint);
            list.Add(subscription);
            Persist(list);
        }
    }

    public bool Remove(string endpoint)
    {
        lock (_gate)
        {
            var list = Load();
            if (list.RemoveAll(s => s.Endpoint == endpoint) == 0)
            {
                return false;
            }
            Persist(list);
            return true;
        }
    }

    private List<StoredPushSubscription> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }
        if (File.Exists(_storeFile))
        {
            try
            {
                _cache = JsonSerializer.Deserialize<List<StoredPushSubscription>>(File.ReadAllText(_storeFile));
            }
            catch
            {
                _cache = null; // corrupt file → start clean rather than crash the notifier
            }
        }
        return _cache ??= new List<StoredPushSubscription>();
    }

    private void Persist(List<StoredPushSubscription> list)
    {
        _cache = list;
        Directory.CreateDirectory(Path.GetDirectoryName(_storeFile)!);
        var tmp = _storeFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list));
        File.Move(tmp, _storeFile, overwrite: true);
    }
}
