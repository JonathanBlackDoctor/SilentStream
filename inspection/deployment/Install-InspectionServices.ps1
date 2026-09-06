[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$PublicKeyFile,
  [string]$SourceRoot = '',
  [Parameter(Mandatory=$true)][ValidateNotNullOrEmpty()][string]$PreseedFile,
  [string]$AppConfigPath = (Join-Path $env:APPDATA 'MediaCaptureHelper\config.json'),
  [string]$AppExecutablePath = (Join-Path $env:LOCALAPPDATA 'MediaCaptureHelper\current\MediaCaptureHelper.exe'),
  [string]$CachedInstallerPath = '',
  [string]$CentralEnrollmentFile = '',
  [string]$InstallRoot = (Join-Path $env:ProgramFiles 'MediaCaptureHelper\Inspection')
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = Join-Path $PSScriptRoot 'bin' }
if ([string]::IsNullOrWhiteSpace($PreseedFile) -or -not (Test-Path -LiteralPath $PreseedFile)) {
  throw '서명된 부팅 안전 점검이 포함된 deployment-preseed.json이 필요합니다. 서명 전 기본 패키지는 설치할 수 없습니다.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw '관리자 권한으로 한 번 실행해야 합니다. 파일 우클릭 → PowerShell에서 실행(관리자)을 선택하세요.'
}
if (-not (Test-Path -LiteralPath $AppConfigPath)) {
  throw 'SilentStream 앱 설정이 아직 없습니다. 앱을 먼저 설치해 한 번 실행하고 호실을 지정한 뒤 원격 점검 설치를 다시 실행하세요.'
}
try { $appConfig = Get-Content -LiteralPath $AppConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json }
catch { throw "SilentStream 앱 설정을 읽을 수 없습니다. 앱에서 설정을 복구한 뒤 다시 실행하세요: $($_.Exception.Message)" }
if (-not $appConfig -or -not $appConfig.remote) {
  throw 'SilentStream 앱 설정에 원격 연결 항목이 없습니다. 앱을 최신 버전으로 한 번 실행한 뒤 다시 시도하세요.'
}
function New-RandomToken {
  $bytes = New-Object byte[] 32
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
}
function New-CryptographicPin {
  $bytes = New-Object byte[] 4
  [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  $value = [BitConverter]::ToUInt32($bytes,0) % 1000000
  return $value.ToString('D6')
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
function Protect-AppSecret([string]$Value) {
  Add-Type -AssemblyName System.Security
  $entropy = [Text.Encoding]::UTF8.GetBytes('SilentStream.v1')
  $blob = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Value),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
  return [Convert]::ToBase64String($blob)
}
function Write-JsonAtomic([string]$Path,$Value) {
  $temp = $Path + '.tmp'
  $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $temp -Encoding UTF8
  Move-Item -LiteralPath $temp -Destination $Path -Force
}

$central = $null
if (-not [string]::IsNullOrWhiteSpace($CentralEnrollmentFile)) {
  if (-not (Test-Path -LiteralPath $CentralEnrollmentFile)) { throw "중앙 등록 파일 없음: $CentralEnrollmentFile" }
  try { $central = Get-Content -LiteralPath $CentralEnrollmentFile -Raw -Encoding UTF8 | ConvertFrom-Json }
  catch { throw "중앙 등록 파일을 읽을 수 없습니다: $($_.Exception.Message)" }
  if ([string]::IsNullOrWhiteSpace([string]$central.roomId) -or [string]$central.roomId -notmatch '^[a-z0-9_-]{1,64}$' -or
      [string]::IsNullOrWhiteSpace([string]$central.deviceId) -or [string]$central.deviceId -notmatch '^[a-f0-9]{32}$' -or
      [string]::IsNullOrWhiteSpace([string]$central.deviceSecret) -or ([string]$central.deviceSecret).Length -lt 32 -or
      [string]::IsNullOrWhiteSpace([string]$central.deviceWebSocketUri) -or -not ([string]$central.deviceWebSocketUri).StartsWith('wss://',[StringComparison]::OrdinalIgnoreCase) -or
      [string]::IsNullOrWhiteSpace([string]$central.controlPlaneBaseUri) -or -not ([string]$central.controlPlaneBaseUri).StartsWith('https://',[StringComparison]::OrdinalIgnoreCase)) {
    throw '중앙 등록 파일의 호실, 기기 자격증명 또는 HTTPS 주소가 올바르지 않습니다.'
  }
}

foreach ($relative in @('coordinator\SilentStreamInspectionCoordinator.exe','broker\SilentStreamInspectionBroker.exe','session\SilentStreamInspectionSessionAgent.exe','cli\SilentStreamInspectionCli.exe')) {
  if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot $relative))) { throw "배포 파일 누락: $relative" }
}
if (-not (Test-Path -LiteralPath $PublicKeyFile)) { throw "공개키 파일을 찾을 수 없습니다: $PublicKeyFile" }
$publicKey = Get-Content -LiteralPath $PublicKeyFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($publicKey.keyId) -or [string]::IsNullOrWhiteSpace($publicKey.subjectPublicKeyInfo)) { throw '공개키 파일 형식이 올바르지 않습니다.' }

