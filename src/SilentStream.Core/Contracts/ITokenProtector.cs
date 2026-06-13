namespace SilentStream.Core.Contracts;

/// <summary>
/// Encrypts/decrypts secrets (OAuth refresh tokens) at rest. The production
/// implementation uses Windows DPAPI (CurrentUser scope) — plan §3.10.
/// </summary>
public interface ITokenProtector
{
    /// <summary>Encrypts plaintext and returns a base64 blob safe to store in config.json.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a blob produced by <see cref="Protect"/>.</summary>
    string Unprotect(string ciphertext);
}
