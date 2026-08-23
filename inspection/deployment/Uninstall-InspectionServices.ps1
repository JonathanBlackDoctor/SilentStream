[CmdletBinding()]param([switch]$RemoveData)
$ErrorActionPreference='Stop'
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '관리자 권한이 필요합니다.'}
Import-Module ScheduledTasks
Unregister-ScheduledTask -TaskName 'SilentStream Inspection Session Agent' -Confirm:$false -ErrorAction SilentlyContinue
foreach($name in @('SilentStreamInspectionCoordinator','SilentStreamInspectionBroker')){& sc.exe stop $name 2>$null|Out-Null;& sc.exe delete $name 2>$null|Out-Null}
Start-Sleep -Seconds 2
$install=Join-Path $env:ProgramFiles 'MediaCaptureHelper\Inspection'
if(Test-Path -LiteralPath $install){Remove-Item -LiteralPath $install -Recurse -Force}
foreach($name in @('InspectionPolicy','InspectionAgentStaging','InspectionUpdater')){$path=Join-Path $env:ProgramFiles "MediaCaptureHelper\$name";if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Recurse -Force}}
if($RemoveData){$data=Join-Path $env:ProgramData 'MediaCaptureHelper\Inspection';if(Test-Path -LiteralPath $data){Remove-Item -LiteralPath $data -Recurse -Force}}
