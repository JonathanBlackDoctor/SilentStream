[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$PublicKeyFile,
  [string]$SourceRoot = '',
  [string]$PreseedFile = '',
  [string]$AppConfigPath = (Join-Path $env:APPDATA 'MediaCaptureHelper\config.json'),
  [string]$AppExecutablePath = (Join-Path $env:LOCALAPPDATA 'MediaCaptureHelper\current\MediaCaptureHelper.exe'),
  [string]$CachedInstallerPath = '',
  [string]$InstallRoot = (Join-Path $env:ProgramFiles 'MediaCaptureHelper\Inspection')
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = Join-Path $PSScriptRoot 'bin' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw '관리자 권한으로 한 번 실행해야 합니다. 파일 우클릭 → PowerShell에서 실행(관리자)을 선택하세요.'
}
& sc.exe stop SilentStreamInspectionCoordinator 2>$null | Out-Null
& sc.exe stop SilentStreamInspectionBroker 2>$null | Out-Null
Stop-ScheduledTask -TaskName 'SilentStream Inspection Session Agent' -ErrorAction SilentlyContinue
Stop-Process -Name MediaCaptureHelper -Force -ErrorAction SilentlyContinue

function New-RandomToken {
  $bytes = New-Object byte[] 32
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
}
function Get-TokenHash([string]$Value) {
  $sha = [Security.Cryptography.SHA256]::Create()
  try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Value)))).Replace('-','').ToLowerInvariant() }
  finally { $sha.Dispose() }
}
function Protect-Machine([string]$Value) {
  Add-Type -AssemblyName System.Security
  $entropy = [Text.Encoding]::UTF8.GetBytes('SilentStream.Inspection.v1')
  $blob = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Value),$entropy,[Security.Cryptography.DataProtectionScope]::LocalMachine)
  return [Convert]::ToBase64String($blob)
}
function Unprotect-AppSecret([string]$Value) {
  if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
  Add-Type -AssemblyName System.Security
  $entropy = [Text.Encoding]::UTF8.GetBytes('SilentStream.v1')
  try {
    $plain = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($Value),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Text.Encoding]::UTF8.GetString($plain)
  } catch { throw '현재 Windows 사용자로 앱의 Cloudflare 인증을 옮길 수 없습니다. 앱을 사용하던 계정에서 설치를 실행하세요.' }
}
function Write-JsonAtomic([string]$Path,$Value) {
  $temp = $Path + '.tmp'
  $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $temp -Encoding UTF8
  Move-Item -LiteralPath $temp -Destination $Path -Force
}

foreach ($relative in @('coordinator\SilentStreamInspectionCoordinator.exe','broker\SilentStreamInspectionBroker.exe','session\SilentStreamInspectionSessionAgent.exe')) {
  if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot $relative))) { throw "배포 파일 누락: $relative" }
}
if (-not (Test-Path -LiteralPath $PublicKeyFile)) { throw "공개키 파일을 찾을 수 없습니다: $PublicKeyFile" }
$publicKey = Get-Content -LiteralPath $PublicKeyFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($publicKey.keyId) -or [string]::IsNullOrWhiteSpace($publicKey.subjectPublicKeyInfo)) { throw '공개키 파일 형식이 올바르지 않습니다.' }

$appConfig = $null
if (Test-Path -LiteralPath $AppConfigPath) { $appConfig = Get-Content -LiteralPath $AppConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json }
$room = if ($appConfig -and -not [string]::IsNullOrWhiteSpace($appConfig.deviceName)) { [string]$appConfig.deviceName } else { $env:COMPUTERNAME }
$hostname = if ($appConfig -and $appConfig.remote) { [string]$appConfig.remote.cloudflareHostname } else { '' }
$tunnelToken = ''
if ($appConfig -and $appConfig.remote) {
  if (-not [string]::IsNullOrWhiteSpace([string]$appConfig.remote.cloudflareTunnelTokenEnc)) { $tunnelToken = Unprotect-AppSecret ([string]$appConfig.remote.cloudflareTunnelTokenEnc) }
  elseif (-not [string]::IsNullOrWhiteSpace([string]$appConfig.remote.cloudflareTunnelToken)) { $tunnelToken = [string]$appConfig.remote.cloudflareTunnelToken }
}

