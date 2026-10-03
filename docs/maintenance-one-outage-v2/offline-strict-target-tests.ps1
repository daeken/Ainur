# HandoffHost OFFLINE-only strict physical-target/automatic recovery allowlist regressions.
# Test copies only repo mock JSON into disposable /tmp. No launchd/native/server/providercall.
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root='/tmp/ainur-strict-target-fixture-'+[guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($root)|Out-Null
try {
 $m=(Get-Content (Join-Path $PSScriptRoot 'strict-target-mock-only.json') -Raw|ConvertFrom-Json)
 $f=(Get-Content (Join-Path $PSScriptRoot 'strict-facts-mock-only.json') -Raw|ConvertFrom-Json)
 $source=(Get-Content (Join-Path $PSScriptRoot 'strict-target-source-only.json') -Raw|ConvertFrom-Json)
 $hostExe='/Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev/supervisor/bootstrap/host/HandoffHost.dll'
 function InvokeCase([string]$name,[object]$manifest,[object]$facts,[bool]$passes){
  $inputDir=Join-Path $root ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($inputDir)|Out-Null
  $manifestPath=Join-Path $inputDir 'manifest.json';$factsPath=Join-Path $inputDir 'facts.json';$receipts='/tmp/ainur-strict-target-fixture-'+[guid]::NewGuid().ToString('N')
  [IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 20)+"`n");[IO.File]::WriteAllText($factsPath,($facts|ConvertTo-Json -Depth 20)+"`n")
  $out=& dotnet $hostExe (Join-Path $PSScriptRoot 'strict-target-guard.ps1') -Manifest $manifestPath -FixtureFacts $factsPath -ReceiptDirectory $receipts -TestMode 2>&1;$code=$LASTEXITCODE
  if($passes){
   if($code -ne 0 -or -not (Test-Path "$receipts/strict-target-gate.json")){throw "$name expected pass code=$code output=$($out -join ' ')"}
   $r=Get-Content "$receipts/strict-target-gate.json" -Raw|ConvertFrom-Json
   if($r.noNativeAction -ne $true -or $r.autoLegacyForbidden -ne $true -or $r.dbNeverRestoreOverNewer -ne $true){throw "$name receipt safety absent"}
   [IO.Directory]::Delete($receipts,$true)
  }elseif($code -eq 0 -or (Test-Path $receipts)){throw "$name expected HOLD without receipt code=$code output=$($out -join ' ')"}
  "PASS $name $(if($passes){'authorized mock-only receipt'}else{'STRICT_HOLD before receipt'})"
 }
 function M { return ($m|ConvertTo-Json -Depth 20|ConvertFrom-Json) }
 function F { return ($f|ConvertTo-Json -Depth 20|ConvertFrom-Json) }
 InvokeCase 'candidate ready dual strict target' (M) (F) $true
 $a=F;$a.outcome='rolled-back-ready';$a.targetRelease=$m.initial.release
 InvokeCase 'contained strict rollback target' (M) $a $true
 $a=F;$a.outcome='common-source-failure';$a.targetRelease='none';$a.childAction='HOLD_ESCALATE_WITH_LATEST_DB'
 InvokeCase 'same-source common failure no old binary/retry/DB restore' (M) $a $true
 $a=F;$a.outcome='common-source-failure';$a.targetRelease=$m.initial.release;InvokeCase 'common failure incorrectly auto rollback' (M) $a $false
 $a=F;$a.supervisorObservedPrevious='r20261002014730-766fa9f930';InvokeCase 'stale previous 766' (M) $a $false
 $a=F;$a.supervisorObservedActive='r20261001190802-e022406443';InvokeCase 'old active e022 on restart' (M) $a $false
 $a=F;$a.supervisorAutoRecoveryAllowlist="$($m.initial.release),r20261002014730-766fa9f930";InvokeCase 'stale auto recovery allowlist' (M) $a $false
 $a=F;$a.allPathsGateBeforeSpawn=$false;InvokeCase 'startup path lacks policy gate' (M) $a $false
 $a=F;$a.oldRuntimeAutoFallback=$true;InvokeCase 'implicit old runtime fallback' (M) $a $false
 $a=F;$a.restoreOlderDbOverNewer=$true;InvokeCase 'overwrite newer committed DB' (M) $a $false
 $a=F;$a.initial.strictMode='UNATTESTED';InvokeCase 'initial all-child enforcement absent' (M) $a $false
 $a=F;$a.candidate.routeReceiptSha='1'*64;InvokeCase 'candidate per-child receipt mismatch' (M) $a $false
 $a=M;$a.initial.coreSha='2'*64;InvokeCase 'strict release physical Core mismatch' $a (F) $false
 $a=M;$a.initial.strictReceiptSha=$a.candidate.strictReceiptSha;InvokeCase 'shared attestation token not child-specific' $a (F) $false
 $a=M;$a.initial.release='r20261002014730-766fa9f930';InvokeCase 'historical 766 candidate not strict' $a (F) $false
 InvokeCase 'source-only no supervisor/individual receipts cannot proceed' $source (F) $false
 'PASS all strict target offline cases, no native actions'
}finally{[IO.Directory]::Delete($root,$true)}
exit 0
