$ErrorActionPreference='Stop';$config=Get-Content -LiteralPath $env:SILENTSTREAM_APP_CONFIG -Raw -Encoding UTF8|ConvertFrom-Json
$sources=@($config.audio.sources);$mics=@($sources|Where-Object{$_.kind-eq'mic'});$system=@($sources|Where-Object{$_.kind-eq'system'})
if($system.Count-lt 1){throw '시스템 오디오 소스가 설정되지 않았습니다.'};if($mics.Count-lt 1){throw '마이크 소스가 설정되지 않았습니다.'}
$devices=@(Get-CimInstance Win32_SoundDevice -ErrorAction SilentlyContinue|Select-Object Name,Status)
if($devices.Count-lt 1){throw 'Windows 오디오 장치를 찾을 수 없습니다.'}
@{configuredSources=$sources.Count;configuredMics=$mics.Count;windowsDevices=$devices}|ConvertTo-Json -Depth 6 -Compress