$adminToken = ''
$appApiToken = New-RandomToken
$brokerSecret = New-RandomToken
$pairingPin = (Get-Random -Minimum 0 -Maximum 1000000).ToString('D6')
$tokens = @()
if ($appConfig -and $appConfig.remote -and $appConfig.remote.deviceTokens) {
  foreach ($hash in $appConfig.remote.deviceTokens) {
    if (-not [string]::IsNullOrWhiteSpace([string]$hash)) { $tokens += [ordered]@{ hash=[string]$hash; role='operator'; label='기존 연결 기기'; createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O') } }
  }
}
$preseed = $null
if (-not [string]::IsNullOrWhiteSpace($PreseedFile)) {
  if (-not (Test-Path -LiteralPath $PreseedFile)) { throw "사전 등록 파일 없음: $PreseedFile" }
  $preseed = Get-Content -LiteralPath $PreseedFile -Raw -Encoding UTF8 | ConvertFrom-Json
  if ($preseed.schemaVersion -ne 1 -or [string]$preseed.signingKeyId -ne [string]$publicKey.keyId) { throw '사전 등록 파일의 스키마 또는 공개키가 일치하지 않습니다.' }
  foreach ($entry in @($preseed.tokens)) {
    $hash = ([string]$entry.hash).ToLowerInvariant();$role = ([string]$entry.role).ToLowerInvariant()
    if ($hash -notmatch '^[0-9a-f]{64}$' -or $role -notin @('observer','operator','admin')) { throw '사전 등록 토큰 형식이 올바르지 않습니다.' }
    $tokens += [ordered]@{ hash=$hash;role=$role;label=([string]$entry.label);createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
  }
  if (@($preseed.tokens | Where-Object { ([string]$_.role).ToLowerInvariant() -eq 'admin' }).Count -lt 1) { throw '사전 등록 관리자 토큰이 없습니다.' }
} else {
  $adminToken = New-RandomToken
  $tokens += [ordered]@{ hash=(Get-TokenHash $adminToken); role='admin'; label='초기 관리자'; createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
}

if ($appConfig) {
  if (-not $appConfig.remote.deviceTokens) { $appConfig.remote | Add-Member -NotePropertyName deviceTokens -NotePropertyValue @() -Force }
  $apiHash = Get-TokenHash $appApiToken
  if (@($appConfig.remote.deviceTokens) -notcontains $apiHash) { $appConfig.remote.deviceTokens = @($appConfig.remote.deviceTokens) + $apiHash }
  Write-JsonAtomic $AppConfigPath $appConfig
}

$programFilesRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
if (-not $resolvedInstallRoot.StartsWith($programFilesRoot,[StringComparison]::OrdinalIgnoreCase)) { throw '설치 경로는 Program Files 내부여야 합니다.' }
if (Test-Path -LiteralPath $resolvedInstallRoot) { Remove-Item -LiteralPath $resolvedInstallRoot -Recurse -Force }
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $SourceRoot 'coordinator') -Destination $InstallRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot 'broker') -Destination $InstallRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot 'session') -Destination $InstallRoot -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $SourceRoot 'cli')) { Copy-Item -LiteralPath (Join-Path $SourceRoot 'cli') -Destination $InstallRoot -Recurse -Force }

