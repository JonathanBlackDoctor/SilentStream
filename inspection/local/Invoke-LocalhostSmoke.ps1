[CmdletBinding()]
param(
  [switch]$NoBuild,
  [string]$ReportPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repo 'src\SilentStream.Inspection.Coordinator\SilentStream.Inspection.Coordinator.csproj'
$assembly = Join-Path $repo 'src\SilentStream.Inspection.Coordinator\bin\Release\net8.0-windows\SilentStreamInspectionCoordinator.dll'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }

if (-not $NoBuild) {
  & $dotnet build $project -c Release --no-restore
  if ($LASTEXITCODE -ne 0) { throw 'Coordinator build failed.' }
}
if (-not (Test-Path -LiteralPath $assembly)) { throw "Coordinator assembly is missing: $assembly" }

function New-RandomToken {
  $bytes = New-Object byte[] 32
  $generator=[Security.Cryptography.RandomNumberGenerator]::Create()
  try{$generator.GetBytes($bytes)}finally{$generator.Dispose()}
  return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
}
function Get-TokenHash([string]$Value) {
  $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
  $sha=[Security.Cryptography.SHA256]::Create()
  try{return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}
function Assert-Equal($Actual,$Expected,[string]$Label) {
  if ($Actual -ne $Expected) { throw "$Label expected '$Expected' but received '$Actual'." }
}
function Assert-True([bool]$Value,[string]$Label) {
  if (-not $Value) { throw "$Label was not true." }
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start();$port=([Net.IPEndPoint]$listener.LocalEndpoint).Port;$listener.Stop()
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("SilentStreamInspectionSmoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
$adminToken = New-RandomToken
$config = [ordered]@{
  schemaVersion=1;port=$port;bindAddress='127.0.0.1';room='LOCAL-SMOKE';pairingPin='246810'
  tokens=@([ordered]@{hash=(Get-TokenHash $adminToken);role='admin';label='localhost-smoke-admin';createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O')})
  publicKeys=[ordered]@{};cloudflareTunnelTokenMachineEnc='';cloudflareHostname='';cloudflaredPath='';cloudflareProtocol='http2'
  brokerSecretMachineEnc='';appApiTokenMachineEnc='';appExecutablePath='';appConfigPath='';cachedInstallerPath='';cachedInstallerSha256=''
}
$config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $tempRoot 'coordinator.json') -Encoding UTF8

$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName=$dotnet
$start.Arguments='"'+$assembly.Replace('"','\"')+'" --console --inspection-root="'+$tempRoot.Replace('"','\"')+'"'
$start.WorkingDirectory=Split-Path $assembly -Parent
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$process = [Diagnostics.Process]::Start($start)
$stdoutTask=$process.StandardOutput.ReadToEndAsync();$stderrTask=$process.StandardError.ReadToEndAsync()
$client = [Net.Http.HttpClient]::new();$client.Timeout=[TimeSpan]::FromSeconds(5)
$base = "http://127.0.0.1:$port"
$checks = [ordered]@{}

function Send-Request([string]$Method,[string]$Path,[string]$Token='',$Body=$null,[string]$Origin='') {
  $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),$base+$Path)
  if (-not [string]::IsNullOrWhiteSpace($Token)) { $request.Headers.Authorization=[Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$Token) }
  if (-not [string]::IsNullOrWhiteSpace($Origin)) { $request.Headers.TryAddWithoutValidation('Origin',$Origin)|Out-Null }
  if ($null -ne $Body) {
    $json=$Body|ConvertTo-Json -Depth 20 -Compress
    $request.Content=[Net.Http.StringContent]::new($json,[Text.Encoding]::UTF8,'application/json')
  }
  try {
    $response=$client.SendAsync($request).GetAwaiter().GetResult()
    $text=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return [pscustomobject]@{Status=[int]$response.StatusCode;Body=$text;Headers=$response.Headers;ContentHeaders=$response.Content.Headers}
  } catch {
    throw "$Method $Path request failed: $($_.Exception.Message)"
  } finally { $request.Dispose() }
}

try {
  $health=$null
  for($attempt=0;$attempt-lt 40;$attempt++){
    if($process.HasExited){throw "Coordinator exited early: $($stderrTask.Result)"}
    try{$health=Send-Request GET '/inspect/api/v1/health';if($health.Status-eq 200){break}}catch{}
    Start-Sleep -Milliseconds 250
  }
  if($null-eq $health){throw 'Coordinator health endpoint did not become ready.'}
  Assert-Equal $health.Status 200 'health status';$checks.health=200
  Assert-Equal ($health.Headers.GetValues('X-Frame-Options')|Select-Object -First 1) 'DENY' 'frame protection'
  Assert-True (($health.Headers.GetValues('Content-Security-Policy')|Select-Object -First 1)-match "frame-ancestors 'none'") 'content security policy'
  Assert-Equal ($health.Headers.CacheControl.ToString()) 'no-store' 'inspection cache policy';$checks.securityHeaders=$true

  $unauthorized=Send-Request GET '/inspect/api/v1/me';Assert-Equal $unauthorized.Status 401 'unauthenticated API';$checks.unauthenticatedRejected=$true
  $wrongPair=Send-Request POST '/inspect/api/v1/pair' '' @{pin='000000';deviceName='wrong'};Assert-Equal $wrongPair.Status 401 'wrong PIN';$checks.wrongPinRejected=$true
  $pair=Send-Request POST '/inspect/api/v1/pair' '' @{pin='246810';deviceName='local-smoke'};Assert-Equal $pair.Status 200 'correct PIN'
  $operatorToken=($pair.Body|ConvertFrom-Json).token;Assert-True (-not [string]::IsNullOrWhiteSpace($operatorToken)) 'operator token issued';$checks.operatorPaired=$true

  $operatorMe=Send-Request GET '/inspect/api/v1/me' $operatorToken;Assert-Equal $operatorMe.Status 200 'operator identity';Assert-Equal (($operatorMe.Body|ConvertFrom-Json).role) 'operator' 'operator role'
  $operatorTokens=Send-Request GET '/inspect/api/v1/tokens' $operatorToken;Assert-Equal $operatorTokens.Status 403 'operator token listing';$checks.roleBoundary=$true
  $adminMe=Send-Request GET '/inspect/api/v1/me' $adminToken;Assert-Equal $adminMe.Status 200 'admin identity';Assert-Equal (($adminMe.Body|ConvertFrom-Json).role) 'admin' 'admin role'
  $evilOrigin=Send-Request GET '/inspect/api/v1/me' $adminToken $null 'https://evil.example';Assert-Equal $evilOrigin.Status 403 'cross-origin request';$checks.crossOriginRejected=$true
  $wrongPortOrigin=Send-Request GET '/inspect/api/v1/me' $adminToken $null ("http://127.0.0.1:"+($port+1));Assert-Equal $wrongPortOrigin.Status 403 'different-port origin';$checks.differentPortOriginRejected=$true

  $tokens=Send-Request GET '/inspect/api/v1/tokens' $adminToken;Assert-Equal $tokens.Status 200 'admin token listing'
  $operatorHash=Get-TokenHash $operatorToken;$revoke=Send-Request DELETE ("/inspect/api/v1/tokens/"+$operatorHash.Substring(0,12)) $adminToken
  Assert-Equal $revoke.Status 204 'operator revocation';$revoked=Send-Request GET '/inspect/api/v1/me' $operatorToken;Assert-Equal $revoked.Status 401 'revoked operator';$checks.revocationImmediate=$true

  $checks.completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
  if([string]::IsNullOrWhiteSpace($ReportPath)){
    $reportDirectory=Join-Path $repo 'inspection\reports';New-Item -ItemType Directory -Path $reportDirectory -Force|Out-Null
    $ReportPath=Join-Path $reportDirectory ("localhost-smoke-"+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')+'.json')
  }
  [ordered]@{passed=$true;port=$port;checks=$checks}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $ReportPath -Encoding UTF8
  Write-Output (Resolve-Path -LiteralPath $ReportPath).Path
}
finally {
  $client.Dispose()
  if($null-ne $process){try{if(-not $process.HasExited){$process.Kill();$process.WaitForExit(5000)|Out-Null};$null=$stdoutTask.Result;$null=$stderrTask.Result}catch{};$process.Dispose()}
  try{Remove-Item -LiteralPath $tempRoot -Recurse -Force}catch{}
}
