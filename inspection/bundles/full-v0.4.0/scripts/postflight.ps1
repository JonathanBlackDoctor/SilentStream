$ErrorActionPreference='Stop';$backup=Join-Path $env:SILENTSTREAM_INSPECTION_ROOT "recovery\$($env:SILENTSTREAM_RUN_ID)\config.json"
if(-not(Test-Path -LiteralPath $backup)){throw '기준 설정 백업이 없습니다.'};if(-not(Test-Path -LiteralPath $env:SILENTSTREAM_APP_CONFIG)){throw '최종 설정 파일이 없습니다.'}
$before=(Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash;$after=(Get-FileHash -LiteralPath $env:SILENTSTREAM_APP_CONFIG -Algorithm SHA256).Hash
if($before-ne$after){throw '최종 설정이 점검 전 설정과 일치하지 않습니다.'}
@{configRestored=$true;configSha256=$after.ToLowerInvariant();checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json -Compress
