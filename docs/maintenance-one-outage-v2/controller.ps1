#requires -Version 7.0
# Offline-first detached outage state machine. Source is inert until invoked; independent gate and explicit
# manager-issued one-use manifest are required before using a native adapter. Native adapter is NOT supplied.
param([Parameter(Mandatory)][string]$Manifest,[Parameter(Mandatory)][string]$ReceiptDirectory,
 [Parameter(Mandatory)][string]$StopToken,[Parameter(Mandatory)][string]$Adapter,
 [switch]$TestMode)
$ErrorActionPreference='Stop'
function Require([bool]$ok,[string]$reason){if(-not $ok){throw "INTERVENTION_REQUIRED:$reason"}}
function InvokeOp([string]$name,[hashtable]$options=@{}){
 Require ($script:authorizedOps -contains $name) "adapter operation not allowlisted:$name"
 $value= & $script:adapterPath $name $options
 if(-not $?){throw "INTERVENTION_REQUIRED:adapter:$name failed"}
 return $value
}
function WriteReceipt([string]$step,[string]$outcome,[object]$data=$null){
 $r=[ordered]@{step=$step;outcome=$outcome;at=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();data=$data}
 $payload=($r|ConvertTo-Json -Depth 9 -Compress)
 $file=Join-Path $ReceiptDirectory ('{0:d3}-{1}.json' -f (++$script:receiptIndex),$step)
 # Immutable per-step receipt, no overwrite on resume/retry. Writes are owned solely by detached controller.
 $fs=[IO.FileStream]::new($file,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
 try{$b=[Text.Encoding]::UTF8.GetBytes($payload+"`n");$fs.Write($b,0,$b.Length);$fs.Flush($true)}finally{$fs.Dispose()}
 [IO.File]::SetUnixFileMode($file,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
 return $file
}
function CheckStop(){Require ((Get-Content -LiteralPath $script:stopFile -Raw).Trim() -ceq $script:expectedStop) 'stop-token changed; no further mutations'}
function ReadRouteProof([object]$f,[object]$m){
 $proofPath=Join-Path $ReceiptDirectory ('route-proof-{0:d3}.json' -f (++$script:proofIndex))
 Require (-not [IO.File]::Exists($proofPath)) 'route proof already exists; no overwrite'
 $ack=InvokeOp 'route-attestation' @{pid=$f.child.pid;started=$f.child.started;release=$f.release;generation=$f.generation;proofPath=$proofPath}
 Require ($ack.independentProofSha -match '^[a-fA-F0-9]{64}$' -and $ack.proofPath -ceq $proofPath) 'route artifact hash/path missing'
 # Single native FD: O_RDONLY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC, fstat owned 0600
 # regular nlink=1, length 3..4096; read and hash the exact same FD bytes.
 $bytes=[AinurRouteProofFd]::Read($proofPath,[string]$ack.independentProofSha)
 $utf8=[Text.UTF8Encoding]::new($false,$true)
 $raw=$utf8.GetString($bytes)
 $route=$raw|ConvertFrom-Json
 $keys=@($route.PSObject.Properties.Name|Sort-Object)
 $want=@('core_sha256','generation','process_id','process_started_utc','provider_policy','release','route_class')
 Require (@(Compare-Object $keys $want).Count -eq 0) 'route artifact field whitelist mismatch'
 Require ($route.route_class -ceq 'subscription' -and $route.provider_policy -ceq 'openai_subscription_strict' -and
 [int]$route.process_id -eq [int]$f.child.pid -and $route.process_started_utc -ceq $f.child.started -and
 $route.release -ceq $f.release -and [long]$route.generation -eq [long]$f.generation -and
 $route.core_sha256 -ceq $f.sourceHash -and $f.sourceHash -ceq $m.expectedCoreHash) 'same-child authenticated route/Core not proven'
 return [pscustomobject]@{route=$route;artifactSha=$ack.independentProofSha;artifactPath=$proofPath}
}
function AssertOld([object]$m,[object]$f){
 Require ($f.label -ceq $m.label -and [int]$f.parent.pid -eq [int]$m.parent.pid -and
 $f.parent.started -ceq $m.parent.started -and $f.parent.family -ceq 'dotnet-supervisor' -and
 [int]$f.child.pid -eq [int]$m.child.pid -and $f.child.started -ceq $m.child.started -and
 $f.child.family -ceq 'dotnet-server' -and [int]$f.child.ppid -eq [int]$m.parent.pid -and
 [int]$f.listener.pid -eq [int]$m.child.pid -and [int]$f.listener.count -eq 1 -and
 $f.listener.endpoint -ceq '127.0.0.1:5181' -and [int]$f.descendantCount -eq 1 -and
 $f.release -ceq 'r20261001190802-e022406443' -and [int]$f.schema -eq 6) 'old exact process ancestry/listener/release drift'
}
function AssertSeven([object]$s,[object]$m){
 Require ([int]$s.agentIds.Count -eq 7 -and [int]$m.agentIds.Count -eq 7 -and
 (@(Compare-Object @($s.agentIds|Sort-Object) @($m.agentIds|Sort-Object)).Count -eq 0) -and
 $s.tulkasState -ceq 'paused' -and [int]$s.schema -eq 6) 'seven agent identities/Tulkas/schema'
 foreach($k in @('messages','sessions','notifications','events','cost_events','reservations','tool_invocations','model_requests')){
  Require ([long]$s.counts.$k -ge [long]$m.counts.$k) "committed row count regressed:$k"
 }
 Require ([bool]$s.integrity -and [bool]$s.committedKeysPreserved) 'SQLite integrity or committed keyset/digests'
}
function AssertSupervisor([object]$f,[object]$m,[bool]$allowChild){
 Require ($f.label -ceq $m.label -and $f.parent.family -ceq 'dotnet-supervisor' -and
 [int]$f.listener.count -eq 1 -and $f.listener.endpoint -ceq '127.0.0.1:5181' -and
 [int]$f.child.pid -eq [int]$f.listener.pid -and [int]$f.child.ppid -eq [int]$f.parent.pid -and
 $f.child.family -ceq 'dotnet-server' -and $f.release -ceq $m.expectedRelease -and
 [int]$f.schema -eq 6 -and $f.sourceHash -ceq $m.expectedCoreHash -and [int]$f.descendantCount -eq 1) 'new parent/child/listener/release/core mismatch'
}
$script:authorizedOps=@('old-facts','preflight-backup','work-classification','drain','disable-label','disabled-status','kill-old-parent-exact','after-parent-facts','stop-old-child-graceful','after-child-facts','kill-old-child-exact','stopped-backup','install-protected','new-spawn-status','enable-label','bootstrap-once','new-facts','activate-candidate','upgrade-status','route-attestation','snapshot-compare','forensic-status','final-facts')
$script:adapterPath=(Resolve-Path -LiteralPath $Adapter).Path
$m=Get-Content -LiteralPath $Manifest -Raw|ConvertFrom-Json
$proofSource=Join-Path $PSScriptRoot 'route-proof-fd.cs'
Require ($m.routeProofSourceSha -ceq (Get-FileHash -LiteralPath $proofSource -Algorithm SHA256).Hash) 'native single-FD proof source hash drift'
Add-Type -Path $proofSource -ErrorAction Stop
Require ($m.planVersion -ceq 'one-outage-v2' -and $m.label -ceq 'gui/501/com.ainur.supervisor.5181' -and
 $m.home -ceq '/Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev' -and
 [int]$m.port -eq 5181 -and $m.expectedRelease -ceq 'r20261002014730-766fa9f930' -and
 $m.expectedCoreHash -ceq '787DD2BB636726472F91689957C283851386E2C0B50C85E45A0E643E75DA59A3' -and
 $m.candidate -ceq 'r20261002154643-01d6c043e2' -and $m.candidateCoreHash -ceq '1CD0AE8D1E2BEE1BCC9DA30C484B04284B73257712BC7C4F737EAF338AF364E3' -and $m.supervisorSource -ceq '254065c008e0ef61565971ebccfa11462c262e01' -and
 $m.oldRoute -ceq 'subscription' -and $m.riskApproval -ceq 'interrupted-turn-and-admission-race-accepted') 'manifest not exact authorized pins'
Require ((Get-FileHash -LiteralPath $Adapter).Hash -ceq $m.adapterHash) 'adapter SHA drift'
if($TestMode){
 Require ($script:adapterPath -ceq (Join-Path $PSScriptRoot 'mock-adapter.ps1') -and
 $m.adapterHash -ceq (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'mock-adapter.ps1')).Hash -and
 $m.liveApproved -eq $false) 'test mode restricted to frozen mock only'
}else{
 # Root strict-only policy supersedes legacy 766/01d6 offline mock rollback pins.
 # NO native adapter can run until independently accepted BOTH running and automatic
 # rollback/recovery strict releases are re-pinned in reviewed controller source.
 Require $false 'strict candidate + automatic rollback/recovery target not pinned; legacy 766/e022 unsafe'
 Require ($m.liveApproved -eq $true -and $m.detachedOwner -and $m.independentGateSha -and $m.managerGoSha) 'not authorized for native adapter'
 Require ($m.approvedNativeAdapterHash -ceq $m.adapterHash) 'native adapter hash not independently approved'
}
Require (-not [IO.Directory]::Exists($ReceiptDirectory)) 'receipt directory exists; never retry'
[IO.Directory]::CreateDirectory($ReceiptDirectory)|Out-Null
[IO.File]::SetUnixFileMode($ReceiptDirectory,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
$script:receiptIndex=0
$script:proofIndex=0
$script:stopFile=(Join-Path $ReceiptDirectory 'STOP')
Require ($StopToken -match '^[a-f0-9]{64}$') 'stop token must be one-use 256-bit lowercase hex'
$script:expectedStop=$StopToken
$fs=[IO.FileStream]::new($script:stopFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
try{$b=[Text.Encoding]::UTF8.GetBytes($StopToken);$fs.Write($b,0,$b.Length);$fs.Flush($true)}finally{$fs.Dispose()}
[IO.File]::SetUnixFileMode($script:stopFile,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
$manifestHash=(Get-FileHash -LiteralPath $Manifest).Hash
WriteReceipt 'claim' 'claimed' ([ordered]@{manifestSha=$manifestHash;adapterSha=$m.adapterHash;owner=$m.detachedOwner;test=[bool]$TestMode})|Out-Null
try{
 CheckStop
 $old=InvokeOp 'old-facts';AssertOld $m $old;WriteReceipt 'old-identity' 'matched' $old|Out-Null
 $pre=InvokeOp 'preflight-backup' @{backupKind='online';neverOverwrite=$true};AssertSeven $pre $m
 Require ($pre.backupSha -match '^[a-fA-F0-9]{64}$' -and $pre.backupLocation -and $pre.walBacked -eq $true) 'preflight online WAL backup absent'
 WriteReceipt 'preflight' 'verified' $pre|Out-Null
 $work=InvokeOp 'work-classification';Require ($work.knownConsequential -eq $false -and $work.approval -ceq 'approved') 'known consequential invocation/no owner decision'
 WriteReceipt 'classified' 'approved' $work|Out-Null
 $drain=InvokeOp 'drain';WriteReceipt 'drain' 'best-effort' $drain|Out-Null
 CheckStop
 AssertOld $m (InvokeOp 'old-facts');$null=InvokeOp 'disable-label';$disabled=InvokeOp 'disabled-status'
 Require ($disabled.label -ceq $m.label -and $disabled.disabled -eq $true) 'launchd exact disabled readback missing'
 WriteReceipt 'disabled' 'verified' $disabled|Out-Null
 CheckStop
 AssertOld $m (InvokeOp 'old-facts');$null=InvokeOp 'kill-old-parent-exact' @{pid=$m.parent.pid;started=$m.parent.started;signal='KILL';neverTree=$true}
 $after=InvokeOp 'after-parent-facts';Require ($after.oldParentGone -eq $true -and $after.newParentAbsent -eq $true -and [int]$after.child.pid -eq [int]$m.child.pid -and $after.child.started -ceq $m.child.started -and [int]$after.listener.pid -eq [int]$m.child.pid -and [int]$after.listener.count -eq 1 -and $after.disabled -eq $true) 'old supervisor respawn/child drift'
 WriteReceipt 'parent-stopped' 'scoped' $after|Out-Null
 CheckStop
 $null=InvokeOp 'stop-old-child-graceful' @{pid=$m.child.pid;started=$m.child.started}
 $afterChild=InvokeOp 'after-child-facts'
 if($afterChild.oldChildGone -ne $true){
  Require ($afterChild.stillExact -eq $true -and $afterChild.knownConsequential -eq $false -and $afterChild.ownerApprovedExactKill -eq $true) 'child incomplete or specific work active'
  CheckStop;$null=InvokeOp 'kill-old-child-exact' @{pid=$m.child.pid;started=$m.child.started;signal='KILL';neverTree=$true};$afterChild=InvokeOp 'after-child-facts'
 }
 Require ($afterChild.oldChildGone -eq $true -and $afterChild.oldParentGone -eq $true -and $afterChild.listenerAbsent -eq $true -and $afterChild.newParentAbsent -eq $true -and $afterChild.disabled -eq $true) 'old processes or listener remain'
 WriteReceipt 'old-stopped' 'absent' $afterChild|Out-Null
 CheckStop
 $stopped=InvokeOp 'stopped-backup' @{backupKind='online';neverOverwrite=$true};AssertSeven $stopped $m
 Require ($stopped.backupSha -match '^[a-fA-F0-9]{64}$' -and $stopped.backupLocation -and $stopped.walBacked -eq $true -and $stopped.originalDbPreserved -eq $true) 'stopped WAL backup/integrity missing'
 WriteReceipt 'stopped-backup' 'verified' $stopped|Out-Null
 $claim=InvokeOp 'new-spawn-status';Require ($claim.absent -eq $true -and $claim.parentAbsent -eq $true -and $claim.listenerAbsent -eq $true) 'orphan spawn lease/child/parent'
 WriteReceipt 'claim-free' 'verified' $claim|Out-Null
 CheckStop
 $installed=InvokeOp 'install-protected';Require ($installed.sourceSha -ceq $m.supervisorSource -and $installed.initialRelease -ceq $m.expectedRelease -and $installed.coreHash -ceq $m.expectedCoreHash -and $installed.subscriptionPin -eq $true -and $installed.sameHomePort -eq $true -and $installed.rollbackPreserved -eq $true) 'protected installation hash/home/pin mismatch'
 WriteReceipt 'installed' 'verified' $installed|Out-Null
 CheckStop
 $null=InvokeOp 'enable-label';$null=InvokeOp 'bootstrap-once'
 $new=InvokeOp 'new-facts';AssertSupervisor $new $m $true;WriteReceipt 'new-ready' 'scoped' $new|Out-Null
 # No speculative self-service Bridge. Adapter may return only an independently reviewed same-process readback.
 $route=ReadRouteProof $new $m
 WriteReceipt 'initial-route' 'subscription' $route|Out-Null
 CheckStop
 $null=InvokeOp 'activate-candidate' @{release=$m.candidate};$upgrade=InvokeOp 'upgrade-status'
 Require ($upgrade.candidate -ceq $m.candidate -and $upgrade.previous -ceq $m.expectedRelease -and $upgrade.state -in @('succeeded','rolled_back')) 'upgrade outcome missing; intervention without blind retry'
 if($upgrade.state -ceq 'rolled_back'){
  $rolled=InvokeOp 'new-facts';AssertSupervisor $rolled $m $true
  $route=ReadRouteProof $rolled $m
  $afterDb=InvokeOp 'snapshot-compare';AssertSeven $afterDb $m
  Require ($afterDb.committedBaselinePreserved -eq $true -and $afterDb.unknownChargesPreserved -eq $true -and $afterDb.noOverwrite -eq $true -and $afterDb.backupUnchanged -eq $true) 'rollback DB/ledger/history/no-overwrite guard'
  $final=InvokeOp 'final-facts';Require ($final.pid -eq $rolled.child.pid -and $final.started -ceq $rolled.child.started -and $final.release -ceq $rolled.release -and $final.soleListener -eq $true -and $final.routeProofStillCurrent -eq $true) 'rollback postreceipt process drift'
  WriteReceipt 'completed' 'rolled-back-contained' ([ordered]@{upgrade=$upgrade;child=$rolled;route=$route;db=$afterDb;final=$final})|Out-Null
  return
 }
 $candidate=InvokeOp 'new-facts';$candidateManifest=$m.PSObject.Copy();$candidateManifest.expectedRelease=$m.candidate;$candidateManifest.expectedCoreHash=$m.candidateCoreHash
 AssertSupervisor $candidate $candidateManifest $true
 $route=ReadRouteProof $candidate $candidateManifest
 $afterDb=InvokeOp 'snapshot-compare';AssertSeven $afterDb $m
 Require ($afterDb.committedBaselinePreserved -eq $true -and $afterDb.unknownChargesPreserved -eq $true -and $afterDb.noOverwrite -eq $true -and $afterDb.backupUnchanged -eq $true) 'ledger/history/cash/inbox/no-overwrite guard'
 $final=InvokeOp 'final-facts';Require ($final.pid -eq $candidate.child.pid -and $final.started -ceq $candidate.child.started -and $final.release -ceq $candidate.release -and $final.soleListener -eq $true -and $final.routeProofStillCurrent -eq $true) 'candidate postreceipt process drift'
 WriteReceipt 'completed' 'candidate-contained' ([ordered]@{upgrade=$upgrade;child=$candidate;route=$route;db=$afterDb;final=$final})|Out-Null
}catch{
 $errorClass=$_.Exception.Message
 $errorStack=$_.ScriptStackTrace
 try{WriteReceipt 'intervention' 'halted' ([ordered]@{error=$errorClass;stack=$errorStack;noAutoRestore=$true;noAutoRetry=$true})|Out-Null}catch{}
 throw
}