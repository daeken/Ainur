$ErrorActionPreference='Stop'
$root=(Resolve-Path "$PSScriptRoot/../../").Path
$p=$PSScriptRoot
$manifest=Join-Path $p 'mock-manifest.json'
$adapter=Join-Path $p 'mock-adapter.ps1'
$controller=Join-Path $p 'controller.ps1'
$hostPath=Join-Path $root 'src/Ainur.Server/.ainur/dev/supervisor/bootstrap/host/HandoffHost.dll'
if(-not (Test-Path $hostPath)){$hostPath='/Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev/supervisor/bootstrap/host/HandoffHost.dll'}
$mock=Get-Content $manifest -Raw|ConvertFrom-Json
if((Get-FileHash $adapter).Hash -cne $mock.adapterHash){throw 'mock adapter drift'}
$cases=@('normal','rolled-back','final-pid-drift','crash-at-parent','disable-failed','second-parent','orphan-lease','bad-route','bad-core-sha','backup-failed','child-survives','install-failed')
$log=@()
foreach($case in $cases){
 $dir=Join-Path ([IO.Path]::GetTempPath()) ('ainur-maint-v2-offline-'+[guid]::NewGuid().ToString('N'))
 $trace=Join-Path ([IO.Path]::GetTempPath()) ('ainur-maint-v2-trace-'+[guid]::NewGuid().ToString('N')+'.jsonl')
 [IO.File]::WriteAllText($trace,'')
 $env:AINUR_MAINT_MOCK_CASE=$case;$env:AINUR_MAINT_MOCK_TRACE=$trace
 $output=@( & dotnet $hostPath $controller -Manifest $manifest -ReceiptDirectory $dir -StopToken ('a'*64) -Adapter $adapter -TestMode 2>&1 )
 $exit=$LASTEXITCODE
 $ops=@(Get-Content $trace|Where-Object {$_}|ForEach-Object {($_|ConvertFrom-Json).op})
 $receipts=@(Get-ChildItem $dir -Filter '*.json'|Sort-Object Name)
 $final=(Get-Content $receipts[-1].FullName -Raw|ConvertFrom-Json)
 if($case -in @('normal','rolled-back')){
  $expected=if($case -eq 'normal'){'candidate-contained'}else{'rolled-back-contained'}
  if($exit -ne 0 -or $final.outcome -cne $expected -or $ops[-1] -ne 'final-facts' -or @($ops|Where-Object {$_ -eq 'route-attestation'}).Count -ne 2){throw "FAIL $case exit=$exit last=$($final.outcome) trace=$trace"}
 }else{
  if($exit -eq 0 -or $final.outcome -cne 'halted' -or ($case -ne 'final-pid-drift' -and $ops -contains 'snapshot-compare')){throw "FAIL $case exit=$exit last=$($final.outcome) trace=$trace"}
  if($case -in @('backup-failed','disable-failed') -and $ops -contains 'kill-old-parent-exact'){throw "FAIL $case reached kill"}
  if($case -eq 'second-parent' -and $ops -contains 'stop-old-child-graceful'){throw 'FAIL second parent reached child stop'}
  if($case -eq 'orphan-lease' -and $ops -contains 'install-protected'){throw 'FAIL orphan lease installed replacement'}
 }
 # One-use owner receipts: invoke exact manifest/dir again, must fail BEFORE any native/mock adapter operation.
 $prior=$ops.Count
 $retry=@(& dotnet $hostPath $controller -Manifest $manifest -ReceiptDirectory $dir -StopToken ('a'*64) -Adapter $adapter -TestMode 2>&1)
 $retryExit=$LASTEXITCODE
 if($retryExit -eq 0 -or @((Get-Content $trace|Where-Object {$_})).Count -ne $prior){throw "FAIL retry $case"}
 $line="PASS $case exit=$exit receipts=$($receipts.Count) ops=$($ops.Count) retryBlocked=$retryExit"
 $line;$log+=$line
}
$env:AINUR_MAINT_MOCK_CASE=$null;$env:AINUR_MAINT_MOCK_TRACE=$null
$global:LASTEXITCODE=0
"PASS: $($cases.Count) offline adapter-invocation cases; NO native adapter supplied, NO live controls"