$dataRoot = Join-Path $env:ProgramData 'MediaCaptureHelper\Inspection'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
if ($preseed) {
  $packageRoot = [IO.Path]::GetFullPath((Split-Path $PreseedFile -Parent)).TrimEnd('\') + '\'
  foreach ($group in @(@{ Values=@($preseed.bundles);Target='preseed-bundles' },@{ Values=@($preseed.agentUpdates);Target='incoming-agent-updates' })) {
    $targetDirectory = Join-Path $dataRoot $group.Target;New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    foreach ($relative in $group.Values) {
      $source = [IO.Path]::GetFullPath((Join-Path $packageRoot ([string]$relative)))
      if (-not $source.StartsWith($packageRoot,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $source) -or [IO.Path]::GetExtension($source) -ne '.zip') { throw "안전하지 않은 사전 등록 경로: $relative" }
      Copy-Item -LiteralPath $source -Destination (Join-Path $targetDirectory (Split-Path $source -Leaf)) -Force
    }
  }
}
$cachedPath = '';$cachedHash = ''
if (-not [string]::IsNullOrWhiteSpace($CachedInstallerPath)) {
  if (-not (Test-Path -LiteralPath $CachedInstallerPath)) { throw "재설치 캐시 파일 없음: $CachedInstallerPath" }
  $cacheDir = Join-Path $dataRoot 'installer-cache';New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
  $cachedPath = Join-Path $cacheDir (Split-Path $CachedInstallerPath -Leaf);Copy-Item -LiteralPath $CachedInstallerPath -Destination $cachedPath -Force
  $cachedHash = (Get-FileHash -LiteralPath $cachedPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$cloudflared = ''
$cloudflaredSource = Join-Path (Split-Path $AppExecutablePath -Parent) 'cloudflared\cloudflared.exe'
if (-not (Test-Path -LiteralPath $cloudflaredSource)) { $cloudflaredSource = Join-Path $SourceRoot 'cloudflared\cloudflared.exe' }
if (Test-Path -LiteralPath $cloudflaredSource) {
  $cloudflaredDir = Join-Path $InstallRoot 'cloudflared';New-Item -ItemType Directory -Path $cloudflaredDir -Force | Out-Null
  $cloudflared = Join-Path $cloudflaredDir 'cloudflared.exe';Copy-Item -LiteralPath $cloudflaredSource -Destination $cloudflared -Force
}
$publicKeys = [ordered]@{}
$publicKeys[[string]$publicKey.keyId] = [string]$publicKey.subjectPublicKeyInfo
$config = [ordered]@{
  schemaVersion=1;port=8790;bindAddress='127.0.0.1';room=$room;pairingPin=$pairingPin;tokens=$tokens
  publicKeys=$publicKeys
  cloudflareTunnelTokenMachineEnc=(Protect-Machine $tunnelToken);cloudflareHostname=$hostname;cloudflaredPath=$cloudflared
  cloudflareProtocol='http2';brokerSecretMachineEnc=(Protect-Machine $brokerSecret);appApiTokenMachineEnc=(Protect-Machine $appApiToken)
  appExecutablePath=$AppExecutablePath;appConfigPath=$AppConfigPath;cachedInstallerPath=$cachedPath;cachedInstallerSha256=$cachedHash
}
Write-JsonAtomic (Join-Path $dataRoot 'coordinator.json') $config

$coordinatorExe = Join-Path $InstallRoot 'coordinator\SilentStreamInspectionCoordinator.exe'
$brokerExe = Join-Path $InstallRoot 'broker\SilentStreamInspectionBroker.exe'
$sessionExe = Join-Path $InstallRoot 'session\SilentStreamInspectionSessionAgent.exe'
foreach ($name in @('SilentStreamInspectionCoordinator','SilentStreamInspectionBroker')) { & sc.exe stop $name 2>$null | Out-Null; & sc.exe delete $name 2>$null | Out-Null }
Start-Sleep -Seconds 2
& sc.exe create SilentStreamInspectionCoordinator "binPath= `"$coordinatorExe`"" 'start= auto' 'obj= NT AUTHORITY\LocalService' 'DisplayName= SilentStream Inspection Coordinator' | Out-Null
& sc.exe create SilentStreamInspectionBroker "binPath= `"$brokerExe`"" 'start= auto' 'obj= LocalSystem' 'DisplayName= SilentStream Inspection Privileged Broker' | Out-Null
& sc.exe sidtype SilentStreamInspectionCoordinator unrestricted | Out-Null
& sc.exe sidtype SilentStreamInspectionBroker unrestricted | Out-Null
foreach ($name in @('SilentStreamInspectionCoordinator','SilentStreamInspectionBroker')) { & sc.exe failure $name 'reset= 86400' 'actions= restart/5000/restart/15000/restart/60000' | Out-Null }

& icacls.exe $dataRoot '/inheritance:r' '/grant:r' 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'NT SERVICE\SilentStreamInspectionCoordinator:(OI)(CI)M' | Out-Null
if (-not [string]::IsNullOrWhiteSpace($cachedPath)) { & icacls.exe $cachedPath '/grant:r' "$($identity.Name):(R,X)" | Out-Null }
if (Test-Path -LiteralPath (Split-Path $AppConfigPath -Parent)) {
  & icacls.exe (Split-Path $AppConfigPath -Parent) '/grant' 'SYSTEM:(OI)(CI)F' 'NT SERVICE\SilentStreamInspectionCoordinator:(OI)(CI)M' | Out-Null
}

Import-Module ScheduledTasks
$taskName = 'SilentStream Inspection Session Agent'
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute $sessionExe
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds 0) -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $taskName

Stop-Process -Name cloudflared -Force -ErrorAction SilentlyContinue
& sc.exe start SilentStreamInspectionBroker | Out-Null
& sc.exe start SilentStreamInspectionCoordinator | Out-Null
if (Test-Path -LiteralPath $AppExecutablePath) { Start-Process -FilePath $AppExecutablePath -WindowStyle Hidden }

$resultPath = Join-Path (Split-Path $PublicKeyFile -Parent) '초기_원격점검_연결정보.txt'
$portal = if ([string]::IsNullOrWhiteSpace($hostname)) { 'http://127.0.0.1:8790/inspect/' } else { "https://$hostname/inspect/" }
@"
SilentStream 원격 점검 v0.4.0
호실: $room
포털: $portal
연결 번호(조작자): $pairingPin
초기 관리자 토큰: $adminToken

Cloudflare Public Hostname에서 /inspect/* 는 http://localhost:8790 으로,
그 외 경로는 기존 http://localhost:8787 로 연결되어야 합니다.
관리자 브라우저 연결이 끝나면 이 파일을 안전한 곳으로 옮기거나 삭제하세요.
"@ | Set-Content -LiteralPath $resultPath -Encoding UTF8
if ($preseed) {
  (Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8).Replace('초기 관리자 토큰: ', '초기 관리자 토큰: 작업실 전용 보관소에 사전 등록됨') | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
& icacls.exe $resultPath '/inheritance:r' "/grant:r" "$($identity.Name):(R,W,D)" | Out-Null
Write-Output "설치 완료: $resultPath"
