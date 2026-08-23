[CmdletBinding()]param([string]$Configuration='Release',[string]$Runtime='win-x64',[string]$Output='')
$ErrorActionPreference='Stop';$repo=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if([string]::IsNullOrWhiteSpace($Output)){$Output=Join-Path $PSScriptRoot 'package'}
$deploymentRoot=[IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')+'\';$Output=[IO.Path]::GetFullPath($Output)
if(-not $Output.StartsWith($deploymentRoot,[StringComparison]::OrdinalIgnoreCase)){throw '패키지 출력 경로는 inspection\deployment 내부여야 합니다.'}
if(Test-Path -LiteralPath $Output){Remove-Item -LiteralPath $Output -Recurse -Force};New-Item -ItemType Directory -Path (Join-Path $Output 'bin') -Force|Out-Null
$projects=[ordered]@{coordinator='src\SilentStream.Inspection.Coordinator\SilentStream.Inspection.Coordinator.csproj';broker='src\SilentStream.Inspection.Broker\SilentStream.Inspection.Broker.csproj';session='src\SilentStream.Inspection.SessionAgent\SilentStream.Inspection.SessionAgent.csproj';cli='src\SilentStream.Inspection.OperatorCli\SilentStream.Inspection.OperatorCli.csproj';installer='src\SilentStream.Inspection.Installer\SilentStream.Inspection.Installer.csproj'}
foreach($name in $projects.Keys){$singleFile=if($name -in @('installer','broker')){'true'}else{'false'};& dotnet publish (Join-Path $repo $projects[$name]) -c $Configuration -r $Runtime --self-contained true "-p:PublishSingleFile=$singleFile" -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $Output "bin\$name");if($LASTEXITCODE -ne 0){throw "publish 실패: $name"}}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-InspectionServices.ps1') -Destination $Output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-InspectionServices.ps1') -Destination $Output
Copy-Item -LiteralPath (Join-Path $repo 'inspection\bundles') -Destination $Output -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'inspection\README.md') -Destination $Output
Copy-Item -LiteralPath (Join-Path $Output 'bin\installer\SilentStreamInspectionInstaller.exe') -Destination (Join-Path $Output '원격점검_설치.exe')
Remove-Item -LiteralPath (Join-Path $Output 'bin\installer') -Recurse -Force
$cloudflared=Join-Path $repo 'publish\cloudflared\cloudflared.exe'
if(Test-Path -LiteralPath $cloudflared){$target=Join-Path $Output 'bin\cloudflared';New-Item -ItemType Directory -Path $target -Force|Out-Null;Copy-Item -LiteralPath $cloudflared -Destination (Join-Path $target 'cloudflared.exe')}
$appSetup=Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'app-release') -Filter '*Setup*.exe' -ErrorAction SilentlyContinue|Select-Object -First 1
if($appSetup){Copy-Item -LiteralPath $appSetup.FullName -Destination $Output -Force}
$zip=Join-Path (Split-Path $Output -Parent) '원격우선_실기기점검_v0.4.0.zip'
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip -Force};Compress-Archive -Path (Join-Path $Output '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Output $zip
