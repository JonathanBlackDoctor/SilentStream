[CmdletBinding()]
param(
  [string]$TokenCsv = '',
  [string]$OutputDirectory = '',
  [switch]$WebSafe
)

$ErrorActionPreference = 'Stop'
$version = '0.4.1'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($TokenCsv)) { $TokenCsv = Join-Path $repo '..\터널토큰목록.csv' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $folder = if ($WebSafe) { 'baek202-web-v0.4.1' } else { 'baek202-v0.4.1' }
  $OutputDirectory = Join-Path $repo "artifacts\$folder"
}
$package = Join-Path $PSScriptRoot 'package'
$assets = Join-Path $PSScriptRoot 'field-pack'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path -LiteralPath (Join-Path $package '원격점검_설치.exe'))) { throw 'Build-InspectionPackage.ps1을 먼저 실행하세요.' }
$setup = Get-ChildItem -LiteralPath $package -Filter '*Setup*.exe' | Select-Object -First 1
if (-not $setup -or [string]$setup.VersionInfo.ProductVersion -ne $version) { throw "v$version 앱 Setup이 패키지에 없습니다." }
$tunnelToken = ''
if (-not $WebSafe) {
  if (-not (Test-Path -LiteralPath $TokenCsv)) { throw "터널 토큰 CSV를 찾을 수 없습니다: $TokenCsv" }
  $row = Import-Csv -LiteralPath $TokenCsv -Encoding UTF8 | Where-Object {
    ([string]$_.'방').ToLowerInvariant() -eq 'baek202' -or ([string]$_.Hostname).ToLowerInvariant() -eq 'baek202.silentstream.win'
  } | Select-Object -First 1
  if (-not $row -or [string]::IsNullOrWhiteSpace([string]$row.Token)) { throw 'CSV에 백202 터널 토큰이 없습니다.' }
  if ([string]$row.Hostname -ne 'baek202.silentstream.win') { throw "백202 호스트명이 다릅니다: $($row.Hostname)" }
  $tunnelToken = [string]$row.Token
}

Copy-Item -LiteralPath (Join-Path $assets '백202_업데이트_점검_설치.cmd') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $assets '백202_업데이트_점검_설치.ps1') -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $assets 'README_백202.md') -Destination $package -Force
$preset = [ordered]@{
  schemaVersion = 1
  version = $version
  roomId = 'baek202'
  displayName = '백202'
  hostname = 'baek202.silentstream.win'
  cloudflareTunnelToken = $tunnelToken
  installerFile = $setup.Name
  installerSha256 = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $package 'baek202.app-preseed.json'),($preset | ConvertTo-Json -Depth 8),$utf8)

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$baseName = if ($WebSafe) { "백202_웹다운로드용_업데이트_점검팩_v$version" } else { "백202_업데이트_점검팩_v$version" }
$prepared = Join-Path $OutputDirectory "$baseName.zip"
$vaultName = if ($WebSafe) { "baek202-web-v$version.json" } else { "baek202-v$version.json" }
$vault = Join-Path $repo "inspection\operator-vault\$vaultName"
$cli = Join-Path $package 'bin\cli\SilentStreamInspectionCli.exe'
if (-not (Test-Path -LiteralPath $cli)) { throw '점검 Operator CLI가 패키지에 없습니다.' }
& $cli deployment prepare $package (Join-Path $repo "inspection\bundles\startup-v$version") 'silentstream-startup' $version $version 'silentstream-ops-2026' $prepared $vault
if ($LASTEXITCODE -ne 0) { throw '백202 서명 준비 팩 생성에 실패했습니다.' }
$hash = (Get-FileHash -LiteralPath $prepared -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory "$baseName.sha256.txt"),"$hash  $([IO.Path]::GetFileName($prepared))`r`n",$utf8)
Write-Output $prepared
