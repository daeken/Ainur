$ErrorActionPreference='Stop'
$fixture=Join-Path '/tmp' ('ainur-disposable-wal-fixture-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture)|Out-Null
[IO.File]::SetUnixFileMode($fixture,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
$env:AINUR_OFFLINE_STOP='test-only-stop-'+[guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText((Join-Path $fixture 'STOP'),$env:AINUR_OFFLINE_STOP)
$env:AINUR_OFFLINE_FAKE_SHA=(Get-FileHash (Join-Path $PSScriptRoot 'fake-native.ps1')).Hash
$fake=Join-Path $PSScriptRoot 'fake-native.ps1';$native=Join-Path $PSScriptRoot 'native-adapter-prototype.ps1'
foreach($case in @('normal','foreign-listener','wrong-parent-start','wrong-child-ancestry','disabled-failed')){
 $env:AINUR_NATIVE_CASE=$case
 $trace=Join-Path $fixture 'native-trace.jsonl';[IO.File]::WriteAllText($trace,'')
 try{$o=@(& $native -Operation 'kill-old-parent-exact' -Options @{pid=61760;started='P0';signal='KILL';neverTree=$true} -FixtureRoot $fixture -NativeRunner $fake 2>&1)}catch{if($case -eq 'normal'){throw};$o=@($_.Exception.Message)}
 $ops=@(Get-Content $trace|Where-Object {$_}|ForEach-Object {($_|ConvertFrom-Json).op})
 if($case -eq 'normal'){
  if(-not $? -or @($ops|Where-Object {$_ -eq 'signal-exact'}).Count -ne 1){throw "FAIL normal ops=$($ops -join ',') $($o -join ',')"}
 }else{
  if($ops -contains 'signal-exact'){throw "FAIL $case reached signal"}
 }
 "PASS fake-native $case signalCount=$(@($ops|Where-Object {$_ -eq 'signal-exact'}).Count)"
}
$env:AINUR_OFFLINE_STOP='different';$env:AINUR_NATIVE_CASE='normal'
[IO.File]::WriteAllText((Join-Path $fixture 'native-trace.jsonl'),'')
try{$null=@(& $native -Operation 'kill-old-parent-exact' -Options @{pid=61760;started='P0';signal='KILL';neverTree=$true} -FixtureRoot $fixture -NativeRunner $fake 2>&1)}catch{}
if((Get-Content (Join-Path $fixture 'native-trace.jsonl') -Raw).Length -ne 0){throw 'STOP mismatch reached fake native'}
'PASS STOP mismatch prevents any adapter operation'
$global:LASTEXITCODE=0