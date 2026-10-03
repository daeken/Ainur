param([string]$Operation,[hashtable]$Options,[string]$FixtureRoot)
$ErrorActionPreference='Stop'
$trace=Join-Path $FixtureRoot 'native-trace.jsonl'
[IO.File]::AppendAllText($trace,(ConvertTo-Json -Compress -Depth 5 ([ordered]@{op=$Operation;options=$Options}))+"`n")
$case=$env:AINUR_NATIVE_CASE
if($case -eq 'foreign-listener' -and $Operation -eq 'lsof-global-listeners'){
 return @([pscustomobject]@{pid=64728;fd=4;address='127.0.0.1:5181'},[pscustomobject]@{pid=999;fd=5;address='127.0.0.1:5181'})
}
if($case -eq 'wrong-parent-start' -and $Operation -eq 'process-facts'){
 return [pscustomobject]@{parent=@{pid=61760;started='P999';exe='/usr/local/share/dotnet/dotnet'};child=@{pid=64728;started='C0';ppid=61760;exe='/usr/local/share/dotnet/dotnet'};descendants=@(64728)}
}
if($case -eq 'disabled-failed' -and $Operation -eq 'launchctl-print-disabled'){return [pscustomobject]@{label='gui/501/com.ainur.supervisor.5181';disabled=$false}}
if($case -eq 'wrong-child-ancestry' -and $Operation -eq 'process-facts'){
 return [pscustomobject]@{parent=@{pid=61760;started='P0';exe='/usr/local/share/dotnet/dotnet'};child=@{pid=64728;started='C0';ppid=999;exe='/usr/local/share/dotnet/dotnet'};descendants=@(64728)}
}
switch($Operation){
 'launchctl-print' {return [pscustomobject]@{label='gui/501/com.ainur.supervisor.5181';pid=61760;started='P0'}}
 'process-facts' {return [pscustomobject]@{parent=@{pid=61760;started='P0';exe='/usr/local/share/dotnet/dotnet'};child=@{pid=64728;started='C0';ppid=61760;exe='/usr/local/share/dotnet/dotnet'};descendants=@(64728)}}
 'lsof-global-listeners' {return @([pscustomobject]@{pid=64728;fd=4;address='127.0.0.1:5181'})}
 'launchctl-disable' {return [pscustomobject]@{label='gui/501/com.ainur.supervisor.5181';attempted=$true}}
 'launchctl-print-disabled' {return [pscustomobject]@{label='gui/501/com.ainur.supervisor.5181';disabled=$true}}
 'signal-exact' {return [pscustomobject]@{pid=61760;signal='KILL';targeted=$true}}
 default {throw "unimplemented fake operation:$Operation"}
}