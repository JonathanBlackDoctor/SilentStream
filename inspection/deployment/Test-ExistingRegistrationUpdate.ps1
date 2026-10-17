# Execute production preservation and idle gates with fixtures; no service/credential mutations.
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-InspectionServices.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors | Out-String)}
$blocks=$ast.FindAll({param($node) $node -is [Management.Automation.Language.IfStatementAst]},$true)
$preserve=@($blocks | Where-Object {$_.Extent.Text.StartsWith('if ($PreserveExistingRegistration)') -and $_.Extent.Text.Contains('$preserved =')})
$gate=@($blocks | Where-Object {$_.Extent.Text.StartsWith('if ($PreserveExistingRegistration)') -and $_.Extent.Text.Contains('$liveStatus =')})
if($preserve.Count -ne 1 -or $gate.Count -ne 1){throw 'Production blocks not uniquely identified'}
$PreserveExistingRegistration=$true
$previousRegistration=[pscustomobject]@{
 centralControlEnabled=$true;roomId='m112';centralDeviceId='existing';centralInstallationId='installation'
 centralDeviceSecretMachineEnc='fixture-ciphertext';tokens=@(@{hash='fixture';role='operator'})
 appExecutablePath='fixture.exe';appConfigPath='fixture.json';startupInspectionEnabled=$false
 publicKeys=@{old='old'};brokerSecretMachineEnc='old';appApiTokenMachineEnc='old'
}
$config=[ordered]@{publicKeys=@{new='new'};brokerSecretMachineEnc='new-broker';appApiTokenMachineEnc='new-app';tokens=@(@{role='admin'})}
$cachedPath='';$cachedHash=''
. ([scriptblock]::Create($preserve[0].Extent.Text))
foreach($name in @('roomId','centralDeviceId','centralInstallationId','centralDeviceSecretMachineEnc','tokens','appExecutablePath','appConfigPath','startupInspectionEnabled')){
 if(($config[$name]|ConvertTo-Json -Depth 10 -Compress) -ne ($previousRegistration.$name|ConvertTo-Json -Depth 10 -Compress)){throw "Identity/policy changed: $name"}
}
if($config.brokerSecretMachineEnc -ne 'new-broker' -or $config.appApiTokenMachineEnc -ne 'new-app' -or -not $config.publicKeys.ContainsKey('new')){throw 'New trust/bridge not installed'}
function Invoke-RestMethod {param($Uri,$Headers,$TimeoutSec) [pscustomobject]@{state=$script:fixtureState}}
$previousBridgeToken='fixture'
foreach($state in @('Live','RecordingOnly','Warmup','Stopping',$null)){
 $script:fixtureState=$state;$rejected=$false
 try{. ([scriptblock]::Create($gate[0].Extent.Text))}catch{$rejected=$true}
 if(-not $rejected){throw "Unsafe state accepted: $state"}
}
$script:fixtureState='Idle'
. ([scriptblock]::Create($gate[0].Extent.Text))
# Rejection must happen outside the rollback handler, which would itself stop running processes.
if($gate[0].Parent.Parent -is [Management.Automation.Language.TryStatementAst]){throw 'Idle rejection would invoke rollback'}
Write-Output 'Existing device identity/policy preserved; trust refreshed; 6 idle gate cases passed.'
