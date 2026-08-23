$ErrorActionPreference='Stop';$config=Get-Content -LiteralPath $env:SILENTSTREAM_APP_CONFIG -Raw -Encoding UTF8|ConvertFrom-Json
$folder=[string]$config.recording.folder
if([string]::IsNullOrWhiteSpace($folder)){$roaming=Split-Path $env:SILENTSTREAM_APPDATA -Parent;$appData=Split-Path $roaming -Parent;$profile=Split-Path $appData -Parent;$folder=Join-Path $profile 'Videos\MediaCaptureHelper'}
if(-not(Test-Path -LiteralPath $folder)){throw "녹화 폴더가 없습니다: $folder"}
$files=@(Get-ChildItem -LiteralPath $folder -File|Where-Object{$_.Extension-in@('.mp4','.mkv','.ts')}|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 5)
if($files.Count-lt 1){throw '검증할 최근 녹화 파일이 없습니다.'};if(($files|Measure-Object Length -Maximum).Maximum-lt 1MB){throw '최근 녹화 파일 크기가 비정상적으로 작습니다.'}
@{folder=$folder;recent=@($files|Select-Object Name,Length,LastWriteTimeUtc)}|ConvertTo-Json -Depth 5 -Compress
