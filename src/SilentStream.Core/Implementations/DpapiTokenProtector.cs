using System.Security.Cryptography;
using System.Text;
using SilentStream.Core.Contracts;

namespace SilentStream.Core.Implementations;

/// <summary>
/// DPAPI-backed secret protection (CurrentUser scope) with extra per-app entropy.
/// Windows-only at runtime; throws <see cref="PlatformNotSupportedException"/> elsewhere
/// so misuse on non-Windows hosts fails loudly instead of storing plaintext.
/// </summary>
public sealed class DpapiTokenProtector : ITokenProtector
{
    // Not a secret: binds blobs to this app so unrelated DPAPI data can't be swapped in.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SilentStream.v1");

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI token protection requires Windows.");
        }
        var blob = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    public string Unprotect(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI token protection requires Windows.");
        }
        var plain = ProtectedData.Unprotect(
            Convert.FromBase64String(ciphertext), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }
}
