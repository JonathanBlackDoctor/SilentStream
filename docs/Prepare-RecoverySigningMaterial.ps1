[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$Dotnet,
  [Parameter(Mandatory=$true)][string]$CliDll,
  [Parameter(Mandatory=$true)][string]$BundleSource,
  [Parameter(Mandatory=$true)][string]$PublicOutput,
  [Parameter(Mandatory=$true)][string]$PrivateVault,
  [string]$Version = '0.4.8',
  [string]$KeyId = 'silentstream-ops-20261017'
)
$ErrorActionPreference = 'Stop'
$publicPath = [IO.Path]::GetFullPath($PublicOutput)
$vaultPath = [IO.Path]::GetFullPath($PrivateVault)
if ($vaultPath.StartsWith($publicPath.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Private vault must be outside public material.' }
if (Test-Path -LiteralPath $vaultPath) { throw 'Private vault already exists; preserve the existing signing material.' }
New-Item -ItemType Directory -Path $publicPath -Force | Out-Null
$keyFile = Join-Path $publicPath "$KeyId.public-key.json"
& $Dotnet $CliDll key init $KeyId $keyFile
if ($LASTEXITCODE -ne 0) { throw 'CNG key creation/public export failed.' }
$bundleFile = Join-Path (Split-Path $vaultPath -Parent) "startup-$Version.zip"
& $Dotnet $CliDll bundle sign $BundleSource 'startup' $Version $Version $KeyId $bundleFile
if ($LASTEXITCODE -ne 0) { throw 'Startup bundle signing failed.' }
& $Dotnet $CliDll bundle verify-startup $bundleFile $keyFile (Join-Path (Split-Path $vaultPath -Parent) 'verified-startup')
if ($LASTEXITCODE -ne 0) { throw 'Startup signature/safety verification failed.' }
[IO.File]::WriteAllText((Join-Path $publicPath 'startup.zip.b64'),[Convert]::ToBase64String([IO.File]::ReadAllBytes($bundleFile))+"`n")
$token = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_')
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($token))).ToLowerInvariant()
$preseed = [ordered]@{schemaVersion=1;signingKeyId=$KeyId;tokens=@(@{hash=$hash;role='admin';label='deployment operator'});bundles=@("preseed-bundles/startup-$Version.zip");agentUpdates=@()}
[IO.File]::WriteAllText((Join-Path $publicPath 'deployment-preseed.json'),($preseed | ConvertTo-Json -Depth 8)+"`n")
New-Item -ItemType Directory -Path (Split-Path $vaultPath -Parent) -Force | Out-Null
[IO.File]::WriteAllText($vaultPath,'')
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [Security.AccessControl.FileSecurity]::new()
$acl.SetAccessRuleProtection($true,$false)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow))
[IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($vaultPath),$acl)
[IO.File]::WriteAllText($vaultPath,(@{keyId=$KeyId;adminToken=$token;createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O');note='Private operator material; never publish.'} | ConvertTo-Json)+"`n")
Write-Output "Verified public signing material: $publicPath"
Write-Output 'Private operator vault saved with current-user-only ACL. Private signing key remains non-exportable in CNG.'
