using Google.Apis.Json;
using Google.Apis.Util.Store;
using SilentStream.Core.Contracts;

namespace SilentStream.Core.YouTube;

/// <summary>
/// Single-credential OAuth data store encrypted with the current user's DPAPI protector. Caption
/// download uses this instead of the live-stream token because its additional force-ssl consent
/// must not invalidate or silently replace an established broadcast credential.
/// </summary>
public sealed class EncryptedFileTokenDataStore(string tokenFile, ITokenProtector protector) : IDataStore
{
    private readonly object _gate = new();
    private readonly string _tokenFile = Path.GetFullPath(tokenFile);

    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var encrypted = protector.Protect(json);
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_tokenFile);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = _tokenFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, encrypted);
                File.Move(temporary, _tokenFile, overwrite: true);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        lock (_gate)
        {
            TryDelete(_tokenFile);
        }
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        lock (_gate)
        {
            if (!File.Exists(_tokenFile))
            {
                return Task.FromResult<T>(default!);
            }

            try
            {
                var encrypted = File.ReadAllText(_tokenFile);
                var json = protector.Unprotect(encrypted);
                return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
            {
                // A copied file belongs to another Windows user/machine. Treat it as a fresh login
                // rather than surfacing a DPAPI error from a button click on the remote screen.
                return Task.FromResult<T>(default!);
            }
        }
    }

    public Task ClearAsync() => DeleteAsync<object>(string.Empty);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The next credential refresh can overwrite it; do not fail an OAuth callback solely
            // because a stale file was momentarily locked by anti-virus/indexing.
        }
    }
}
