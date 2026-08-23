$ErrorActionPreference='Stop'
$path=$env:SILENTSTREAM_APP_CONFIG
if(-not(Test-Path -LiteralPath $path)){throw '앱 설정 파일이 없습니다.'}
$config=Get-Content -LiteralPath $path -Raw -Encoding UTF8|ConvertFrom-Json
$config.youtube.privacy='private'
$config.automaticOperationEnabled=$false
$temp=$path+'.inspection.tmp';$config|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $temp -Encoding UTF8;Move-Item -LiteralPath $temp -Destination $path -Force
@{privacy='private';automaticOperation=$false;restoredByCleanup=$true}|ConvertTo-Json -Compress
