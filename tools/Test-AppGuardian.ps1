param(
    [Parameter(Mandatory=$true)][string]$Helper,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$Dotnet
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
$fakeDirectory = Join-Path $taskRoot 'fake'
[IO.Directory]::CreateDirectory($fakeDirectory) | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>MediaCaptureHelper</AssemblyName><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $fakeDirectory 'Fake.csproj') -Encoding utf8
@'
if (args.Length != 0 && args[0] == "parent") await Task.Delay(Timeout.Infinite);
else File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "launches.txt"), DateTimeOffset.UtcNow.ToString("O") + Environment.NewLine);
'@ | Set-Content -LiteralPath (Join-Path $fakeDirectory 'Program.cs') -Encoding utf8
& $Dotnet build (Join-Path $fakeDirectory 'Fake.csproj') -c Release --artifacts-path (Join-Path $taskRoot 'build') *> (Join-Path $taskRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Fake application build failed.' }
$fakeExe = Join-Path $taskRoot 'build/bin/Fake/release/MediaCaptureHelper.exe'
$launches = Join-Path ([IO.Path]::GetDirectoryName($fakeExe)) 'launches.txt'
function Start-Hidden([string]$file, [string[]]$arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($file)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($start)
}
function Count-Launches { if (Test-Path -LiteralPath $launches) { return @(Get-Content -LiteralPath $launches).Count }; return 0 }
$results = @()
foreach ($scenario in @('unexpected-death', 'duplicate-observers', 'manual-stop', 'preservation-hold', 'power-handoff', 'stale-heartbeat', 'wrong-generation')) {
    $directory = Join-Path $taskRoot $scenario
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $taskParent = Start-Hidden $fakeExe @('parent')
    $companions = @()
    try {
        $runId = [Guid]::NewGuid().ToString('N')
        $ticks = $taskParent.StartTime.ToUniversalTime().Ticks
        $now = [DateTimeOffset]::UtcNow
        $state = @{ RunId=$runId; ProcessId=$taskParent.Id; ProcessStartUtcTicks=$ticks; BootId='isolated-smoke-boot';
            DesiredMode=1; Transition=0; TransitionToken=$null; Revision=1; CaptureExpected=$true;
            AliveAtUtc=$now.ToString('O'); RecoveryWindowUtc=$now.ToString('O'); RecoveryAttempts=0; Reason='isolated-smoke' }
        if ($scenario -eq 'manual-stop') { $state.DesiredMode=0; $state.CaptureExpected=$false }
        if ($scenario -eq 'power-handoff') { $state.Transition=3; $state.TransitionToken='planned-power' }
        if ($scenario -eq 'stale-heartbeat') { $state.AliveAtUtc=$now.AddMinutes(-5).ToString('O') }
        if ($scenario -eq 'wrong-generation') { $state.RunId=[Guid]::NewGuid().ToString('N') }
        $state | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'app-run-intent.json') -Encoding utf8NoBOM
        if ($scenario -eq 'preservation-hold') { 'preserve originals' | Set-Content -LiteralPath (Join-Path $directory 'original-preservation.hold.json') }
        $before = Count-Launches
        $arguments = @('--watch-app', [string]$taskParent.Id, [string]$ticks, $fakeExe, $directory, $runId)
        $companions += Start-Hidden $Helper $arguments
        if ($scenario -eq 'duplicate-observers') { $companions += Start-Hidden $Helper $arguments }
        # Only terminate the synthetic process started immediately above. No recorder is launched.
        $taskParent.Kill()
        $taskParent.WaitForExit()
        foreach ($companion in $companions) {
            if (!$companion.WaitForExit(15000)) { throw "Observer timeout: $scenario" }
            if ($companion.ExitCode -ne 0) { throw "Observer failed: $scenario, code $($companion.ExitCode)" }
        }
        Start-Sleep -Milliseconds 400
        $count = (Count-Launches) - $before
        $expected = if ($scenario -in @('unexpected-death','duplicate-observers')) { 1 } else { 0 }
        if ($count -ne $expected) { throw "$scenario launches: expected $expected, actual $count" }
        $results += @{scenario=$scenario; launches=$count; expected=$expected; passed=$true}
    }
    finally {
        if (!$taskParent.HasExited) { $taskParent.Kill(); $taskParent.WaitForExit() }
        $taskParent.Dispose()
        foreach ($companion in $companions) { if (!$companion.HasExited) { $companion.Kill() }; $companion.Dispose() }
    }
}
$proof = @{checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); helperSha256=(Get-FileHash -LiteralPath $Helper -Algorithm SHA256).Hash.ToLowerInvariant();
    scenarios=$results; actualRecordingVerified=$false; fieldCommands=0}
$helperAssembly = [IO.Path]::ChangeExtension($Helper, '.dll')
if (Test-Path -LiteralPath $helperAssembly) { $proof.helperAssemblySha256=(Get-FileHash -LiteralPath $helperAssembly -Algorithm SHA256).Hash.ToLowerInvariant() }
$proof | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRoot 'verification.json') -Encoding utf8
$proof | ConvertTo-Json -Depth 6 -Compress
