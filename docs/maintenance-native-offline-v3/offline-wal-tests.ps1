$ErrorActionPreference='Stop'
$fixture=Join-Path '/tmp' ('ainur-disposable-wal-fixture-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture)|Out-Null
$source=Join-Path $fixture 'ainur.db';$backup=Join-Path $fixture 'backup.db';$receipt=Join-Path $fixture 'backup-receipt.json'
$db='/usr/bin/sqlite3'
$ids=@('agt_01a0f814b38b794e8b81fe32dea9645d','agt_01a0fda60697713a9e88d6663da1a104','agt_01a0f81760187d9bb07007727f53f57c','agt_01a0f817601b7e2eb485890de912d974','agt_01a0f8479f287fa1bd399291dadc71f9','agt_01a0f8479f2b7cff9f45a1f36d2ab242','agt_01a0f8479f2c76b5863328898763faef')
# Fifth ID is intentionally replaced below with the real paused Tulkas ID.
$ids[4]='agt_01a0f8479f287ca88498e5af75232ac0'
$sourceMigration=Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))) 'src/Ainur.Core/Persistence/Migrations.cs'
$expectedMigrationSha='0422A5A327000E1B95CA9B02F6A6E9997B773C1847781F3FB17BF0A185E1821C'
if((Get-FileHash $sourceMigration -Algorithm SHA256).Hash -cne $expectedMigrationSha){throw 'checked-in migration source hash drift'}
$code=[IO.File]::ReadAllText($sourceMigration)
$migrations=[regex]::Matches($code,'\((\d+),\s*"([^"]+)",\s*"""([\s\S]*?)"""\)',[Text.RegularExpressions.RegexOptions]::Singleline)
if($migrations.Count -lt 6 -or @($migrations|Where-Object {[int]$_.Groups[1].Value -le 6}).Count -ne 6){throw 'checked-in migration parse failed'}
$schemaSql="PRAGMA journal_mode=WAL;`n"+(($migrations|Where-Object {[int]$_.Groups[1].Value -le 6}|ForEach-Object {$_.Groups[3].Value+"`nPRAGMA user_version=$($_.Groups[1].Value);`n"}) -join "`n")
$sqlPath=Join-Path $fixture 'checked-in-migrations.sql'
[IO.File]::WriteAllText($sqlPath,$schemaSql)
$null=& $db $source ".read $sqlPath"
if($LASTEXITCODE -ne 0){throw 'checked-in migrations SQLite fixture failed'}
$base="INSERT INTO projects(id,name,created_at,updated_at) VALUES('project-fixture','fixture',0,0);"
$null=& $db $source $base
if($LASTEXITCODE -ne 0){throw 'fixture project failed'}
foreach($id in $ids){$state=if($id -eq 'agt_01a0f8479f287ca88498e5af75232ac0'){'paused'}else{'active'};$null=& $db $source "INSERT INTO agents(id,project_id,name,role,lifetime,model_id,state,compaction_mode,created_at,updated_at) VALUES('$id','project-fixture','fixture','specialist','persistent','gpt-6-sol','$state','rolling',0,0);";if($LASTEXITCODE -ne 0){throw 'fixture agent failed'}}
$null=& $db $source "INSERT INTO sessions(id,project_id,agent_id,kind,state,model_id,compaction_mode,created_at,updated_at) VALUES('session-fixture','project-fixture','$($ids[0])','primary','idle','gpt-6-sol','rolling',0,0);"
if($LASTEXITCODE -ne 0){throw 'fixture session failed'}
# Separate sqlite WAL writer retains transaction committed in WAL while snapshot occurs.
$si=[Diagnostics.ProcessStartInfo]::new($db);$si.ArgumentList.Add('-batch');$si.ArgumentList.Add($source);$si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true;$si.UseShellExecute=$false
$writer=[Diagnostics.Process]::Start($si)
try{
 $writer.StandardInput.WriteLine('PRAGMA wal_autocheckpoint=0;');$writer.StandardInput.WriteLine('BEGIN;');$writer.StandardInput.WriteLine("INSERT INTO session_items(id,session_id,seq,kind,turn,payload,token_estimate,created_at) VALUES('committed-wal-item','session-fixture',1,'user',1,'committed WAL',3,1);");$writer.StandardInput.WriteLine("INSERT INTO cost_events(id,project_id,category,cash_nanos,cash_basis,effective_nanos,created_at) VALUES('unknown-cost','project-fixture','direct',NULL,'unknown',42,1);");$writer.StandardInput.WriteLine("INSERT INTO reservations(id,project_id,effective_nanos,cash_nanos,state,created_at) VALUES('hold','project-fixture',42,42,'held',1);");$writer.StandardInput.WriteLine("INSERT INTO model_requests(id,project_id,session_id,agent_id,purpose,model_id,provider,upstream_model,state,quote,started_at) VALUES('model-unknown','project-fixture','session-fixture','$($ids[0])','turn','gpt-6-sol','openai','gpt-6-sol','unknown','{}',1);");$writer.StandardInput.WriteLine("INSERT INTO notifications(id,project_id,type,to_agent_id,body,wakes,state,created_at) VALUES('pending-notification','project-fixture','decision','$($ids[0])','fixture pending',1,'pending',1);");$writer.StandardInput.WriteLine('COMMIT;');$writer.StandardInput.Flush()
 Start-Sleep -Milliseconds 200
 if(-not [IO.File]::Exists($source+'-wal')){throw 'fixture WAL not present during backup'}
 $o=& dotnet /Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev/supervisor/bootstrap/host/HandoffHost.dll (Join-Path $PSScriptRoot 'disposable-wal-backup.ps1') -Database $source -Backup $backup -Receipt $receipt 2>&1
 $exit=$LASTEXITCODE
 if($exit -ne 0){throw "backup failed exit=$exit $($o -join ' ')"}
 $r=Get-Content $receipt -Raw|ConvertFrom-Json
 if($r.sessionItemCount -ne 1 -or $r.costCount -ne 1 -or $r.reservationCount -ne 1 -or $r.modelRequestCount -ne 1 -or $r.notificationCount -ne 1 -or $r.sessionCount -ne 1 -or -not $r.walBacked -or $r.agentIds -ne 7 -or $r.tulkas -ne 'paused'){throw 'backup receipt fails WAL/ledger/seven proof'}
 $brows=& $db $backup "SELECT id FROM session_items UNION ALL SELECT id FROM cost_events UNION ALL SELECT id FROM reservations ORDER BY id;"
 if((@($brows|Sort-Object) -join ',') -cne 'committed-wal-item,hold,unknown-cost'){throw 'backup committed WAL rows absent'}
 $s=& $db $backup "SELECT state FROM model_requests WHERE id='model-unknown';SELECT cash_basis||':'||ifnull(cash_nanos,'NULL') FROM cost_events WHERE id='unknown-cost';SELECT state FROM reservations WHERE id='hold';SELECT state FROM notifications WHERE id='pending-notification';"
 if((@($s) -join ',') -cne 'unknown,unknown:NULL,held,pending'){throw 'backup lost unknown charge/held reservation/pending wake'}
 "PASS actual SQLite online WAL backup committed session_item+unknown cost+hold, 7 agents paused Tulkas, integrity/schema/hash; receipt=$receipt"
 $again=@(& dotnet /Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev/supervisor/bootstrap/host/HandoffHost.dll (Join-Path $PSScriptRoot 'disposable-wal-backup.ps1') -Database $source -Backup $backup -Receipt $receipt 2>&1)
 if($LASTEXITCODE -eq 0){throw 'no-overwrite retry incorrectly succeeded'}
 'PASS backup/receipt never overwrite on retry'
 # Stop writer, checkpointed DB remains physically intact; second stopped-consistent backup never overwrites online image.
 $writer.StandardInput.Close();if(-not $writer.WaitForExit(2000)){throw 'fixture writer would not stop'}
 $stopped=Join-Path $fixture 'stopped.db';$stoppedReceipt=Join-Path $fixture 'stopped-receipt.json'
 $stopOut=& dotnet /Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev/supervisor/bootstrap/host/HandoffHost.dll (Join-Path $PSScriptRoot 'disposable-wal-backup.ps1') -Database $source -Backup $stopped -Receipt $stoppedReceipt 2>&1
 if($LASTEXITCODE -ne 0){throw "stopped backup failed $($stopOut -join ' ')"}
 $sr=Get-Content $stoppedReceipt -Raw|ConvertFrom-Json
 if($sr.sessionItemCount -ne $r.sessionItemCount -or $sr.costCount -ne $r.costCount -or $sr.reservationCount -ne $r.reservationCount -or $sr.agentIds -ne 7){throw 'stopped backup changed keysets'}
 'PASS stopped consistent SQLite backup after writer termination preserves ledger/session_items/seven IDs'
}finally{
 $writer.StandardInput.Close();if(-not $writer.WaitForExit(2000)){try{$writer.Kill();$writer.WaitForExit(1000)|Out-Null}catch{}}
 $writer.Dispose()
}
$global:LASTEXITCODE=0