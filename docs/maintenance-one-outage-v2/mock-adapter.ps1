param([string]$Operation,[hashtable]$Options)
$ErrorActionPreference='Stop'
$scenario=$env:AINUR_MAINT_MOCK_CASE
$trace=$env:AINUR_MAINT_MOCK_TRACE
$mk=[ordered]@{op=$Operation;options=$Options;scenario=$scenario}
$fs=[IO.FileStream]::new($trace,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::Read)
try{$b=[Text.Encoding]::UTF8.GetBytes(($mk|ConvertTo-Json -Depth 5 -Compress)+"`n");$fs.Write($b,0,$b.Length);$fs.Flush($true)}finally{$fs.Dispose()}
if($scenario -eq 'crash-at-parent' -and $Operation -eq 'kill-old-parent-exact'){throw 'MOCK:injected parent failure'}
if($scenario -eq 'disable-failed' -and $Operation -eq 'disabled-status'){return [pscustomobject]@{label='gui/501/com.ainur.supervisor.5181';disabled=$false}}
if($scenario -eq 'second-parent' -and $Operation -eq 'after-parent-facts'){return [pscustomobject]@{oldParentGone=$true;newParentAbsent=$false;child=@{pid=64728;started='C0'};listener=@{pid=64728;count=1};disabled=$true}}
if($scenario -eq 'orphan-lease' -and $Operation -eq 'new-spawn-status'){return [pscustomobject]@{absent=$false;parentAbsent=$true;listenerAbsent=$true}}
if($scenario -eq 'bad-core-sha' -and $Operation -eq 'route-attestation'){return [pscustomobject]@{route_class='subscription';process_id=$Options.pid;process_started_utc=$Options.started;release=$Options.release;generation=$Options.generation;core_sha256=('0'*64);provider_policy='openai_subscription_strict';independentProofSha=('a'*64)}}
if($scenario -eq 'bad-route' -and $Operation -eq 'route-attestation'){return [pscustomobject]@{route_class='UNKNOWN';process_id=$Options.pid;process_started_utc=$Options.started;release=$Options.release;generation=$Options.generation;sourceHash='';provider_policy='openai_subscription_strict';independentProofSha=('a'*64)}}
if($scenario -eq 'final-pid-drift' -and $Operation -eq 'final-facts'){return [pscustomobject]@{pid=999;started='P999';release='r20261002154643-01d6c043e2';soleListener=$false;routeProofStillCurrent=$false}}
if($scenario -eq 'backup-failed' -and $Operation -eq 'preflight-backup'){return [pscustomobject]@{backupSha='bad';backupLocation='';agentIds=@();schema=6}}
if($scenario -eq 'child-survives' -and $Operation -eq 'after-child-facts'){return [pscustomobject]@{oldChildGone=$false;stillExact=$true;knownConsequential=$true;ownerApprovedExactKill=$false;oldParentGone=$true;listenerAbsent=$false;newParentAbsent=$true;disabled=$true}}
if($scenario -eq 'install-failed' -and $Operation -eq 'install-protected'){return [pscustomobject]@{sourceSha='wrong';initialRelease='';coreHash='';subscriptionPin=$false;sameHomePort=$true;rollbackPreserved=$true}}
$ids=@('agt_01a0f814b38b794e8b81fe32dea9645d','agt_01a0fda60697713a9e88d6663da1a104','agt_01a0f81760187d9bb07007727f53f57c','agt_01a0f817601b7e2eb485890de912d974','agt_01a0f8479f287ca88498e5af75232ac0','agt_01a0f8479f2b7cff9f45a1f36d2ab242','agt_01a0f8479f2c76b5863328898763faef')
$counts=@{messages=111;sessions=7;notifications=51;events=101;cost_events=101;reservations=11;tool_invocations=101;model_requests=101}
switch($Operation){
 'old-facts' {return [pscustomobject]@{label='gui/501/com.ainur.supervisor.5181';parent=@{pid=61760;started='P0';family='dotnet-supervisor'};child=@{pid=64728;started='C0';ppid=61760;family='dotnet-server'};listener=@{pid=64728;count=1;endpoint='127.0.0.1:5181'};descendantCount=1;release='r20261001190802-e022406443';schema=6}}
 'preflight-backup' {return [pscustomobject]@{backupSha=('a'*64);backupLocation='/tmp/mock-preflight.db';walBacked=$true;integrity=$true;committedKeysPreserved=$true;agentIds=$ids;tulkasState='paused';schema=6;counts=$counts}}
 'stopped-backup' {return [pscustomobject]@{backupSha=('b'*64);backupLocation='/tmp/mock-stopped.db';walBacked=$true;originalDbPreserved=$true;integrity=$true;committedKeysPreserved=$true;agentIds=$ids;tulkasState='paused';schema=6;counts=$counts}}
 'work-classification' {return [pscustomobject]@{knownConsequential=$false;approval='approved';unknownChargePreserved=$true}}
 'drain' {return [pscustomobject]@{drained=$false;residual='interrupted-turn-risk-approved'}}
 'disabled-status' {return [pscustomobject]@{label='gui/501/com.ainur.supervisor.5181';disabled=$true}}
 'after-parent-facts' {return [pscustomobject]@{oldParentGone=$true;newParentAbsent=$true;child=@{pid=64728;started='C0'};listener=@{pid=64728;count=1};disabled=$true}}
 'after-child-facts' {return [pscustomobject]@{oldChildGone=$true;oldParentGone=$true;listenerAbsent=$true;newParentAbsent=$true;disabled=$true}}
 'new-spawn-status' {return [pscustomobject]@{absent=$true;parentAbsent=$true;listenerAbsent=$true}}
 'install-protected' {return [pscustomobject]@{sourceSha='254065c008e0ef61565971ebccfa11462c262e01';initialRelease='r20261002014730-766fa9f930';coreHash='787DD2BB636726472F91689957C283851386E2C0B50C85E45A0E643E75DA59A3';subscriptionPin=$true;sameHomePort=$true;rollbackPreserved=$true}}
 'new-facts' {
 $t=Get-Content $trace|Where-Object {$_ -match '"op":"activate-candidate"'}
 if(@($t).Count -gt 0 -and $scenario -ne 'rolled-back'){$r='r20261002154643-01d6c043e2';$h='1CD0AE8D1E2BEE1BCC9DA30C484B04284B73257712BC7C4F737EAF338AF364E3';$childPid=701;$gen=10}
 else{$r='r20261002014730-766fa9f930';$h='787DD2BB636726472F91689957C283851386E2C0B50C85E45A0E643E75DA59A3';$childPid=700;$gen=9}
 return [pscustomobject]@{label='gui/501/com.ainur.supervisor.5181';parent=@{pid=699;family='dotnet-supervisor'};child=@{pid=$childPid;started="N$childPid";ppid=699;family='dotnet-server'};listener=@{pid=$childPid;count=1;endpoint='127.0.0.1:5181'};descendantCount=1;release=$r;schema=6;sourceHash=$h;generation=$gen}
 }
 'route-attestation' {return [pscustomobject]@{route_class='subscription';process_id=$Options.pid;process_started_utc=$Options.started;release=$Options.release;generation=$Options.generation;core_sha256=$(if($Options.release -eq 'r20261002154643-01d6c043e2'){'1CD0AE8D1E2BEE1BCC9DA30C484B04284B73257712BC7C4F737EAF338AF364E3'}else{'787DD2BB636726472F91689957C283851386E2C0B50C85E45A0E643E75DA59A3'});provider_policy='openai_subscription_strict';independentProofSha=('a'*64)}}
 'upgrade-status' {return [pscustomobject]@{candidate='r20261002154643-01d6c043e2';previous='r20261002014730-766fa9f930';state=$(if($scenario -eq 'rolled-back'){'rolled_back'}else{'succeeded'})}}
 'final-facts' {return [pscustomobject]@{pid=$(if($scenario -eq 'rolled-back'){700}else{701});started=$(if($scenario -eq 'rolled-back'){'N700'}else{'N701'});release=$(if($scenario -eq 'rolled-back'){'r20261002014730-766fa9f930'}else{'r20261002154643-01d6c043e2'});soleListener=$true;routeProofStillCurrent=$true}}
 'snapshot-compare' {return [pscustomobject]@{agentIds=$ids;tulkasState='paused';schema=6;counts=$counts;integrity=$true;committedKeysPreserved=$true;committedBaselinePreserved=$true;unknownChargesPreserved=$true;noOverwrite=$true;backupUnchanged=$true}}
 default {return [pscustomobject]@{ok=$true}}
}