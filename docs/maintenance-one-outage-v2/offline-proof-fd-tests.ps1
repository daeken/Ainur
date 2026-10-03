# Disposable /tmp only. Independent single-FD read regression, no Ainur service or OS registration.
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root='/tmp/ainur-route-proof-fd-'+[guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($root)|Out-Null
try {
 $typeFile=Join-Path $PSScriptRoot 'route-proof-fd.cs'
 Add-Type -Path $typeFile -ErrorAction Stop
 $base=Join-Path $root 'proof.json';[IO.File]::WriteAllText($base,'{"route_class":"subscription"}')
 [IO.File]::SetUnixFileMode($base,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
 $sha=(Get-FileHash $base -Algorithm SHA256).Hash
 $ok=[Text.Encoding]::UTF8.GetString([AinurRouteProofFd]::Read($base,$sha))
 if($ok -cne '{"route_class":"subscription"}'){throw 'regular FD read failed'}
 'PASS owned private single-link regular same-FD SHA read'
 function Reject([string]$name,[string]$path,[string]$digest){
  $wasRejected=$false;try{[AinurRouteProofFd]::Read($path,$digest)|Out-Null}catch{$wasRejected=$true}
  if(-not $wasRejected){throw "unsafe $name proof accepted"};"PASS reject $name"
 }
 Reject 'forged digest' $base ('a'*64)
 $fifo=Join-Path $root 'proof.fifo';$null=& /usr/bin/mkfifo $fifo;if($LASTEXITCODE -ne 0){throw 'mkfifo failed'}
 Reject 'FIFO nonblocking' $fifo $sha
 $link=Join-Path $root 'proof.link';$null=& /bin/ln $base $link;if($LASTEXITCODE -ne 0){throw 'hardlink failed'};Reject 'hardlink nlink2' $base $sha
 [IO.File]::Delete($link)
 $symlink=Join-Path $root 'proof.symlink';[IO.File]::CreateSymbolicLink($symlink,$base)|Out-Null;Reject 'symlink nofollow' $symlink $sha
 $replacement=Join-Path $root 'replacement.json';[IO.File]::WriteAllText($replacement,'{"route_class":"UNKNOWN"}')
 [IO.File]::SetUnixFileMode($replacement,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
 [AinurRouteProofFd]::AfterOpenForOfflineTest=[Action[string]]{param($path) [IO.File]::Move($replacement,$path,$true)}
 try {
  $read=[Text.Encoding]::UTF8.GetString([AinurRouteProofFd]::Read($base,$sha))
  if($read -cne $ok){throw 'path replacement after open altered FD bytes'}
  'PASS path replace adversary after verified fstat: FD bytes remain original'
 } finally {[AinurRouteProofFd]::AfterOpenForOfflineTest=$null}
} finally {[IO.Directory]::Delete($root,$true)}
