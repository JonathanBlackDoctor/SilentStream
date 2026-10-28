[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$Dotnet,
  [Parameter(Mandatory=$true)][string]$CloudflaredDirectory,
  [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$version='0.4.22'
$keyId='silentstream-ops-20261017'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not [Security.Cryptography.CngKey]::Exists("SilentStream.Inspection.Signing.$keyId")) {
  throw 'Existing trusted signing key is required. Do not create a replacement key.'
}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output already exists; preserve the previous signing result.' }
New-Item -ItemType Directory -Path $output | Out-Null
$payload=Join-Path $output 'payload'
$projects=[ordered]@{
 coordinator='SilentStream.Inspection.Coordinator'
 broker='SilentStream.Inspection.Broker'
 session='SilentStream.Inspection.SessionAgent'
 cli='SilentStream.Inspection.OperatorCli'
}
foreach($component in $projects.Keys){
  $project=$projects[$component]
  $single=if($component -eq 'coordinator'){'false'}else{'true'}
  & $Dotnet publish (Join-Path $repo "src/$project/$project.csproj") -c Release -r win-x64 --self-contained true "-p:Version=$version" "-p:PublishSingleFile=$single" -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $payload $component) *> (Join-Path $output "publish-$component.log")
  if($LASTEXITCODE -ne 0){throw "Publish failed: $component; see its bounded build log."}
}
Copy-Item -LiteralPath $CloudflaredDirectory -Destination (Join-Path $payload 'cloudflared') -Recurse
$cli=Join-Path $payload 'cli/SilentStreamInspectionCli.exe'
$key=Join-Path $repo 'inspection/releases/0.4.19/silentstream-ops-20261017.public-key.json'
$archive=Join-Path $output "inspection-agent-$version.zip"
& $cli agent-update sign $payload $version $keyId $archive
if($LASTEXITCODE -ne 0){throw 'Helper signing failed.'}
& $cli agent-update verify $archive $key (Join-Path $output 'verified-helper')
if($LASTEXITCODE -ne 0){throw 'Existing-key signature verification failed.'}
$materials=Join-Path $repo "inspection/releases/$version"
if(Test-Path -LiteralPath $materials){throw 'Release signing material already exists; preserve it.'}
New-Item -ItemType Directory -Path $materials | Out-Null
$bundle=Join-Path $output "startup-$version.zip"
& $cli bundle sign (Join-Path $repo "inspection/bundles/startup-v$version") 'startup' $version $version $keyId $bundle
if($LASTEXITCODE -ne 0){throw 'Startup signing failed.'}
& $cli bundle verify-startup $bundle $key (Join-Path $output 'verified-startup')
if($LASTEXITCODE -ne 0){throw 'Startup safety/signature verification failed.'}
Copy-Item -LiteralPath $key -Destination $materials
$preseed=[IO.File]::ReadAllText((Join-Path $repo 'inspection/releases/0.4.19/deployment-preseed.json')).Replace('0.4.19',$version)
[IO.File]::WriteAllText((Join-Path $materials 'deployment-preseed.json'),$preseed)
[IO.File]::WriteAllText((Join-Path $materials 'startup.zip.b64'),[Convert]::ToBase64String([IO.File]::ReadAllBytes($bundle)) + [Environment]::NewLine)
$metadata=[ordered]@{version=$version;keyId=$keyId;helperVerified=$true;startupVerified=$true;length=(Get-Item -LiteralPath $archive).Length;sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'signed-helper.json') -Encoding utf8
$metadata | ConvertTo-Json -Compress
