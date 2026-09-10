# Runs the production transaction with real temporary files and mocked Windows integration.
# Never installs services, starts apps, changes machine ACLs, or edits the user's app config.
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$sourcePath = Join-Path $PSScriptRoot 'Install-InspectionServices.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($sourcePath,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$source = [IO.File]::ReadAllText($sourcePath)
$start = $source.IndexOf('$productRoot = Split-Path $resolvedInstallRoot -Parent')
if ($start -lt 0) { throw 'Transaction entry point not found' }
$transaction = [scriptblock]::Create($source.Substring($start).Replace("Join-Path `$env:ProgramData 'MediaCaptureHelper\Inspection'", '$testDataRoot'))
# Load only pure helpers from the real installer.
foreach ($name in @('Write-JsonAtomic','Get-TokenHash')) {
  $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
  . ([scriptblock]::Create($definition.Extent.Text))
}
function Get-CimInstance { param($ClassName,$Filter) [pscustomobject]@{Name=($Filter -replace "Name='|'",'');PathName='old.exe';DisplayName='old';StartMode='Auto';StartName='LocalSystem';State='Stopped'} }
function Invoke-CimMethod { param($InputObject,$MethodName,$Arguments) [pscustomobject]@{ReturnValue=0} }
function Get-ScheduledTask { param($TaskName) $null }
function Export-ScheduledTask { param($TaskName) '<Task />' }
function Get-Service { param($Name) $null }
function Stop-Service { param($Name) }
function Start-Service { param($Name) }
function Stop-ScheduledTask { param($TaskName) }
function Start-ScheduledTask { param($TaskName) }
function Unregister-ScheduledTask { param($TaskName,$Confirm) }
function Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Principal,$Settings,$Xml,[switch]$Force) }
function New-ScheduledTaskAction { param($Execute) @{} }
function New-ScheduledTaskTrigger { param([switch]$AtLogOn,$User) @{} }
function New-ScheduledTaskPrincipal { param($UserId,$LogonType,$RunLevel) @{} }
function New-ScheduledTaskSettingsSet { param($MultipleInstances,$ExecutionTimeLimit,$RestartCount,$RestartInterval) @{} }
function Import-Module { param($Name) }
function Stop-OwnedProcess { param($Executable) }
function Start-Process { param($FilePath,$WindowStyle) }
function Invoke-ServiceController { param($Arguments,$FailureMessage) }
function Set-InstallAcl { param($Path,$Rules)
  if ($script:fault -eq 'acl' -and $Path -eq $testDataRoot) { throw 'injected ACL failure' }
}
function Register-WindowsService { param($Name,$BinaryPath,$DisplayName,$Account)
  if ($script:fault -eq 'registration') { throw 'injected registration failure' }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('SilentStream-RecoveryTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
  foreach ($case in @('copy','acl','registration')) {
    $script:fault = $case
    $caseRoot = Join-Path $testRoot $case
    $InstallRoot = Join-Path $caseRoot 'Product\Inspection'
    $resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
    $programFilesRoot = [IO.Path]::GetFullPath($caseRoot).TrimEnd('\') + '\'
    $SourceRoot = Join-Path $caseRoot 'source'
    $testDataRoot = Join-Path $caseRoot 'data'
    $AppConfigPath = Join-Path $caseRoot 'app\config.json'
    $AppExecutablePath = Join-Path $caseRoot 'app\MediaCaptureHelper.exe'
    foreach ($dir in @($InstallRoot,$SourceRoot,$testDataRoot,(Split-Path $AppConfigPath -Parent),(Join-Path $caseRoot 'Product\InspectionPolicy'))) {
      New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $InstallRoot 'old.txt'),'original binaries')
    [IO.File]::WriteAllText((Join-Path $caseRoot 'Product\InspectionPolicy\old.txt'),'original policy')
    [IO.File]::WriteAllText((Join-Path $testDataRoot 'coordinator.json'),'original coordinator')
    [IO.File]::WriteAllText($AppConfigPath,'{"deviceName":"M112","remote":{"deviceTokens":[]}}')
    $originalApp = [IO.File]::ReadAllText($AppConfigPath)
    foreach ($component in @('coordinator','broker','session','cli')) {
      if ($case -eq 'copy' -and $component -eq 'broker') { continue }
      New-Item -ItemType Directory -Path (Join-Path $SourceRoot $component) | Out-Null
      [IO.File]::WriteAllText((Join-Path $SourceRoot "$component\new.txt"),'new binaries')
    }
    $appConfig = $originalApp | ConvertFrom-Json
    $previousBridgeToken = ''; $appApiToken = 'test-api-token'; $inspectionBridgeTokenEnc = 'test-encrypted'
    $central = $null; $preseed = $null; $CachedInstallerPath = ''; $cloudflaredSource = Join-Path $caseRoot 'absent.exe'
    $publicKey = [pscustomobject]@{keyId='test';subjectPublicKeyInfo='test'}
    $room = 'M112'; $roomId = 'm112'; $requiredMicrophoneDeviceIds = @{}; $tokens = @()
    $pairingPin = ''; $tunnelTokenMachineEnc = ''; $hostname = ''; $brokerSecretMachineEnc = ''
    $appApiTokenMachineEnc = ''; $centralInstallationId = ''; $centralDeviceSecretMachineEnc = ''
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $failed = $false
    try { . $transaction } catch {
      $failed = $true
      if ($_.Exception.Message -notmatch '기존 설정과 서비스 파일을 복구했습니다') { throw }
    }
    if (-not $failed) { throw "$case did not exercise rollback" }
    if ([IO.File]::ReadAllText($AppConfigPath) -ne $originalApp) { throw "${case}: app config not restored" }
    if ([IO.File]::ReadAllText((Join-Path $InstallRoot 'old.txt')) -ne 'original binaries') { throw "${case}: binaries not restored" }
    if ([IO.File]::ReadAllText((Join-Path $caseRoot 'Product\InspectionPolicy\old.txt')) -ne 'original policy') { throw "${case}: policy not restored" }
    if ([IO.File]::ReadAllText((Join-Path $testDataRoot 'coordinator.json')) -ne 'original coordinator') { throw "${case}: credentials not restored" }
    Write-Output "PASS: $case failure restores original app, binaries, policy and coordinator configuration"
  }
} finally {
  $resolved = [IO.Path]::GetFullPath($testRoot)
  if ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('SilentStream-RecoveryTest-')) {
    Remove-Item -LiteralPath $resolved -Recurse -Force
  }
}
