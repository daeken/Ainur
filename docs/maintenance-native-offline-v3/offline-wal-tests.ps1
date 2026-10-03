$ErrorActionPreference='Stop'
$fixture=Join-Path '/tmp' ('ainur-disposable-wal-fixture-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture)|Out-Null
$source=Join-Path $fixture 'ainur.db';$backup=Join-Path $fixture 'backup.db';$receipt=Join-Path $fixture 'backup-receipt.json'
$db='/usr/bin/sqlite3'
$ids=@('agt_01a0f814b38b794e8b81fe32dea9645d','agt_01a0fda60697713a9e88d6663da1a104','agt_01a0f81760187d9bb07007727f53f57c','agt_01a0f817601b7e2eb485890de912d974','agt_01a0f8479f287fa1bd399291dadc71f9','agt_01a0f8479f2b7cff9f45a1f36d2ab242','agt_01a0f8479f2c76b5863328898763faef')
# Fifth ID is intentionally replaced below with the real paused Tulkas ID.
$ids[4]='agt_01a0f8479f287ca88498e5af75232ac0'
$schema="PRAGMA journal_mode=WAL;PRAGMA user_version=6;CREATE TABLE agents(id TEXT PRIMARY KEY,state TEXT NOT NULL);CREATE TABLE messages(id TEXT PRIMARY KEY);CREATE TABLE cost_events(id TEXT PRIMARY KEY);CREATE TABLE reservations(id TEXT PRIMARY KEY);"
$null=& $db $source $schema
if($LASTEXITCODE -ne 0){throw 'fixture sqlite schema failed'}
foreach($id in $ids){$state=if($id -eq 'agt_01a0f8479f287ca88498e5af75232ac0'){'paused'}else{'active'};$null=& $db $source "INSERT INTO agents VALUES('$id','$state');";if($LASTEXITCODE -ne 0){throw 'fixture agent failed'}}
# Separate sqlite WAL writer retains transaction committed in WAL while snapshot occurs.
$si=[Diagnostics.ProcessStartInfo]::new($db);$si.ArgumentList.Add('-batch');$si.ArgumentList.Add($source);$si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true;$si.UseShellExecute=$false
$writer=[Diagnostics.Process]::Start($si)
try{
 $writer.StandardInput.WriteLine('PRAGMA wal_autocheckpoint=0;');$writer.StandardInput.WriteLine('BEGIN;');$writer.StandardInput.WriteLine("INSERT INTO messages VALUES('committed-wal-message');");$writer.StandardInput.WriteLine("INSERT INTO cost_events VALUES('unknown-cost');");$writer.StandardInput.WriteLine("INSERT INTO reservations VALUES('hold');");$writer.StandardInput.WriteLine('COMMIT;');$writer.StandardInput.Flush()
 Start-Sleep -Milliseconds 200
 if(-not [IO.File]::Exists($source+'-wal')){throw 'fixture WAL not present during backup'}
 $o=& dotnet /Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev/supervisor/bootstrap/host/HandoffHost.dll (Join-Path $PSScriptRoot 'disposable-wal-backup.ps1') -Database $source -Backup $backup -Receipt $receipt 2>&1
 $exit=$LASTEXITCODE
 if($exit -ne 0){throw "backup failed exit=$exit $($o -join ' ')"}
 $r=Get-Content $receipt -Raw|ConvertFrom-Json
 if($r.messageCount -ne 1 -or $r.costCount -ne 1 -or $r.reservationCount -ne 1 -or -not $r.walBacked -or $r.agentIds -ne 7 -or $r.tulkas -ne 'paused'){throw 'backup receipt fails WAL/ledger/seven proof'}
 $brows=& $db $backup "SELECT id FROM messages UNION ALL SELECT id FROM cost_events UNION ALL SELECT id FROM reservations ORDER BY id;"
 if((@($brows|Sort-Object) -join ',') -cne 'committed-wal-message,hold,unknown-cost'){throw 'backup committed WAL rows absent'}
 "PASS actual SQLite online WAL backup committed message+unknown cost+hold, 7 agents paused Tulkas, integrity/schema/hash; receipt=$receipt"
 $again=@(& dotnet /Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev/supervisor/bootstrap/host/HandoffHost.dll (Join-Path $PSScriptRoot 'disposable-wal-backup.ps1') -Database $source -Backup $backup -Receipt $receipt 2>&1)
 if($LASTEXITCODE -eq 0){throw 'no-overwrite retry incorrectly succeeded'}
 'PASS backup/receipt never overwrite on retry'
}finally{
 $writer.StandardInput.Close();if(-not $writer.WaitForExit(2000)){try{$writer.Kill();$writer.WaitForExit(1000)|Out-Null}catch{}}
 $writer.Dispose()
}
$global:LASTEXITCODE=0