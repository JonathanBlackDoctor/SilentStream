[CmdletBinding()]param([string]$Configuration='Release',[string]$Runtime='win-x64',[string]$Output='')
$ErrorActionPreference='Stop';$packageVersion='0.4.1';$repo=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if([string]::IsNullOrWhiteSpace($Output)){$Output=Join-Path $PSScriptRoot 'package'}
$deploymentRoot=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')+'\';$Output=[IO.Path]::GetFullPath($Output)
if(-not $Output.StartsWith($deploymentRoot,[StringComparison]::OrdinalIgnoreCase)){throw '패키지 출력 경로는 inspection\deployment 내부여야 합니다.'}
$startupBundle=Join-Path $repo "inspection\bundles\startup-v$packageVersion"
if(-not (Test-Path -LiteralPath (Join-Path $startupBundle 'plan.json'))){throw "부팅 안전 점검 번들 누락: $startupBundle"}
Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*v0.4.0.zip' -ErrorAction SilentlyContinue | ForEach-Object {
  Write-Warning "오래된 v0.4.0 산출물은 startup-v$packageVersion 및 현재 바이너리를 포함하지 않습니다. 배포하지 말고 다시 생성하세요: $($_.Name)"
}
$appSetup=Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'app-release') -Filter '*Setup*.exe' -ErrorAction SilentlyContinue|Select-Object -First 1
if(-not $appSetup){throw 'v0.4.1 앱 Setup이 없습니다. Build-AppSetup.ps1을 먼저 실행하세요.'}
$appSetupVersion=[string]$appSetup.VersionInfo.ProductVersion
if($appSetupVersion -ne $packageVersion){throw "앱 Setup 버전이 $appSetupVersion 입니다. 오래된 Setup을 배포하지 말고 v$packageVersion 앱 Setup을 다시 생성하세요."}
if(Test-Path -LiteralPath $Output){Remove-Item -LiteralPath $Output -Recurse -Force};New-Item -ItemType Directory -Path (Join-Path $Output 'bin') -Force|Out-Null
$projects=[ordered]@{coordinator='src\SilentStream.Inspection.Coordinator\SilentStream.Inspection.Coordinator.csproj';broker='src\SilentStream.Inspection.Broker\SilentStream.Inspection.Broker.csproj';session='src\SilentStream.Inspection.SessionAgent\SilentStream.Inspection.SessionAgent.csproj';cli='src\SilentStream.Inspection.OperatorCli\SilentStream.Inspection.OperatorCli.csproj';installer='src\SilentStream.Inspection.Installer\SilentStream.Inspection.Installer.csproj'}
foreach($name in $projects.Keys){$singleFile=if($name -in @('installer','broker')){'true'}else{'false'};& dotnet publish (Join-Path $repo $projects[$name]) -c $Configuration -r $Runtime --self-contained true "-p:Version=$packageVersion" "-p:PublishSingleFile=$singleFile" -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $Output "bin\$name");if($LASTEXITCODE -ne 0){throw "publish 실패: $name"}}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-InspectionServices.ps1') -Destination $Output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-InspectionServices.ps1') -Destination $Output
$bundleTarget=Join-Path $Output 'bundles';New-Item -ItemType Directory -Path $bundleTarget -Force|Out-Null
Copy-Item -LiteralPath $startupBundle -Destination (Join-Path $bundleTarget (Split-Path $startupBundle -Leaf)) -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'inspection\README.md') -Destination $Output
Copy-Item -LiteralPath (Join-Path $Output 'bin\installer\SilentStreamInspectionInstaller.exe') -Destination (Join-Path $Output '원격점검_설치.exe')
Remove-Item -LiteralPath (Join-Path $Output 'bin\installer') -Recurse -Force
$cloudflared=Join-Path $repo 'publish\cloudflared\cloudflared.exe'
if(Test-Path -LiteralPath $cloudflared){$target=Join-Path $Output 'bin\cloudflared';New-Item -ItemType Directory -Path $target -Force|Out-Null;Copy-Item -LiteralPath $cloudflared -Destination (Join-Path $target 'cloudflared.exe')}
Copy-Item -LiteralPath $appSetup.FullName -Destination $Output -Force
$zip=Join-Path (Split-Path $Output -Parent) "원격점검_서명전_기본패키지_v$packageVersion.zip"
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip -Force};Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Warning '이 ZIP은 deployment prepare 입력용 서명 전 기본 패키지입니다. deployment-preseed.json이 포함된 준비 완료 ZIP을 만든 뒤에만 강의실 PC에 설치하세요.'
Write-Output $zip
