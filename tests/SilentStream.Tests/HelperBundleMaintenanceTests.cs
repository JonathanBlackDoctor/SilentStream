using Xunit;

namespace SilentStream.Tests;

public sealed class HelperBundleMaintenanceTests
{
    [Fact]
    public void New_helper_is_packaged_outside_the_legacy_automatic_import_directory()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SilentStream.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "release-app-0.4.31.yml"));
        Assert.Contains("--dir publish/inspection-maintenance", workflow);
        Assert.DoesNotContain("--dir publish/inspection-update", workflow);
        Assert.Contains("Automatic helper-import directory must remain absent", workflow);
    }
}