$room = if ($appConfig -and -not [string]::IsNullOrWhiteSpace($appConfig.deviceName)) { [string]$appConfig.deviceName } else { $env:COMPUTERNAME }
$roomId = if ($appConfig -and $appConfig.provisioning -and -not [string]::IsNullOrWhiteSpace([string]$appConfig.provisioning.roomId)) { [string]$appConfig.provisioning.roomId } else { $room.ToLowerInvariant() }
$centralInstallationId = ''
if ($central) {
  $roomId = ([string]$central.roomId).Trim().ToLowerInvariant()
  $room = if ($roomId -match '^baek([0-9]+)$') { "백$($Matches[1])" } else { $roomId.ToUpperInvariant() }
  $centralInstallationId = [string]$central.installationId
  if ([string]::IsNullOrWhiteSpace($centralInstallationId) -or $centralInstallationId.Length -gt 128) {
    throw '중앙 등록 파일의 설치 식별자가 올바르지 않습니다.'
  }
}
$normalizedRoomId = $roomId.Trim().ToLowerInvariant().Replace('강의실','').Replace('호','').Trim()
if ($normalizedRoomId -match '^백([0-9]+)$') { $normalizedRoomId = "baek$($Matches[1])" }
$requiredMicrophoneDeviceIds = [ordered]@{}
if ($appConfig -and $appConfig.audio -and $appConfig.audio.sources) {
  $realMics = @($appConfig.audio.sources | Where-Object {
    ([string]$_.kind).ToLowerInvariant() -eq 'mic' -and
    ([string]$_.name) -notmatch '(?i)stereo\s*mix|스테레오\s*믹스|what\s*u\s*hear|wave\s*out|loopback|monitor\s+of'
  })
  $requiredMics = if ($normalizedRoomId -eq 'm113') { @($realMics | Where-Object { ([string]$_.name) -match '(?i)xaudio' }) } else { @() }
  if ($requiredMics.Count -eq 0 -and $realMics.Count -eq 1) { $requiredMics = @($realMics[0]) }
  $requiredIds = @($requiredMics | ForEach-Object { [string]$_.deviceId } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
  if ($requiredIds.Count -gt 0) { $requiredMicrophoneDeviceIds[$normalizedRoomId] = $requiredIds }
}
$hostname = if ($appConfig -and $appConfig.remote) { [string]$appConfig.remote.cloudflareHostname } else { '' }
$tunnelToken = ''
$previousBridgeToken = ''
if ($appConfig -and $appConfig.remote) {
  if (-not [string]::IsNullOrWhiteSpace([string]$appConfig.remote.cloudflareTunnelTokenEnc)) { $tunnelToken = Unprotect-AppSecret ([string]$appConfig.remote.cloudflareTunnelTokenEnc) }
  elseif (-not [string]::IsNullOrWhiteSpace([string]$appConfig.remote.cloudflareTunnelToken)) { $tunnelToken = [string]$appConfig.remote.cloudflareTunnelToken }
  if (-not [string]::IsNullOrWhiteSpace([string]$appConfig.remote.inspectionBridgeTokenEnc)) { $previousBridgeToken = Unprotect-AppSecret ([string]$appConfig.remote.inspectionBridgeTokenEnc) }
}
if ($central) { $hostname = ''; $tunnelToken = '' }

$adminToken = ''
$appApiToken = New-RandomToken
$brokerSecret = New-RandomToken
$pairingPin = New-CryptographicPin
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
  if (@($preseed.bundles).Count -lt 1) { throw '사전 등록된 서명 부팅 점검 번들이 없습니다.' }
} else {
  $adminToken = New-RandomToken
  $tokens += [ordered]@{ hash=(Get-TokenHash $adminToken); role='admin'; label='초기 관리자'; createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
}

$programFilesRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
if (-not $resolvedInstallRoot.StartsWith($programFilesRoot,[StringComparison]::OrdinalIgnoreCase)) { throw '설치 경로는 Program Files 내부여야 합니다.' }
if (-not (Test-Path -LiteralPath $AppExecutablePath)) { throw "SilentStream 앱 실행 파일을 찾을 수 없습니다: $AppExecutablePath" }
if (-not [string]::IsNullOrWhiteSpace($CachedInstallerPath) -and -not (Test-Path -LiteralPath $CachedInstallerPath)) {
  throw "재설치 캐시 파일 없음: $CachedInstallerPath"
}
if ($preseed) {
  $preflightPackageRoot = [IO.Path]::GetFullPath((Split-Path $PreseedFile -Parent)).TrimEnd('\') + '\'
  foreach ($relative in @($preseed.bundles) + @($preseed.agentUpdates)) {
    $source = [IO.Path]::GetFullPath((Join-Path $preflightPackageRoot ([string]$relative)))
    if (-not $source.StartsWith($preflightPackageRoot,[StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $source) -or [IO.Path]::GetExtension($source) -ne '.zip') {
      throw "안전하지 않은 사전 등록 경로: $relative"
    }
  }
  $verificationRoot = Join-Path ([IO.Path]::GetTempPath()) ('SilentStreamInspectionPreflight-' + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $verificationRoot | Out-Null
  try {
    $cli = Join-Path $SourceRoot 'cli\SilentStreamInspectionCli.exe'
    foreach ($relative in @($preseed.bundles)) {
      $source = [IO.Path]::GetFullPath((Join-Path $preflightPackageRoot ([string]$relative)))
      $destination = Join-Path $verificationRoot ([Guid]::NewGuid().ToString('N'))
      & $cli bundle verify-startup $source $PublicKeyFile $destination | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "서명된 부팅 안전 점검 사전 검증 실패: $relative" }
    }
    foreach ($relative in @($preseed.agentUpdates)) {
      $source = [IO.Path]::GetFullPath((Join-Path $preflightPackageRoot ([string]$relative)))
      $destination = Join-Path $verificationRoot ([Guid]::NewGuid().ToString('N'))
      & $cli agent-update verify $source $PublicKeyFile $destination | Out-Null
      if ($LASTEXITCODE -ne 0) { throw "서명된 점검 서비스 업데이트 사전 검증 실패: $relative" }
    }
  }
  finally {
    if (Test-Path -LiteralPath $verificationRoot) {
      try { [IO.Directory]::Delete([IO.Path]::GetFullPath($verificationRoot), $true) } catch { }
    }
  }
}
$cloudflaredSource = Join-Path (Split-Path $AppExecutablePath -Parent) 'cloudflared\cloudflared.exe'
if (-not (Test-Path -LiteralPath $cloudflaredSource)) { $cloudflaredSource = Join-Path $SourceRoot 'cloudflared\cloudflared.exe' }
if ((-not [string]::IsNullOrWhiteSpace($hostname) -or -not [string]::IsNullOrWhiteSpace($tunnelToken)) -and
    -not (Test-Path -LiteralPath $cloudflaredSource)) {
  throw 'Cloudflare 원격 주소 또는 토큰이 설정되어 있지만 cloudflared.exe가 배포 파일에 없습니다.'
}

# Exercise both DPAPI scopes before stopping a working installation. A different Windows account,
# malformed ciphertext or unavailable profile now fails without taking the current services down.
$inspectionBridgeTokenEnc = Protect-AppSecret $appApiToken
$tunnelTokenMachineEnc = Protect-Machine $tunnelToken
$brokerSecretMachineEnc = Protect-Machine $brokerSecret
$appApiTokenMachineEnc = Protect-Machine $appApiToken
$centralDeviceSecretMachineEnc = if ($central) { Protect-Machine ([string]$central.deviceSecret) } else { '' }

& sc.exe stop SilentStreamInspectionCoordinator 2>$null | Out-Null
& sc.exe stop SilentStreamInspectionBroker 2>$null | Out-Null
Stop-ScheduledTask -TaskName 'SilentStream Inspection Session Agent' -ErrorAction SilentlyContinue
Stop-Process -Name MediaCaptureHelper -Force -ErrorAction SilentlyContinue
Stop-Process -Name SilentStreamInspectionSessionAgent -Force -ErrorAction SilentlyContinue
foreach ($serviceName in @('SilentStreamInspectionCoordinator','SilentStreamInspectionBroker')) {
  $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
  if ($service -and $service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
    try { $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(30)) }
    catch { throw "$serviceName 서비스를 안전하게 중지하지 못했습니다. 기존 설치는 삭제하지 않았습니다." }
  }
}

if ($appConfig) {
  if (-not $appConfig.remote.deviceTokens) { $appConfig.remote | Add-Member -NotePropertyName deviceTokens -NotePropertyValue @() -Force }
  if (-not [string]::IsNullOrWhiteSpace($previousBridgeToken)) {
    $previousApiHash = Get-TokenHash $previousBridgeToken
    $appConfig.remote.deviceTokens = @($appConfig.remote.deviceTokens | Where-Object { [string]$_ -ne $previousApiHash })
  }
  $apiHash = Get-TokenHash $appApiToken
  if (@($appConfig.remote.deviceTokens) -notcontains $apiHash) { $appConfig.remote.deviceTokens = @($appConfig.remote.deviceTokens) + $apiHash }
  $appConfig.remote | Add-Member -NotePropertyName inspectionBridgeTokenEnc -NotePropertyValue $inspectionBridgeTokenEnc -Force
  if ($central) {
    $appConfig.deviceName = $room
    $appConfig.remote.mode = 'central'
    $appConfig.remote.port = 8787
    $appConfig.remote.cloudflareHostname = ''
    $appConfig.remote.cloudflareTunnelToken = ''
    $appConfig.remote.cloudflareTunnelTokenEnc = ''
    if (-not $appConfig.provisioning) { $appConfig | Add-Member -NotePropertyName provisioning -NotePropertyValue ([ordered]@{}) -Force }
    $appConfig.provisioning | Add-Member -NotePropertyName roomId -NotePropertyValue $roomId -Force
    $appConfig.provisioning | Add-Member -NotePropertyName installationId -NotePropertyValue $centralInstallationId -Force
    $appConfig.provisioning | Add-Member -NotePropertyName completed -NotePropertyValue $true -Force
    $appConfig.provisioning | Add-Member -NotePropertyName completedAtUtc -NotePropertyValue ([DateTimeOffset]::UtcNow.ToString('O')) -Force
  }
  Write-JsonAtomic $AppConfigPath $appConfig
}

$productRoot = Split-Path $resolvedInstallRoot -Parent
$policyRoot = Join-Path $productRoot 'InspectionPolicy'
$stagingRoot = Join-Path $productRoot 'InspectionAgentStaging'
$updaterRoot = Join-Path $productRoot 'InspectionUpdater'
if (Test-Path -LiteralPath $resolvedInstallRoot) { Remove-Item -LiteralPath $resolvedInstallRoot -Recurse -Force }
foreach ($old in @($policyRoot,$stagingRoot,$updaterRoot)) { if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force } }
New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path $policyRoot -Force | Out-Null
& icacls.exe $policyRoot '/inheritance:r' '/grant:r' 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
Copy-Item -LiteralPath (Join-Path $SourceRoot 'coordinator') -Destination $InstallRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot 'broker') -Destination $InstallRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $SourceRoot 'session') -Destination $InstallRoot -Recurse -Force
if (Test-Path -LiteralPath (Join-Path $SourceRoot 'cli')) { Copy-Item -LiteralPath (Join-Path $SourceRoot 'cli') -Destination $InstallRoot -Recurse -Force }

