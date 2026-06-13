using Google.Apis.Json;
using Google.Apis.Util.Store;
using SilentStream.Core.Contracts;

namespace SilentStream.Core.YouTube;

/// <summary>
/// Google OAuth token store backed by config.json: the serialized token response is
/// encrypted via <see cref="ITokenProtector"/> (DPAPI) before persisting (plan §3.7/§3.10).
/// </summary>
public sealed class EncryptedTokenDataStore(IConfigStore configStore, ITokenProtector protector)
    : IDataStore
{
    public Task StoreAsync<T>(string key, T value)
    {
        var json = NewtonsoftJsonSerializer.Instance.Serialize(value);
        var config = configStore.Load();
        config.YouTube.RefreshTokenEnc = protector.Protect(json);
        configStore.Save(config);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        var config = configStore.Load();
        config.YouTube.RefreshTokenEnc = string.Empty;
        configStore.Save(config);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        var blob = configStore.Load().YouTube.RefreshTokenEnc;
        if (string.IsNullOrEmpty(blob))
        {
            return Task.FromResult<T>(default!);
        }

        try
        {
            var json = protector.Unprotect(blob);
            return Task.FromResult(NewtonsoftJsonSerializer.Instance.Deserialize<T>(json));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            // Blob from another machine/user (DPAPI scope) — force a fresh login.
            return Task.FromResult<T>(default!);
        }
    }

    public Task ClearAsync() => DeleteAsync<object>(string.Empty);
}
