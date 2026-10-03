# Strict release-transition preflight/test seam ONLY. Does not read production paths or start/stop anything.
# An executable native controller must independently incorporate this gate BEFORE enabling/bootstrap/upgrade
# and harden source/loaded-binary/receipt origin; this source is not authority to launch a child.
param([Parameter(Mandatory)][string]$Manifest,[Parameter(Mandatory)][string]$FixtureFacts,
      [Parameter(Mandatory)][string]$ReceiptDirectory,[switch]$TestMode)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
function Guard([bool]$value,[string]$reason){if(-not $value){throw "STRICT_HOLD:$reason"}}
# No production-call mode. Keep this script useful for reviewing an incomplete physical pairing safely.
Guard ([bool]$TestMode) 'no native or live adapter implemented'
$dir=[IO.Path]::GetFullPath($ReceiptDirectory)
Guard ($dir.StartsWith('/tmp/ainur-strict-target-fixture-',[StringComparison]::Ordinal) -and -not [IO.Directory]::Exists($dir)) 'exclusive disposable /tmp receipt only'
$m=Get-Content -LiteralPath $Manifest -Raw|ConvertFrom-Json
$f=Get-Content -LiteralPath $FixtureFacts -Raw|ConvertFrom-Json
$sha='\A[0-9A-Fa-f]{64}\z'
$gitSha='\A[0-9A-Fa-f]{40}\z'
$release='\Ar[0-9]{8}-strict-(?:rollback|candidate)-[a-zA-Z0-9_.-]+\z'
Guard ($m.planVersion -ceq 'strict-target-guard-mock-v1' -and $m.enabled -eq $false -and
 $m.policy -ceq 'subscription-only-explicit-reject-paid-all-entry-paths' -and
 $m.legacyForbidden -ceq 'e022,766,01d6-without-strict-acceptance') 'strict policy or legacy denylist absent'
Guard ($m.sourceSha -match $gitSha -and $m.sourceTreeSha -match $gitSha -and
 $m.supervisorSourceSha -match $gitSha -and $m.supervisorBinarySha -match $sha -and
 $m.independentStrictGateSha -match $sha -and $m.supervisorAutoTargetGateSha -match $sha) 'source and independent/supervisor acceptances not pinned'
$one=$m.initial;$two=$m.candidate
foreach($target in @($one,$two)){
 Guard ($target.release -match $release -and $target.coreSha -match $sha -and $target.payloadManifestSha -match $sha -and
  $target.serverSha -match $sha -and $target.exeSha -match $sha -and $target.sourceSha -ceq $m.sourceSha -and
  $target.strictReceiptSha -match $sha -and $target.routeReceiptSha -match $sha -and
  $target.providerPolicy -ceq 'openai_subscription_strict' -and $target.strictMode -ceq 'enforced') 'missing same-source per-target strict hashes/receipts'
}
Guard ($one.release -cne $two.release -and $one.coreSha -ceq $two.coreSha -and
 $one.serverSha -ceq $two.serverSha -and $one.exeSha -ceq $two.exeSha -and $one.payloadManifestSha -ceq $two.payloadManifestSha -and
 $one.physicalPath -cne $two.physicalPath -and $one.strictReceiptSha -cne $two.strictReceiptSha -and
 $one.routeReceiptSha -cne $two.routeReceiptSha) 'two separate physical locations same accepted payload and distinct per-child receipts required (not software diversity)'
Guard ($f.readOnlyOldFence -eq $true -and $f.stoppedDbBackupIntegrity -eq $true -and
 $f.sourceDbPreserved -eq $true -and $f.knownConsequential -eq $false -and
 $f.scope -ceq 'unique-parent-child-listener-approved') 'exact old process/DB/work checkpoints missing'
Guard ($f.supervisorLaunchAllowlist -ceq "$($one.release),$($two.release)" -and
 $f.supervisorAutoRecoveryAllowlist -ceq "$($one.release),$($two.release)" -and
 $f.supervisorObservedActive -ceq $one.release -and $f.supervisorObservedPrevious -ceq $two.release -and
 $f.supervisorFallbackIfUnknown -ceq 'HOLD_NO_SPAWN' -and $f.allPathsGateBeforeSpawn -eq $true -and
 $f.supervisorSourceSha -ceq $m.supervisorSourceSha -and $f.independentSupervisorGateSha -ceq $m.supervisorAutoTargetGateSha) 'stale supervisor previous/default/auto target can spawn unsafe binary'
Guard ($f.initial.release -ceq $one.release -and $f.initial.coreSha -ceq $one.coreSha -and
 $f.initial.serverSha -ceq $one.serverSha -and $f.initial.strictReceiptSha -ceq $one.strictReceiptSha -and
 $f.initial.routeReceiptSha -ceq $one.routeReceiptSha -and $f.initial.routeClass -ceq 'subscription' -and
 $f.initial.strictMode -ceq 'enforced' -and $f.initial.childBinding -ceq 'sole-listener-PID-start-loaded-Core') 'initial same-process proof missing'
Guard ($f.candidate.release -ceq $two.release -and $f.candidate.coreSha -ceq $two.coreSha -and
 $f.candidate.serverSha -ceq $two.serverSha -and $f.candidate.strictReceiptSha -ceq $two.strictReceiptSha -and
 $f.candidate.routeReceiptSha -ceq $two.routeReceiptSha -and $f.candidate.routeClass -ceq 'subscription' -and
 $f.candidate.strictMode -ceq 'enforced' -and $f.candidate.childBinding -ceq 'sole-listener-PID-start-loaded-Core') 'candidate same-process proof missing'
Guard ($f.outcome -in @('candidate-ready','rolled-back-ready','common-source-failure') -and
 $f.oldRuntimeAutoFallback -eq $false -and $f.restoreOlderDbOverNewer -eq $false) 'unsafe outcome/fallback/DB restore'
if($f.outcome -ceq 'common-source-failure'){
 Guard ($f.childAction -ceq 'HOLD_ESCALATE_WITH_LATEST_DB' -and $f.targetRelease -ceq 'none' -and
  $f.noFurtherSpawn -eq $true) 'common-source failure must HOLD, not cycle identical code or old binaries'
}else{
 $chosen=if($f.outcome -ceq 'candidate-ready'){$two}else{$one}
 Guard ($f.targetRelease -ceq $chosen.release -and $f.childAction -ceq 'VALIDATE_RECEIPT_ONLY' -and
  $f.noFurtherSpawn -eq $true) 'target not in strict allowlist or repeated spawn'
}
[IO.Directory]::CreateDirectory($dir)|Out-Null
[IO.File]::SetUnixFileMode($dir,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
$path=Join-Path $dir 'strict-target-gate.json'
$receipt=[ordered]@{phase='OFFLINE_TEST_ONLY';outcome=$f.outcome;initialRelease=$one.release;candidateRelease=$two.release;targetRelease=$f.targetRelease;commonSourceSha=$m.sourceSha;physicalRedundancyNotDiversity=$true;autoLegacyForbidden=$true;dbNeverRestoreOverNewer=$true;noNativeAction=$true}
$payload=[Text.Encoding]::UTF8.GetBytes(($receipt|ConvertTo-Json -Compress)+"`n")
$stream=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
try{$stream.Write($payload,0,$payload.Length);$stream.Flush($true)}finally{$stream.Dispose()}
[IO.File]::SetUnixFileMode($path,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
"PASS strict offline $($f.outcome): $($f.targetRelease); no native actions"
