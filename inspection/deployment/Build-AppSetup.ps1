[CmdletBinding()]param(
  [string]$Version = '0.4.1',
  [string]$Runtime = 'win-x64',
  [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$deploymentRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $PSScriptRoot 'app-release' }
$Output = [IO.Path]::GetFullPath($Output)
if (-not $Output.StartsWith($deploymentRoot,[StringComparison]::OrdinalIgnoreCase)) { throw '앱 출력 경로는 inspection\deployment 내부여야 합니다.' }
$staging = Join-Path $PSScriptRoot 'app-publish'
foreach ($path in @($staging,$Output)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }

& dotnet publish (Join-Path $repo 'src\SilentStream.App\SilentStream.App.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o $staging
if ($LASTEXITCODE -ne 0) { throw '앱 publish 실패' }
& dotnet publish (Join-Path $repo 'src\SilentStream.UninstallHelper\SilentStream.UninstallHelper.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $staging 'uninstall-helper')
if ($LASTEXITCODE -ne 0) { throw '제거 도우미 publish 실패' }

$required = @(
  @{ Source='publish\ffmpeg\ffmpeg.exe'; Destination='ffmpeg\ffmpeg.exe' },
  @{ Source='publish\cloudflared\cloudflared.exe'; Destination='cloudflared\cloudflared.exe' },
  @{ Source='publish\yt-dlp\yt-dlp.exe'; Destination='yt-dlp\yt-dlp.exe' },
  @{ Source='publish\yt-dlp\deno.exe'; Destination='yt-dlp\deno.exe' },
  @{ Source='publish\client_secret.json'; Destination='client_secret.json' },
  @{ Source='docs\THIRD_PARTY_NOTICES.md'; Destination='THIRD_PARTY_NOTICES.md' }
)
foreach ($item in $required) {
  $source = Join-Path $repo $item.Source
  if (-not (Test-Path -LiteralPath $source)) { throw "필수 배포 자산 누락: $($item.Source)" }
  $destination = Join-Path $staging $item.Destination
  New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
  Copy-Item -LiteralPath $source -Destination $destination -Force
}
foreach ($license in @('THIRD_PARTY_LICENSES.txt','DENO_LICENSE.md')) {
  $source = Join-Path $repo "publish\yt-dlp\$license"
  if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $staging "yt-dlp\$license") -Force }
}
if (Test-Path -LiteralPath (Join-Path $repo 'publish\provisioning.json')) { Copy-Item -LiteralPath (Join-Path $repo 'publish\provisioning.json') -Destination $staging -Force }
$agentUpdates = Join-Path $PSScriptRoot 'app-agent-update'
if (Test-Path -LiteralPath $agentUpdates) {
  $target = Join-Path $staging 'inspection-update';New-Item -ItemType Directory -Path $target -Force | Out-Null
  Get-ChildItem -LiteralPath $agentUpdates -Filter '*.zip' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target -Force }
}

& vpk pack --packId MediaCaptureHelper --packVersion $Version --packDir $staging --mainExe MediaCaptureHelper.exe --packTitle 'Media Capture Helper' --packAuthors 'Media Capture Helper' --delta None --outputDir $Output
if ($LASTEXITCODE -ne 0) { throw 'Velopack Setup 생성 실패' }
$setup = Get-ChildItem -LiteralPath $Output -Filter '*Setup*.exe' | Select-Object -First 1
if (-not $setup) { throw 'Velopack Setup 파일을 찾지 못했습니다.' }
Write-Output $setup.FullName
