$ErrorActionPreference='Stop';$target=Join-Path $env:SILENTSTREAM_INSPECTION_ROOT "recovery\$($env:SILENTSTREAM_RUN_ID)"
if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force};New-Item -ItemType Directory -Path $target -Force|Out-Null
$names=@('config.json','youtube_token.dat','youtube_caption_token.dat','pending_splits.json','upload_queue.json','push_subscriptions.json','vapid_keys.json')
foreach($name in $names){$source=Join-Path $env:SILENTSTREAM_APPDATA $name;if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $target $name) -Force}}
if(-not(Test-Path -LiteralPath (Join-Path $target 'config.json'))){throw '복구 백업에 config.json이 없습니다.'}
@{backupPath=$target;files=@(Get-ChildItem -LiteralPath $target -File|Select-Object -ExpandProperty Name)}|ConvertTo-Json -Compress
