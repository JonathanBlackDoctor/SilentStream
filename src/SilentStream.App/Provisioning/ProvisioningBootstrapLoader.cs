using System.IO;
using System.Text.Json;
using SilentStream.Core.Provisioning;

namespace SilentStream.App.Provisioning;

/// <summary>
/// Loads the optional, non-secret installer bootstrap. The release workflow creates this file only
/// when the repository has a <c>ROOM_PROVISIONING_URL</c> variable, so development builds and
/// existing deployments remain unchanged. Tunnel tokens are intentionally never bundled here.
/// </summary>
public static class ProvisioningBootstrapLoader
{
    public static RoomProvisioningBootstrap? TryLoad()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "provisioning.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bootstrap = JsonSerializer.Deserialize<RoomProvisioningBootstrap>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return string.IsNullOrWhiteSpace(bootstrap?.ServiceUrl) ? null : bootstrap;
        }
        catch (JsonException)
        {
            // A malformed optional bootstrap must never stop the unattended recording path.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