$dataRoot = Join-Path $env:ProgramData 'MediaCaptureHelper\Inspection'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
& icacls.exe $dataRoot '/inheritance:r' '/grant:r' 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
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
  $cacheDir = Join-Path $policyRoot 'installer-cache';New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
  $cachedPath = Join-Path $cacheDir (Split-Path $CachedInstallerPath -Leaf);Copy-Item -LiteralPath $CachedInstallerPath -Destination $cachedPath -Force
  $cachedHash = (Get-FileHash -LiteralPath $cachedPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$cloudflared = ''
if (Test-Path -LiteralPath $cloudflaredSource) {
  $cloudflaredDir = Join-Path $InstallRoot 'cloudflared';New-Item -ItemType Directory -Path $cloudflaredDir -Force | Out-Null
  $cloudflared = Join-Path $cloudflaredDir 'cloudflared.exe';Copy-Item -LiteralPath $cloudflaredSource -Destination $cloudflared -Force
}
$publicKeys = [ordered]@{}
$publicKeys[[string]$publicKey.keyId] = [string]$publicKey.subjectPublicKeyInfo
$centralEnabled = [bool]($null -ne $central)
$centralBaseUri = if ($central) { [string]$central.controlPlaneBaseUri } else { '' }
$centralWebSocketUri = if ($central) { [string]$central.deviceWebSocketUri } else { '' }
$centralDeviceId = if ($central) { [string]$central.deviceId } else { '' }
$config = [ordered]@{
  schemaVersion=1;port=8790;bindAddress='127.0.0.1';room=$room;roomId=$roomId
  managedRooms=@('m111','m112','m113','baek202');requiredMicrophoneDeviceIds=$requiredMicrophoneDeviceIds;startupInspectionEnabled=$true;startupInspectionDelaySeconds=20
  pairingPin=$pairingPin;tokens=$tokens
  publicKeys=$publicKeys
  cloudflareTunnelTokenMachineEnc=$tunnelTokenMachineEnc;cloudflareHostname=$hostname;cloudflaredPath=$cloudflared
  cloudflareProtocol='http2';brokerSecretMachineEnc=$brokerSecretMachineEnc;appApiTokenMachineEnc=$appApiTokenMachineEnc
  centralControlEnabled=$centralEnabled
  centralControlBaseUri=$centralBaseUri
  centralControlWebSocketUri=$centralWebSocketUri
  centralDeviceId=$centralDeviceId
  centralInstallationId=$centralInstallationId
  centralDeviceSecretMachineEnc=$centralDeviceSecretMachineEnc
  appExecutablePath=$AppExecutablePath;appConfigPath=$AppConfigPath;cachedInstallerPath=$cachedPath;cachedInstallerSha256=$cachedHash
}
Write-JsonAtomic (Join-Path $dataRoot 'coordinator.json') $config
$brokerPolicy = [ordered]@{
  schemaVersion=1;publicKeys=$publicKeys;appExecutablePath=$AppExecutablePath
  cachedInstallerPath=$cachedPath;cachedInstallerSha256=$cachedHash
}
Write-JsonAtomic (Join-Path $policyRoot 'broker-policy.json') $brokerPolicy

$coordinatorExe = Join-Path $InstallRoot 'coordinator\SilentStreamInspectionCoordinator.exe'
$brokerExe = Join-Path $InstallRoot 'broker\SilentStreamInspectionBroker.exe'
$sessionExe = Join-Path $InstallRoot 'session\SilentStreamInspectionSessionAgent.exe'
foreach ($name in @('SilentStreamInspectionCoordinator','SilentStreamInspectionBroker')) {
  & sc.exe delete $name 2>$null | Out-Null
  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
  while ((Get-Service -Name $name -ErrorAction SilentlyContinue) -and [DateTimeOffset]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
  if (Get-Service -Name $name -ErrorAction SilentlyContinue) { throw "$name 기존 서비스 등록을 교체할 수 없습니다." }
}
& sc.exe create SilentStreamInspectionCoordinator "binPath= `"$coordinatorExe`"" 'start= auto' 'obj= NT AUTHORITY\LocalService' 'DisplayName= SilentStream Inspection Coordinator' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Coordinator 서비스 등록 실패: $LASTEXITCODE" }
& sc.exe create SilentStreamInspectionBroker "binPath= `"$brokerExe`"" 'start= auto' 'obj= LocalSystem' 'DisplayName= SilentStream Inspection Privileged Broker' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Broker 서비스 등록 실패: $LASTEXITCODE" }
& sc.exe sidtype SilentStreamInspectionCoordinator unrestricted | Out-Null
& sc.exe sidtype SilentStreamInspectionBroker unrestricted | Out-Null
foreach ($name in @('SilentStreamInspectionCoordinator','SilentStreamInspectionBroker')) { & sc.exe failure $name 'reset= 86400' 'actions= restart/5000/restart/15000/restart/60000' | Out-Null }

& icacls.exe $dataRoot '/grant' 'NT SERVICE\SilentStreamInspectionCoordinator:(OI)(CI)M' | Out-Null
if (-not [string]::IsNullOrWhiteSpace($cachedPath)) {
  & icacls.exe (Split-Path $cachedPath -Parent) '/grant' "$($identity.Name):(OI)(CI)RX" | Out-Null
  & icacls.exe $cachedPath '/grant' "$($identity.Name):(R,X)" | Out-Null
}
if (Test-Path -LiteralPath (Split-Path $AppConfigPath -Parent)) {
  & icacls.exe (Split-Path $AppConfigPath -Parent) '/grant' 'SYSTEM:(OI)(CI)F' 'NT SERVICE\SilentStreamInspectionCoordinator:(OI)(CI)M' | Out-Null
}

Import-Module ScheduledTasks
$taskName = 'SilentStream Inspection Session Agent'
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
$action = New-ScheduledTaskAction -Execute $sessionExe
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds 0) -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $taskName

