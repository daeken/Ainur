# Disposable *offline* receipt process guard. The caller must supply a reviewed process fixture.
# Nothing under the live service home or user home is opened, changed, or removed.
[CmdletBinding()]
param(
	[Parameter(Mandatory)] [string]$TempRoot,
	[Parameter(Mandatory)] [string]$Executable,
	[Parameter(Mandatory)] [string]$ReleaseDirectory,
	[Parameter(Mandatory)] [string]$ExpectedExecutableSha256,
	[Parameter(Mandatory)] [string]$ExpectedReleaseId,
	[Parameter(Mandatory)] [int]$Port,
	[Parameter(Mandatory)] [string]$EvidencePath,
	[Parameter()] [string[]]$ChildArguments = @(),
	# Reviewed callbacks run inside the guard's ownership window. Never return secrets.
	[Parameter()] [scriptblock]$BeforeStart,
	[Parameter()] [scriptblock]$WhileRunning
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$process = $null
$record = [ordered]@{ stage = 'setup'; created = $false; process_id = $null; process_started_utc = $null; process_executable = $null; port = $Port; exit_code = $null; stopped = $false; scratch_root = $null; error_code = $null }

function Assert-NoSymbolicComponents([string]$FullPath) {
	$path = [IO.Path]::GetFullPath($FullPath)
	$probe = $path
	while($probe) {
		if($probe -ceq '/tmp') { $probe = '/private/tmp'; continue }
		if([IO.File]::Exists($probe) -or [IO.Directory]::Exists($probe)) {
			$item = Get-Item -LiteralPath $probe -Force -ErrorAction Stop
			if($item.LinkTarget -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'SYMLINK_COMPONENT' }
		}
		$next = [IO.Path]::GetDirectoryName($probe)
		if(!$next -or $next -ceq $probe) { break }
		$probe = $next
	}
}

function Assert-ScratchHome([string]$Value, [string]$Root) {
	$full = [IO.Path]::GetFullPath($Value)
	$expected = Join-Path $Root 'home'
	if($full -cne $expected -or !$full.StartsWith('/tmp/ainur-rr-guard-', [StringComparison]::Ordinal)) { throw 'UNSAFE_HOME' }
	if([IO.Directory]::Exists($full)) {
		foreach($part in @($full, $Root)) {
			$item = Get-Item -LiteralPath $part -Force
			if($item.LinkTarget -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'SYMLINK_COMPONENT' }
		}
	}
	if(![IO.Directory]::Exists($full)) { throw 'MISSING_HOME' }
	$mode = [IO.File]::GetUnixFileMode($full)
	if($mode -ne ([IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)) { throw 'HOME_NOT_0700' }
}

try {
	if(![IO.Path]::IsPathFullyQualified($TempRoot) -or ![IO.Path]::IsPathFullyQualified($ReleaseDirectory) -or
		![IO.Path]::IsPathFullyQualified($Executable) -or ![IO.Path]::IsPathFullyQualified($EvidencePath)) { throw 'RELATIVE_PATH' }
	$root = [IO.Path]::GetFullPath($TempRoot)
	$release = [IO.Path]::GetFullPath($ReleaseDirectory)
	$exe = [IO.Path]::GetFullPath($Executable)
	$evidence = [IO.Path]::GetFullPath($EvidencePath)
	$record.scratch_root = $root
	if(!$root.StartsWith('/tmp/ainur-rr-guard-', [StringComparison]::Ordinal) -or
		[IO.Path]::GetFileName($root) -notmatch '^ainur-rr-guard-[A-Za-z0-9-]+$' -or
		[IO.Path]::GetDirectoryName($root) -cne '/tmp' -or
		$root -in @('/tmp','/Users/daeken','/Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev')) { throw 'UNSAFE_ROOT' }
	if($Port -lt 1 -or $Port -gt 65535) { throw 'INVALID_PORT' }
	if($ExpectedExecutableSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $ExpectedReleaseId -notmatch '^r[A-Za-z0-9-]+$') { throw 'INVALID_PIN' }
	if($evidence.StartsWith($root + '/', [StringComparison]::Ordinal) -or ![IO.Directory]::Exists([IO.Path]::GetDirectoryName($evidence))) { throw 'UNSAFE_EVIDENCE' }
	if(![IO.File]::Exists($evidence) -or [IO.Path]::GetDirectoryName($evidence) -cne '/tmp') { throw 'MISSING_EVIDENCE' }
	# macOS /tmp is the well-known system symlink to /private/tmp; pin it exactly.
	if([IO.Path]::GetFullPath((Get-Item -LiteralPath '/tmp' -Force).ResolvedTarget) -cne '/private/tmp') { throw 'UNSAFE_TEMP_ALIAS' }
	Assert-NoSymbolicComponents '/private/tmp'
	if((Get-Item -LiteralPath $evidence -Force).LinkTarget) { throw 'UNSAFE_EVIDENCE' }
	Assert-NoSymbolicComponents $release
	Assert-NoSymbolicComponents $exe
	if((Get-Item -LiteralPath $evidence -Force).LinkTarget) { throw 'UNSAFE_EVIDENCE' }
	if((Get-Item -LiteralPath $evidence -Force).Length -ne 2) { throw 'EVIDENCE_NOT_EMPTY' }
	if([IO.Directory]::Exists($root) -or [IO.File]::Exists($root) -or (Test-Path -LiteralPath $root -PathType Any)) { throw 'STALE_ROOT' }
	if(![IO.Directory]::Exists($release) -or ![IO.File]::Exists($exe) -or [IO.Path]::GetDirectoryName($exe) -cne $release) { throw 'MISSING_RELEASE' }
	Assert-NoSymbolicComponents (Join-Path $release 'release.json')
	$releaseId = (Get-Content -LiteralPath (Join-Path $release 'release.json') -Raw -ErrorAction Stop | ConvertFrom-Json).id
	if($releaseId -cne $ExpectedReleaseId) { throw 'RELEASE_MISMATCH' }
	if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -cne $ExpectedExecutableSha256.ToUpperInvariant()) { throw 'EXECUTABLE_MISMATCH' }
	[IO.Directory]::CreateDirectory($root) | Out-Null
	$record.created = $true
	[IO.File]::SetUnixFileMode($root,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
	$marker = Join-Path $root '.scratch-owner'
	$nonce = [Guid]::NewGuid().ToString('N')
	[IO.File]::WriteAllText($marker,$nonce,[Text.UTF8Encoding]::new($false))
	[IO.File]::SetUnixFileMode($marker,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
	$scratchHome = Join-Path $root 'home'
	[IO.Directory]::CreateDirectory($scratchHome) | Out-Null
	[IO.File]::SetUnixFileMode($scratchHome,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
	Assert-ScratchHome $scratchHome $root
	$psi = [Diagnostics.ProcessStartInfo]::new($exe)
	$psi.WorkingDirectory = $release
	$psi.UseShellExecute = $false
	$psi.Environment['AINUR_VALIDATION'] = '1'
	$psi.Environment['AINUR_OPENAI_ROUTE'] = 'subscription'
	$psi.RedirectStandardOutput = $true
	$psi.RedirectStandardError = $true
	foreach($arg in @('--home',$scratchHome,'--port',"$Port",'--release',$ExpectedReleaseId) + $ChildArguments) { $psi.ArgumentList.Add($arg) }
	if($psi.FileName -cne $exe -or $psi.WorkingDirectory -cne $release -or
		$psi.Environment['AINUR_VALIDATION'] -cne '1' -or $psi.Environment['AINUR_OPENAI_ROUTE'] -cne 'subscription' -or
		$psi.ArgumentList.Count -lt 6 -or $psi.ArgumentList[0] -cne '--home' -or
		$psi.ArgumentList[2] -cne '--port' -or $psi.ArgumentList[3] -cne "$Port" -or
		$psi.ArgumentList[4] -cne '--release' -or $psi.ArgumentList[5] -cne $ExpectedReleaseId -or
		@($ChildArguments | Where-Object { $_ -match '^--(home|port|release)(=|$)' }).Count -ne 0) { throw 'ARGV_MISMATCH' }
	Assert-ScratchHome $psi.ArgumentList[1] $root
	if((Get-Item -LiteralPath $root -Force).LinkTarget -or (Get-Item -LiteralPath $scratchHome -Force).LinkTarget) { throw 'SYMLINK_COMPONENT' }
	# Provision only inside this newly owned scratch home, before a server could read it.
	if($BeforeStart) {
		try { $null = & $BeforeStart $scratchHome } catch { throw 'PRESTART_FAILED' }
		Assert-ScratchHome $scratchHome $root
		Assert-NoSymbolicComponents $scratchHome
	}
	# Recheck the release and marker immediately before child creation. Only this line may launch.
	if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -cne $ExpectedExecutableSha256.ToUpperInvariant() -or
		(Get-Content -LiteralPath $marker -Raw) -cne $nonce) { throw 'PIN_CHANGED' }
	$process = [Diagnostics.Process]::Start($psi)
	$record.stage = 'launched'
	$record.process_id = $process.Id
	$record.process_executable = $exe
	# A fast-exiting child can vanish before StartTime is readable. Never claim
	# a successful/identified launch from only a PID and prelaunch timestamp.
	try { $started = $process.StartTime } catch { $started = $null }
	if($null -eq $started) { throw 'START_IDENTITY_UNAVAILABLE' }
	$record.process_started_utc = $started.ToUniversalTime().ToString('O')
	if($WhileRunning) {
		# Callback receives only process identity + loopback port and scratch home;
		# no key, executable substitution, outside-home path or mutable Process object.
		try { $null = & $WhileRunning $scratchHome $Port $record.process_id $record.process_started_utc }
		catch { throw 'RUNNING_CHECK_FAILED' }
		Assert-ScratchHome $scratchHome $root
	}
	$process.WaitForExit(5000) | Out-Null
	if(!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
	$record.exit_code = $process.ExitCode
	$record.stopped = $process.HasExited
	$record.stage = 'finished'
} catch {
	$record.error_code = if($_.Exception.Message -match '^[A-Z_]+$') { $_.Exception.Message } else { 'SETUP_FAILED' }
	$record.stage = 'failed'
} finally {
	if($process) {
		if(!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
		$record.exit_code = $process.ExitCode
		$record.stopped = $process.HasExited
		$process.Dispose()
	}
	if([IO.Path]::IsPathFullyQualified($EvidencePath) -and [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath)) -ceq '/tmp' -and
		[IO.File]::Exists($EvidencePath) -and !(Get-Item -LiteralPath $EvidencePath -Force).LinkTarget -and
		[IO.File]::ReadAllText($EvidencePath) -ceq '{}') {
		[IO.File]::WriteAllText($EvidencePath,($record | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
	}
	# No cleanup here: a separate, independently scoped owner must verify the marker
	# and path identities before touching even a newly created scratch directory.
}
if($record.stage -eq 'failed') { throw "Scratch guard failed: $($record.error_code)" }
$record
