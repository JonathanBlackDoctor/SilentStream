using SilentStream.Core.Implementations;
using Xunit;

namespace SilentStream.Tests;

public class TokenProtectorTests
{
    [Fact]
    public void Dpapi_roundtrip_preserves_token()
    {
        if (!OperatingSystem.IsWindows())
        {
            // DPAPI는 Windows 전용 — 로컬 Windows / windows CI 잡에서 실행된다.
            return;
        }

        var protector = new DpapiTokenProtector();
        const string token = "1//refresh-token-예시-ÆØ✓";

        var blob = protector.Protect(token);

        Assert.NotEqual(token, blob);
        Assert.Equal(token, protector.Unprotect(blob));
    }

    [Fact]
    public void Dpapi_throws_on_unsupported_platform()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new DpapiTokenProtector();
        Assert.Throws<PlatformNotSupportedException>(() => protector.Protect("x"));
        Assert.Throws<PlatformNotSupportedException>(() => protector.Unprotect("eA=="));
    }
}