Stop-Process -Name cloudflared -Force -ErrorAction SilentlyContinue
& sc.exe start SilentStreamInspectionBroker | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Broker 서비스 시작 실패: $LASTEXITCODE" }
& sc.exe start SilentStreamInspectionCoordinator | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Coordinator 서비스 시작 실패: $LASTEXITCODE" }
foreach ($serviceName in @('SilentStreamInspectionBroker','SilentStreamInspectionCoordinator')) {
  $service = Get-Service -Name $serviceName -ErrorAction Stop
  try { $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(30)) }
  catch { throw "$serviceName 서비스가 30초 안에 실행 상태가 되지 않았습니다." }
}
if (Test-Path -LiteralPath $AppExecutablePath) { Start-Process -FilePath $AppExecutablePath -WindowStyle Hidden }

$resultPath = Join-Path (Split-Path $PublicKeyFile -Parent) '초기_원격점검_연결정보.txt'
$portal = if ($central) { "$($centralBaseUri.TrimEnd('/'))/control/?room=$roomId" } elseif ([string]::IsNullOrWhiteSpace($hostname)) { 'http://127.0.0.1:8790/inspect/' } else { "https://$hostname/inspect/" }
$authLines = if ($central) { '인증: 중앙 포털 Google 로그인' } else { "연결 번호(조작자): $pairingPin`r`n초기 관리자 토큰: $adminToken" }
@"
SilentStream 중앙 원격 점검
관리 대상: M111 / M112 / M113 / 백202 (각 PC 독립 운영)
호실: $room
포털: $portal
$authLines

