$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($env:SILENTSTREAM_APP_CONFIG)-or-not(Test-Path -LiteralPath $env:SILENTSTREAM_APP_CONFIG)){throw '앱 설정 파일을 찾을 수 없습니다.'}
if([string]::IsNullOrWhiteSpace($env:SILENTSTREAM_APP_EXE)-or-not(Test-Path -LiteralPath $env:SILENTSTREAM_APP_EXE)){throw '앱 실행 파일을 찾을 수 없습니다.'}
$config=Get-Content -LiteralPath $env:SILENTSTREAM_APP_CONFIG -Raw -Encoding UTF8|ConvertFrom-Json
$drive=Get-Item -LiteralPath $env:SILENTSTREAM_APP_CONFIG|Select-Object -ExpandProperty PSDrive
$free=(Get-PSDrive -Name $drive.Name).Free
if($free-lt 5GB){throw '앱 데이터 드라이브 여유 공간이 5GB 미만입니다.'}
$installerHash=if(-not[string]::IsNullOrWhiteSpace($env:SILENTSTREAM_CACHED_INSTALLER)-and(Test-Path -LiteralPath $env:SILENTSTREAM_CACHED_INSTALLER)){(Get-FileHash -LiteralPath $env:SILENTSTREAM_CACHED_INSTALLER -Algorithm SHA256).Hash.ToLowerInvariant()}else{''}
$services=@('SilentStreamInspectionCoordinator','SilentStreamInspectionBroker')|ForEach-Object{Get-Service -Name $_ -ErrorAction Stop|Select-Object Name,Status,StartType}
$appVersion=(Get-Item -LiteralPath $env:SILENTSTREAM_APP_EXE).VersionInfo.FileVersion
$timeSource=(& w32tm.exe /query /source 2>$null|Out-String).Trim()
$powershellWindowVisible=(Get-Process -Id $PID).MainWindowHandle-ne 0
if($powershellWindowVisible){throw '점검 PowerShell에 보이는 창이 감지되었습니다.'}
$result=[ordered]@{machine=$env:COMPUTERNAME;room=$config.deviceName;appVersion=$appVersion;setupSha256=$installerHash;services=$services;cloudflaredRunning=(@(Get-Process cloudflared -ErrorAction SilentlyContinue).Count-gt0);timeSource=$timeSource;configSha256=(Get-FileHash -LiteralPath $env:SILENTSTREAM_APP_CONFIG -Algorithm SHA256).Hash.ToLowerInvariant();freeBytes=$free;youtubeCredentialPresent=(-not[string]::IsNullOrWhiteSpace([string]$config.youtube.refreshTokenEnc));remoteMode=$config.remote.mode;hostname=$config.remote.cloudflareHostname;automaticOperation=$config.automaticOperationEnabled;checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')}
$result|ConvertTo-Json -Depth 8 -Compress
