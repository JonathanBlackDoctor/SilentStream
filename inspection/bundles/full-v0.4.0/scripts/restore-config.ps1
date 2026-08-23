$ErrorActionPreference='Stop';$source=Join-Path $env:SILENTSTREAM_INSPECTION_ROOT "recovery\$($env:SILENTSTREAM_RUN_ID)"
if(-not(Test-Path -LiteralPath (Join-Path $source 'config.json'))){throw '복구 백업이 없어 정리할 수 없습니다.'}
New-Item -ItemType Directory -Path $env:SILENTSTREAM_APPDATA -Force|Out-Null
foreach($file in Get-ChildItem -LiteralPath $source -File){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $env:SILENTSTREAM_APPDATA $file.Name) -Force}
@{restored=@(Get-ChildItem -LiteralPath $source -File|Select-Object -ExpandProperty Name)}|ConvertTo-Json -Compress
