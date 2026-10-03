# Fixed-purpose guard probe tests. Only disposable Python stdlib loopback dummies.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$guard = Join-Path $PSScriptRoot 'route-receipt-scratch-guard.ps1'
$fixture = "/tmp/ainur-rr-fixed-fixture-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($fixture) | Out-Null
[IO.File]::SetUnixFileMode($fixture,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
$core = Join-Path $fixture 'Ainur.Core.dll'
[IO.File]::WriteAllText($core,'offline dummy not a managed assembly',[Text.UTF8Encoding]::new($false))
$coreHash = (Get-FileHash $core -Algorithm SHA256).Hash
$server = Join-Path $fixture 'Ainur.Server.dll'
[IO.File]::WriteAllText($server,'offline dummy server assembly',[Text.UTF8Encoding]::new($false))
$serverHash = (Get-FileHash $server -Algorithm SHA256).Hash
$dummy = Join-Path $fixture 'dummy'
$script = @'
#!/usr/bin/env python3
import hashlib, json, os, sys, time
from http.server import BaseHTTPRequestHandler, HTTPServer
assert len(sys.argv)==7 and sys.argv[1]=='--home' and sys.argv[3]=='--port' and sys.argv[5]=='--release'
assert os.environ['AINUR_VALIDATION']=='1' and os.environ['AINUR_OPENAI_ROUTE']=='subscription'
home, port, release, mode = sys.argv[2], int(sys.argv[4]), sys.argv[6], 'CASE_MODE'
assert home.startswith('/tmp/ainur-rr-guard-') and home.endswith('/home')
key_path=os.path.join(home,'supervisor','receipt-secrets','route-receipt.key')
assert os.stat(key_path).st_mode & 0o777 == 0o600
with open(key_path,'r') as f: key=f.read().strip()
from datetime import datetime, timezone
started=datetime.now(timezone.utc).isoformat().replace('+00:00','Z')
class Handler(BaseHTTPRequestHandler):
 def log_message(self,*args): pass
 def do_GET(self):
  if mode=='slow': time.sleep(30)
  if mode=='handoff' and self.path=='/api/v1/control/route-receipt': time.sleep(30)
  if self.path=='/api/v1/version':
   payload={'release':release,'generation':8,'schema':6}
  elif self.path=='/api/v1/control/route-receipt' and self.headers.get('Authorization')=='Bearer '+key:
   payload={'route_class':'api' if mode=='bad-route' else 'subscription','provider_policy':'openai_subscription_strict','release':release,'generation':8,'process_id':os.getpid(),'process_started_utc':started,'core_sha256':hashlib.sha256(open(os.path.join(os.path.dirname(__file__),'Ainur.Core.dll'),'rb').read()).hexdigest().upper()}
  else:
   self.send_error(403); return
  data=json.dumps(payload).encode()
  self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(data))); self.end_headers(); self.wfile.write(data)
HTTPServer(('127.0.0.1',port),Handler).serve_forever()
'@
[IO.File]::WriteAllText($dummy,$script+"`n",[Text.UTF8Encoding]::new($false))
[IO.File]::SetUnixFileMode($dummy,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
[IO.File]::WriteAllText((Join-Path $fixture 'release.json'),'{'+'"id":"rdummy1"'+'}',[Text.UTF8Encoding]::new($false))
$cases = @(
 @{ name='positive-receipt'; mode='valid'; expected='finished'; valid=$true },
 @{ name='bad-route'; mode='bad-route'; expected='RECEIPT_ROUTE_MISMATCH'; valid=$true },
 @{ name='hung-http'; mode='slow'; expected='RECEIPT_TIMEOUT'; valid=$true },
 @{ name='handoff-hung-receipt'; mode='handoff'; expected='SETUP_FAILED'; valid=$true },
 @{ name='bad-core'; mode='valid'; expected='CORE_MISMATCH'; valid=$false }
)
foreach($case in $cases) {
 [IO.File]::WriteAllText($dummy,$script.Replace('CASE_MODE',$case.mode)+"`n",[Text.UTF8Encoding]::new($false))
 $hash = (Get-FileHash $dummy -Algorithm SHA256).Hash
 $root="/tmp/ainur-rr-guard-fixed-$($case.name)-$([Guid]::NewGuid().ToString('N'))"
 $evidence="/tmp/ainur-rr-fixed-evidence-$([Guid]::NewGuid().ToString('N')).json"
 [IO.File]::WriteAllText($evidence,'{}',[Text.UTF8Encoding]::new($false))
 $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
 $listener.Start();$port=([Net.IPEndPoint]$listener.LocalEndpoint).Port;$listener.Stop()
 $timer=[Diagnostics.Stopwatch]::StartNew()
 try {
  $null=& $guard -TempRoot $root -Executable $dummy -ReleaseDirectory $fixture -ExpectedExecutableSha256 $hash -ExpectedCoreSha256 $(if($case.valid){$coreHash}else{'0'*64}) -ExpectedServerSha256 $serverHash -ExpectedReleaseId 'rdummy1' -Port $port -EvidencePath $evidence
 } catch {}
 $timer.Stop()
 $record=Get-Content $evidence -Raw | ConvertFrom-Json
 if($record.stage -cne $case.expected -and $record.error_code -cne $case.expected) {throw "CASE_FAIL $($case.name) observed=$($record.error_code) stage=$($record.stage)"}
 if($timer.Elapsed.TotalSeconds -gt 12){throw "UNBOUNDED $($case.name) seconds=$($timer.Elapsed.TotalSeconds)"}
 if($case.valid -and (!$record.stopped -or !$record.process_id -or !$record.process_started_utc)){throw "CHILD_UNIDENTIFIED_OR_LIVE $($case.name)"}
 if(!$case.valid -and $record.process_id){throw 'BAD_CORE_DISPATCHED'}
 if($case.name -eq 'positive-receipt' -and ($record.route_class -cne 'subscription' -or $record.provider_policy -cne 'openai_subscription_strict' -or $record.core_sha256 -cne $coreHash -or $record.auth_negative -cne 'absent-invalid-query-forbidden')){throw 'POSITIVE_RECEIPT_MISSING'}
 "PASS $($case.name) $($record.stage) $($record.error_code) seconds=$([Math]::Round($timer.Elapsed.TotalSeconds,2))"
}
'PASSED=4'