자동 운행 전제: 이 PC의 Windows 사용자 자동 로그인은 별도로 설정해야 합니다.
전원 → 앱/점검 서비스 업데이트 확인 → startup-v0.4.1 안전 점검 → 원격 결과 순서로 진행됩니다.
M113은 실제 마이크와 스테레오 믹스를 구분합니다. 스테레오 믹스만 있으면 실제 마이크 합격이 아닙니다.
무선 수신기 전원·배터리·페어링·케이블은 소프트웨어가 켜거나 복구할 수 없습니다.
이 연결 정보와 호실 설정을 다른 강의실 PC에 복사하지 마세요.

강의실 PC는 중앙 서버로 아웃바운드 연결만 만들며 외부 인바운드 포트를 열지 않습니다.
관리자는 별도 PIN 없이 Google 계정 권한으로 담당 호실을 확인하고 제어합니다.
"@ | Set-Content -LiteralPath $resultPath -Encoding UTF8
if ($preseed -and -not $central) {
  (Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8).Replace('초기 관리자 토큰: ', '초기 관리자 토큰: 작업실 전용 보관소에 사전 등록됨') | Set-Content -LiteralPath $resultPath -Encoding UTF8
}
& icacls.exe $resultPath '/inheritance:r' "/grant:r" "$($identity.Name):(R,W,D)" | Out-Null
Write-Output "설치 완료(v0.5.0 중앙 연결, $room): $resultPath"
