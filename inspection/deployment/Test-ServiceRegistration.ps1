# Tests the real registration helper with a fake CIM boundary; no SCM changes are made.
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-InspectionServices.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Register-WindowsService' }, $true)
. ([scriptblock]::Create($definition.Extent.Text))
$script:service = $null
$script:returnValue = 0
$script:methods = @()
function Get-CimInstance { param($ClassName,$Filter) $script:service }
function Invoke-CimMethod { param($ClassName,$InputObject,$MethodName,$Arguments)
  $script:methods += $MethodName
  if ($Arguments.PathName -ne ('"' + $testExecutable + '"')) { throw 'Quoted path was not preserved' }
  if ($Arguments.StartName -ne 'NT AUTHORITY\LocalService') { throw 'Service account changed' }
  if ($Arguments.StartMode -ne 'Automatic') { throw 'Automatic startup missing' }
  if ($MethodName -eq 'Create' -and ($Arguments.ServiceType -ne 16 -or $Arguments.ErrorControl -ne 1)) { throw 'Invalid service type' }
  $script:service = [pscustomobject]@{PathName=$Arguments.PathName;StartMode='Auto'}
  [pscustomobject]@{ReturnValue=$script:returnValue}
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('SilentStream-ServiceTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
  $testExecutable = Join-Path $testRoot '한글 공백 서비스.exe'
  [IO.File]::WriteAllText($testExecutable,'never executed')
  Register-WindowsService -Name 'TestOnly' -BinaryPath $testExecutable -DisplayName 'Test service' -Account 'NT AUTHORITY\LocalService'
  Register-WindowsService -Name 'TestOnly' -BinaryPath $testExecutable -DisplayName 'Test service' -Account 'NT AUTHORITY\LocalService'
  if (($script:methods -join ',') -ne 'Create,Change') { throw 'Reinstall did not update the existing service' }
  $script:returnValue = 21
  $failed = $false
  try { Register-WindowsService -Name 'TestOnly' -BinaryPath $testExecutable -DisplayName 'Test service' -Account 'NT AUTHORITY\LocalService' }
  catch { if ($_.Exception.Message -notmatch '21') { throw }; $failed = $true }
  if (-not $failed) { throw 'Native error was ignored' }
  Write-Output 'PASS: quoted Korean/space path, LocalService, create, repeated change, native failure propagation'
} finally {
  $resolved = [IO.Path]::GetFullPath($testRoot)
  if ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('SilentStream-ServiceTest-')) {
    Remove-Item -LiteralPath $resolved -Recurse -Force
  }
}
