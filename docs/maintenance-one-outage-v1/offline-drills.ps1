# OFFLINE-only model: no shell native commands, network, filesystem control, or runtime access.
$ErrorActionPreference='Stop'
function Gate($condition,[string]$label){if(-not $condition){throw "STOP:$label"}}
function Simulate([string]$scenario){
 $s=[ordered]@{parent=61760;child=64728;parentStart='P0';childStart='C0';labelDisabled=$false;childListener=64728;newParent=$null;newChild=$null;lease='absent';candidateRoute='subscription';rollbackRoute='subscription';agentIds=7;tulkas='paused';messages=110;costEvents=100;reservations=10;unknownCharges=1;backup=$false;newWrites=0;intervention=$false;consequential=$false}
 switch($scenario){
  foreign_listener {$s.childListener=999}
  parent_reused {$s.parentStart='P1'}
  child_reused {$s.childStart='C1'}
  consequential_tool {$s.consequential=$true}
  label_disable_failed {$s.labelDisabled=$false}
  orphan_lease {$s.lease='claimed'}
  partial_parent_survives {$s.parent=61760}
  partial_child_survives {$s.child=64728}
  candidate_route_unknown {$s.candidateRoute='UNKNOWN'}
  rollback_route_unknown {$s.candidateRoute='UNKNOWN';$s.rollbackRoute='UNKNOWN'}
  foreign_after_stop {$s.childListener=999}
  ledger_missing {$s.costEvents=99}
  agent_missing {$s.agentIds=6}
  messages_missing {$s.messages=109}
  orphan_new_parent {$s.newParent=777;$s.lease='claimed'}
  restarted_parent {$s.parent=61888}
  stale_http200 {$s.childListener=61889}
 }
 # Fresh-process ancestry and listener must be established before destructive steps.
 Gate ($s.parent -eq 61760 -and $s.child -eq 64728 -and $s.parentStart -eq 'P0' -and $s.childStart -eq 'C0') 'old_pid_start_drift'
 Gate ($s.childListener -eq $s.child) 'global_sole_listener'
 Gate (-not $s.consequential) 'known_consequential_operation_requires_owner_decision'
 $s.labelDisabled=$scenario -ne 'label_disable_failed'
 Gate $s.labelDisabled 'label_disable_not_proven'
 # Detached exact parent termination; every partial failure must STOP without child/new bootstrap.
 if($scenario -ne 'partial_parent_survives'){$s.parent=$null}
 Gate ($null -eq $s.parent) 'old_parent_survives'
 if($scenario -ne 'partial_child_survives'){$s.child=$null;$s.childListener=$null}
 Gate ($null -eq $s.child -and $null -eq $s.childListener) 'old_child_or_listener_survives'
 # backup is made ONLY after both original processes and listener are demonstrably absent.
 $s.backup=$true
 Gate ($s.lease -eq 'absent' -and $null -eq $s.newParent) 'orphan_new_supervisor_or_spawn_claim'
 $s.lease='claimed';$s.newParent=778;$s.newChild=779
 Gate ($s.agentIds -eq 7 -and $s.tulkas -eq 'paused' -and $s.messages -ge 110 -and $s.costEvents -ge 100 -and $s.reservations -ge 10) 'committed_state_regressed'
 if($scenario -eq 'foreign_after_stop'){throw 'STOP:foreign_listener_after_stop'}
 if($s.candidateRoute -ne 'subscription'){
  Gate ($s.rollbackRoute -eq 'subscription') 'candidate_and_rollback_route_unknown_detached_owner'
  # Unknown dispatched/charged request is preserved, not zeroed, replayed, or used to prohibit one contained rollback.
  Gate ($s.unknownCharges -eq 1 -and $s.newWrites -eq 0) 'unknown_charge_or_newer_DB_records_lost'
  return [pscustomobject]@{outcome='contained-rollback';backup=$s.backup;unknownCharges=$s.unknownCharges;agentIds=$s.agentIds;lease=$s.lease}
 }
 return [pscustomobject]@{outcome='candidate';backup=$s.backup;unknownCharges=$s.unknownCharges;agentIds=$s.agentIds;lease=$s.lease}
}
function Check($condition,$name){if(-not $condition){throw "FAIL:$name"};"PASS:$name"}
foreach($scenario in @('normal','candidate_route_unknown')){
 $r=Simulate $scenario
 Check ($r.backup -and $r.agentIds -eq 7 -and $r.unknownCharges -eq 1 -and $r.lease -eq 'claimed') "$scenario preserves backup/agents/unknown charges/claim"
 Check (($scenario -eq 'normal' -and $r.outcome -eq 'candidate') -or ($scenario -eq 'candidate_route_unknown' -and $r.outcome -eq 'contained-rollback')) "$scenario selected outcome"
}
foreach($scenario in @('foreign_listener','parent_reused','child_reused','consequential_tool','label_disable_failed','orphan_lease','partial_parent_survives','partial_child_survives','rollback_route_unknown','foreign_after_stop','ledger_missing','agent_missing','messages_missing','orphan_new_parent','restarted_parent','stale_http200')){
 $failed=$false;try{$null=Simulate $scenario}catch{$failed=$_.Exception.Message -match 'STOP:'}
 Check $failed "$scenario stops without blind retry"
}
$script=[IO.File]::ReadAllText($MyInvocation.MyCommand.Path)
$forbidden=@('Start-Process','Invoke-RestMethod','Invoke-WebRequest','launchctl\s+(?:disable|bootout|enable|bootstrap|kickstart)','kill\s+','SqliteConnection','\bsqlite3\b','/control/stop')
$body=$script.Substring(0,$script.IndexOf('$script=[IO.File]::ReadAllText'))
Check (-not (@($forbidden|Where-Object {$body -match $_}).Count)) 'harness contains no live process/DB/network control'
'PASS offline model only; neither simulator nor this output is live permission'