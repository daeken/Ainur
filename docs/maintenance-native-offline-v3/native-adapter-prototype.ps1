# v3 OFFLINE native adapter prototype. Native process/launchd/route operations deliberately absent.
# Execute ONLY with -FixtureRoot /tmp/ainur-disposable-wal-fixture-* and -NativeRunner path to an
# injectable fake executable. A live process or other path is rejected unconditionally.
param([Parameter(Mandatory)][string]$Operation,[hashtable]$Options=@{},
 [Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][string]$NativeRunner)
$ErrorActionPreference='Stop'
function Fail([string]$reason){throw "INTERVENTION_REQUIRED:$reason"}
$root=[IO.Path]::GetFullPath($FixtureRoot)
if($root -notmatch '^/tmp/ainur-disposable-wal-fixture-[a-f0-9]{32}/?$' -or
 -not [IO.Directory]::Exists($root) -or
 ([IO.File]::GetUnixFileMode($root) -band ([IO.UnixFileMode]::GroupWrite -bor [IO.UnixFileMode]::OtherWrite))){Fail 'fixture restriction'}
$runner=(Resolve-Path -LiteralPath $NativeRunner).Path
if($runner -ne (Join-Path $PSScriptRoot 'fake-native.ps1') -or
 (Get-FileHash $runner).Hash -cne $env:AINUR_OFFLINE_FAKE_SHA){Fail 'native executable not exact injected mock'}
$stopFile=Join-Path $root 'STOP'
if(-not [IO.File]::Exists($stopFile) -or (Get-Content $stopFile -Raw).Trim() -cne $env:AINUR_OFFLINE_STOP){Fail 'STOP token no longer matches'}
function ExecFake([string]$cmd,[hashtable]$cmdOptions=@{}){
 if($cmd -notin @('lsof-global-listeners','launchctl-print','launchctl-disable','launchctl-print-disabled','process-facts','signal-exact','stop-exact','installed-hashes','version-health','route-receipt')){Fail 'native opcode not allowlisted'}
 $val=& $runner -Operation $cmd -Options $cmdOptions -FixtureRoot $root
 if(-not $?){Fail "fake native command failed:$cmd"}
 return $val
}
function Facts(){
 $label=ExecFake 'launchctl-print' @{label='gui/501/com.ainur.supervisor.5181'}
 $proc=ExecFake 'process-facts' @{parent=61760;child=64728}
 $listeners=ExecFake 'lsof-global-listeners' @{port=5181}
 if($label.label -cne 'gui/501/com.ainur.supervisor.5181' -or [int]$label.pid -ne 61760 -or
 $label.started -cne 'P0' -or $proc.parent.started -cne $label.started -or [int]$proc.parent.pid -ne 61760 -or
 $proc.parent.exe -cne '/usr/local/share/dotnet/dotnet' -or [int]$proc.child.pid -ne 64728 -or
 $proc.child.started -cne 'C0' -or [int]$proc.child.ppid -ne 61760 -or
 $proc.child.exe -cne '/usr/local/share/dotnet/dotnet' -or [int]$proc.descendants.Count -ne 1 -or
 [int]$listeners.Count -ne 1 -or [int]$listeners[0].pid -ne 64728 -or
 [int]$listeners[0].fd -le 0 -or $listeners[0].address -cne '127.0.0.1:5181'){
  Fail 'exact label/PID/start/ancestry/exe/sole global listener/fd drift'
 }
 return [pscustomobject]@{label=$label.label;parent=$proc.parent;child=$proc.child;listener=$listeners[0]}
}
switch($Operation){
 'old-facts' {return Facts}
 'disable-label' {
  $null=Facts
  $null=ExecFake 'launchctl-disable' @{label='gui/501/com.ainur.supervisor.5181'}
  $state=ExecFake 'launchctl-print-disabled' @{label='gui/501/com.ainur.supervisor.5181'}
  if($state.label -cne 'gui/501/com.ainur.supervisor.5181' -or $state.disabled -ne $true){Fail 'launchd label disable not authoritative'}
  return $state
 }
 'kill-old-parent-exact' {
  if([int]$Options.pid -ne 61760 -or $Options.started -cne 'P0' -or $Options.signal -cne 'KILL' -or $Options.neverTree -ne $true){Fail 'parent target/flag mismatch'}
  $null=Facts
  $disabled=ExecFake 'launchctl-print-disabled' @{label='gui/501/com.ainur.supervisor.5181'}
  if($disabled.disabled -ne $true){Fail 'label not disabled'}
  $null=ExecFake 'signal-exact' @{pid=61760;started='P0';signal='KILL';neverTree=$true}
  return [pscustomobject]@{signal='KILL';pid=61760;neverTree=$true}
 }
 'preflight-backup' {
  $db=Join-Path $root 'ainur.db';$backup=Join-Path $root 'preflight.db';$receipt=Join-Path $root 'preflight-receipt.json'
  if($Options.backupKind -cne 'online' -or $Options.neverOverwrite -ne $true){Fail 'backup flag mismatch'}
  $out=& $PSScriptRoot/disposable-wal-backup.ps1 -Database $db -Backup $backup -Receipt $receipt
  if(-not $?){Fail 'disposable online WAL backup failed'}
  return $out
 }
 default {Fail "offline native adapter capability missing:$Operation"}
}