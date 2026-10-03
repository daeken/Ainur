#requires -Version 7.0
# Native-proof building block. OFFLINE DISPOSABLE FIXTURE ONLY; no launchctl, network or process kill.
param([Parameter(Mandatory)][string]$Database,[Parameter(Mandatory)][string]$Backup,
 [Parameter(Mandatory)][string]$Receipt,[string]$Sqlite='/usr/bin/sqlite3')
$ErrorActionPreference='Stop'
function Fail($reason){throw "INTERVENTION_REQUIRED:$reason"}
if(-not [IO.File]::Exists($Database) -or [IO.File]::Exists($Backup) -or [IO.File]::Exists($Receipt)){Fail 'DB absent or backup/receipt exists (no overwrite)'}
if([IO.Path]::GetFullPath($Backup) -ceq [IO.Path]::GetFullPath($Database)){Fail 'backup cannot equal source'}
if([IO.Path]::GetFullPath($Database) -match '/\.ainur/' -or [IO.Path]::GetFullPath($Database) -notmatch '^/(private/)?tmp/ainur-disposable-wal-fixture-'){Fail 'DISPOSABLE FIXTURE ONLY; never live DB'}
if($Sqlite -cne '/usr/bin/sqlite3' -or -not [IO.File]::Exists($Sqlite)){Fail 'pinned SQLite CLI absent'}
if([IO.File]::GetUnixFileMode($Database) -band ([IO.UnixFileMode]::GroupWrite -bor [IO.UnixFileMode]::OtherWrite)){Fail 'DB writable by others'}
$old=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Database))
if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Backup)) -cne $old){Fail 'backup must be in same disposable fixture directory'}
# sqlite3 .backup uses SQLite online backup API: committed WAL rows become a consistent image.
# Child exit is bounded, no shell, no interpolation of SQL; paths passed as CLI args to native sqlite.
$backupEscaped=($Backup.Replace("'","''"))
$op=".timeout 3000`n.backup '$backupEscaped'`n"
$si=[Diagnostics.ProcessStartInfo]::new($Sqlite)
$si.ArgumentList.Add('-batch');$si.ArgumentList.Add($Database)
$si.RedirectStandardInput=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true;$si.UseShellExecute=$false
$pr=[Diagnostics.Process]::Start($si)
try{
 $pr.StandardInput.Write($op);$pr.StandardInput.Close()
 if(-not $pr.WaitForExit(8000)){
  try{$pr.Kill();$pr.WaitForExit(1000)|Out-Null}catch{}
  Fail 'sqlite online backup timed out'
 }
 $err=$pr.StandardError.ReadToEnd()
 if($pr.ExitCode -ne 0){Fail "sqlite backup failed exit $($pr.ExitCode) (stderr suppressed)"}
}finally{$pr.Dispose()}
if(-not [IO.File]::Exists($Backup)){Fail 'backup not created'}
[IO.File]::SetUnixFileMode($Backup,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
# Integrity, selected persisted keysets, seven identities, paused Tulkas; minimal fixture schema.
$verifySql="PRAGMA integrity_check; PRAGMA user_version; SELECT 'agent|'||id||'|'||state FROM agents ORDER BY id; SELECT 'session|'||id||'|'||state FROM sessions ORDER BY id; SELECT 'session_item|'||id FROM session_items ORDER BY id; SELECT 'model_request|'||id||'|'||state FROM model_requests ORDER BY id; SELECT 'cost|'||id||'|'||cash_basis||'|'||ifnull(cash_nanos,'NULL') FROM cost_events ORDER BY id; SELECT 'reservation|'||id||'|'||state FROM reservations ORDER BY id; SELECT 'notification|'||id||'|'||state FROM notifications ORDER BY id; SELECT 'event|'||id FROM events ORDER BY id;"
function ReadSnapshot([string]$path){
 $si=[Diagnostics.ProcessStartInfo]::new($Sqlite);$si.ArgumentList.Add('-batch');$si.ArgumentList.Add($path);$si.ArgumentList.Add($verifySql)
 $si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true;$si.UseShellExecute=$false
 $pr=[Diagnostics.Process]::Start($si)
 try{
  if(-not $pr.WaitForExit(8000)){
   try{$pr.Kill();$pr.WaitForExit(1000)|Out-Null}catch{}
   Fail 'sqlite snapshot timed out'
  }
  $text=$pr.StandardOutput.ReadToEnd();$err=$pr.StandardError.ReadToEnd()
  if($pr.ExitCode -ne 0){Fail ('sqlite snapshot query error exit '+$pr.ExitCode+' (stderr suppressed)')}
  return $text.TrimEnd().Split("`n")
 }finally{$pr.Dispose()}
}
$before=ReadSnapshot $Database;$after=ReadSnapshot $Backup
if($before.Count -ne $after.Count -or (Compare-Object $before $after).Count -ne 0){Fail 'source/backup committed keyset mismatch'}
if($after[0] -cne 'ok' -or $after[1] -cne '6'){Fail 'integrity/schema mismatch'}
$agents=@($after|Where-Object {$_ -like 'agent|*'})
if($agents.Count -ne 7 -or -not (@($agents|Where-Object {$_ -ceq 'agent|agt_01a0f8479f287ca88498e5af75232ac0|paused'}).Count -eq 1)){Fail 'seven agents/paused Tulkas missing'}
$sha=(Get-FileHash -LiteralPath $Backup).Hash
$fs=[IO.FileStream]::new($Backup,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{$fs.Flush($true)}finally{$fs.Dispose()}
$record=[ordered]@{kind='fixture-sqlite-online-backup';backupSha=$sha;integrity='ok';schema=6;agentIds=7;tulkas='paused';sessionCount=@($after|Where-Object {$_ -like 'session|*'}).Count;modelRequestCount=@($after|Where-Object {$_ -like 'model_request|*'}).Count;notificationCount=@($after|Where-Object {$_ -like 'notification|*'}).Count;eventCount=@($after|Where-Object {$_ -like 'event|*'}).Count;sessionItemCount=@($after|Where-Object {$_ -like 'session_item|*'}).Count;costCount=@($after|Where-Object {$_ -like 'cost|*'}).Count;reservationCount=@($after|Where-Object {$_ -like 'reservation|*'}).Count;sourceAndBackupKeysetEqual=$true;walBacked=[IO.File]::Exists($Database+'-wal')}
$payload=($record|ConvertTo-Json -Compress)+"`n"
$fs=[IO.FileStream]::new($Receipt,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough)
try{$bytes=[Text.Encoding]::UTF8.GetBytes($payload);$fs.Write($bytes,0,$bytes.Length);$fs.Flush($true)}finally{$fs.Dispose()}
[IO.File]::SetUnixFileMode($Receipt,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
$record