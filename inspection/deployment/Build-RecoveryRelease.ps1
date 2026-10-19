[CmdletBinding()]
param([string]$Version='0.4.8', [string]$SigningMaterialVersion=$Version)
$ErrorActionPreference='Stop'
if ($Version -notin @('0.4.8','0.4.9','0.4.10','0.4.11','0.4.12','0.4.13')) { throw 'Use reviewed signing material for each immutable release.' }
if ($SigningMaterialVersion -ne $Version -and -not ($Version -eq '0.4.13' -and $SigningMaterialVersion -eq '0.4.12')) { throw 'Unreviewed signing material reuse.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Set-Location -LiteralPath $repo
$package = Join-Path $repo 'recovery-package'
if (Test-Path -LiteralPath $package) { throw 'Recovery staging already exists; do not mix build outputs.' }
New-Item -ItemType Directory -Path (Join-Path $package 'bin') | Out-Null
$projects = [ordered]@{
  coordinator='src/SilentStream.Inspection.Coordinator/SilentStream.Inspection.Coordinator.csproj'
  broker='src/SilentStream.Inspection.Broker/SilentStream.Inspection.Broker.csproj'
  session='src/SilentStream.Inspection.SessionAgent/SilentStream.Inspection.SessionAgent.csproj'
  cli='src/SilentStream.Inspection.OperatorCli/SilentStream.Inspection.OperatorCli.csproj'
  installer='src/SilentStream.Inspection.Installer/SilentStream.Inspection.Installer.csproj'
}
foreach ($name in $projects.Keys) {
  $single = if ($name -in @('broker','installer')) { 'true' } else { 'false' }
  dotnet publish $projects[$name] -c Release -r win-x64 --self-contained true "-p:Version=$Version" "-p:PublishSingleFile=$single" -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "recovery-package/bin/$name"
  if ($LASTEXITCODE -ne 0) { throw "Inspection publish failed: $name" }
}
Copy-Item -LiteralPath 'publish/cloudflared' -Destination (Join-Path $package 'bin/cloudflared') -Recurse
foreach ($script in @('Install-InspectionServices.ps1','Uninstall-InspectionServices.ps1')) {
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $package
}
$materials = Join-Path $repo "inspection/releases/$SigningMaterialVersion"
$preseedFile = Join-Path $materials 'deployment-preseed.json'
$preseed = Get-Content -LiteralPath $preseedFile -Raw | ConvertFrom-Json
if ($preseed.schemaVersion -ne 1 -or $preseed.signingKeyId -ne 'silentstream-ops-20261017' -or @($preseed.agentUpdates).Count -ne 0) { throw 'Unexpected recovery trust material.' }
$publicKey = Join-Path $materials ($preseed.signingKeyId+'.public-key.json')
Copy-Item -LiteralPath $publicKey -Destination $package
Copy-Item -LiteralPath $preseedFile -Destination $package
New-Item -ItemType Directory -Path (Join-Path $package 'preseed-bundles') | Out-Null
$bundle = Join-Path $package "preseed-bundles/startup-$SigningMaterialVersion.zip"
[IO.File]::WriteAllBytes($bundle,[Convert]::FromBase64String([IO.File]::ReadAllText((Join-Path $materials 'startup.zip.b64')).Trim()))
$cli = Join-Path $package 'bin/cli/SilentStreamInspectionCli.exe'
$verified = Join-Path $repo 'verified-recovery-startup'
& $cli bundle verify-startup $bundle $publicKey $verified
if ($LASTEXITCODE -ne 0) { throw 'Signed startup safety verification failed.' }
$expectedPlan = Join-Path $repo "inspection/bundles/startup-v$SigningMaterialVersion/plan.json"
$verifiedPlans = @(Get-ChildItem -LiteralPath $verified -Filter 'plan.json' -Recurse -File)
if ($verifiedPlans.Count -ne 1) { throw 'Verified plan must be unique inside its bundle directory.' }
$signedPlanJson = Get-Content -LiteralPath $verifiedPlans[0].FullName -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 40 -Compress
$sourcePlanJson = Get-Content -LiteralPath $expectedPlan -Raw | ConvertFrom-Json | ConvertTo-Json -Depth 40 -Compress
if (-not [string]::Equals($signedPlanJson,$sourcePlanJson,[StringComparison]::Ordinal)) { throw 'Signed plan differs from reviewed source.' }
$setup = @(Get-ChildItem -LiteralPath 'Releases' -Filter '*Setup*.exe')
if ($setup.Count -ne 1) { throw 'Expected exactly one app Setup.' }
Copy-Item -LiteralPath $setup[0].FullName -Destination $package
$installerName = "SilentStream-Central-Installer-v$Version.exe"
$installer = Join-Path $package 'bin/installer/SilentStreamInspectionInstaller.exe'
Copy-Item -LiteralPath $installer -Destination (Join-Path $repo $installerName)
Copy-Item -LiteralPath $installer -Destination (Join-Path $package '원격점검_설치.exe')
$archiveName = "SilentStream-prepared-package-v$Version.zip"
Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $repo $archiveName) -CompressionLevel Optimal
$assets = foreach ($file in @($archiveName,$installerName)) {
  $item = Get-Item -LiteralPath $file
  [ordered]@{name=$item.Name;length=$item.Length;sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
[ordered]@{version=$Version;signingMaterialVersion=$SigningMaterialVersion;signingKeyId=$preseed.signingKeyId;startupSignatureVerified=$true;assets=@($assets)} |
  ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "recovery-metadata-$Version.json" -Encoding utf8
Write-Output "Prepared and verified recovery package $archiveName"
