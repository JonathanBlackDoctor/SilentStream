using Xunit;

namespace SilentStream.Tests;

public sealed class DeploymentFailClosedStructureTests
{
    [Fact]
    public void Installer_accepts_only_adjacent_preseed_or_an_authenticated_hash_checked_package()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository, "src", "SilentStream.Inspection.Installer", "Program.cs"));

        AssertOrdered(source,
            "if (File.Exists(legacyPreseed))",
            "AuthorizeAndEnrollAsync(controlPlane, room)",
            "DownloadPackageAsync(enrollment, archive)",
            "ZipFile.ExtractToDirectory(archive, packageRoot)",
            "RunPackageInstallAsync(packageRoot, enrollmentFile, installAppFirst: true)");
        Assert.Contains("await VerifiedDownload.RunAsync", source);
        AssertOrdered(source,
            "var preseed = Path.Combine(packageRoot, \"deployment-preseed.json\");",
            "if (!File.Exists(script) || !Directory.Exists(source) || !File.Exists(publicKey) || !File.Exists(preseed))",
            "var start = new ProcessStartInfo(powershell)",
            "start.ArgumentList.Add(\"-PreseedFile\")",
            "start.ArgumentList.Add(preseed)",
            "Process.Start(start)");
    }

    [Fact]
    public void Service_installer_requires_and_verifies_every_preseed_bundle_before_stopping_services()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository, "inspection", "deployment", "Install-InspectionServices.ps1"));

        Assert.Contains(
            "[Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$PreseedFile",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "'cli\\SilentStreamInspectionCli.exe'",
            source,
            StringComparison.Ordinal);

        AssertOrdered(source,
            "if ([string]::IsNullOrWhiteSpace($PreseedFile)",
            "$preseed = Get-Content -LiteralPath $PreseedFile",
            "if (@($preseed.bundles).Count -lt 1)",
            "$cli = Join-Path $SourceRoot 'cli\\SilentStreamInspectionCli.exe'",
            "foreach ($relative in @($preseed.bundles))",
            "& $cli bundle verify-startup $source $PublicKeyFile $destination",
            "foreach ($relative in @($preseed.agentUpdates))",
            "& $cli agent-update verify $source $PublicKeyFile $destination",
            "Stop-Service -Name $name -ErrorAction Stop");

        var verificationBlock = Slice(source,
            "$cli = Join-Path $SourceRoot 'cli\\SilentStreamInspectionCli.exe'",
            "$cloudflaredSource = Join-Path");
        Assert.Contains("if ($LASTEXITCODE -ne 0)", verificationBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("SilentlyContinue", verificationBlock, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Base_package_outputs_are_explicitly_marked_unsigned_and_not_install_ready()
    {
        var repository = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repository, ".github", "workflows", "build.yml"));
        var packageScript = File.ReadAllText(Path.Combine(
            repository, "inspection", "deployment", "Build-InspectionPackage.ps1"));

        Assert.Contains("inspection-package-unsigned-base-v", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("inspection-package-v${{ steps.ver.outputs.version }}.zip", workflow, StringComparison.Ordinal);
        Assert.Contains("서명전_기본패키지", packageScript, StringComparison.Ordinal);
        Assert.Contains("deployment prepare", packageScript, StringComparison.Ordinal);
        Assert.Contains("deployment-preseed.json", packageScript, StringComparison.Ordinal);
        Assert.Contains("강의실 PC에 설치", packageScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_registration_avoids_native_creation_and_keeps_recovery_material()
    {
        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repository, "inspection", "deployment", "Install-InspectionServices.ps1"));

        Assert.Contains("function Invoke-ServiceController", source, StringComparison.Ordinal);
        Assert.Contains("$output = @(& $sc @Arguments 2>&1)", source, StringComparison.Ordinal);
        Assert.Contains("-MethodName Change", source, StringComparison.Ordinal);
        Assert.Contains("-MethodName Create", source, StringComparison.Ordinal);
        Assert.Contains("$result.ReturnValue -ne 0", source, StringComparison.Ordinal);
        Assert.Contains("InspectionRollback-", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrators:(OI)(CI)F", source, StringComparison.Ordinal);
        Assert.Contains("@('failure',$name,'reset=','86400','actions='", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"binPath= `\"$coordinatorExe`\"\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("& sc.exe create", source, StringComparison.Ordinal);
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        var previous = -1;
        foreach (var marker in markers)
        {
            var current = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Deployment safety marker was not found: {marker}");
            Assert.True(current > previous, $"Deployment safety marker is out of order: {marker}");
            previous = current;
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Source marker was not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Source marker was not found after start: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SilentStream.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("SilentStream repository root was not found.");
    }
}
