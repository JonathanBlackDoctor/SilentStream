using SilentStream.Inspection.Installer;
using Xunit;

namespace SilentStream.Inspection.Tests;

public class InstallerFileNameTests
{
    [Theory]
    [InlineData("SilentStream-Install-m112.exe", "m112")]
    [InlineData("SilentStream-Install-m112 (1).exe", "m112")]
    [InlineData("SilentStream-Install-M112 (12).exe", "m112")]
    [InlineData("SilentStream-Install-m112(2).exe", "m112")]
    [InlineData("SilentStream-Install-m111 (1) (2).exe", "m111")]
    [InlineData("SilentStream-Install-m113 (3).exe", "m113")]
    [InlineData("SilentStream-Setup-baek202 (1).exe", "baek202")]
    [InlineData("SilentStream-Install-m112 (other).exe", null)]
    [InlineData("SilentStream-Install-m112 (1) unexpected.exe", null)]
    [InlineData("SilentStream-Central-Installer-v0.4.1-hotfix1.exe", null)]
    [InlineData(null, null)]
    public void Resolves_room_without_treating_browser_copy_number_as_room(string? path, string? expected)
    {
        Assert.Equal(expected, InstallerFileName.RoomFromPath(path));
    }
}
