# Disposable offline guard tests: only bounded /bin/sleep and /usr/bin/true are started.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$guard = Join-Path $PSScriptRoot 'route-receipt-scratch-guard.ps1'
$fixture = "/tmp/ainur-rr-guard-fixture-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($fixture) | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'release.json'),'{"id":"rdummy1"}',[Text.UTF8Encoding]::new($false))
[IO.File]::Copy('/bin/sleep',(Join-Path $fixture 'dummy'),$false)
[IO.File]::Copy('/usr/bin/true',(Join-Path $fixture 'fast-dummy'),$false)
$hash = (Get-FileHash (Join-Path $fixture 'dummy') -Algorithm SHA256).Hash
$fastHash = (Get-FileHash (Join-Path $fixture 'fast-dummy') -Algorithm SHA256).Hash
$envDummy = Join-Path $fixture 'env-dummy'
$envResult = Join-Path $fixture 'env-result'
[IO.File]::WriteAllText($envDummy,"#!/bin/sh`nif [ `"`$AINUR_VALIDATION`" = '1' ] && [ `"`$AINUR_OPENAI_ROUTE`" = 'subscription' ]; then echo PINNED > '$envResult'; else echo WRONG > '$envResult'; fi`nexec /bin/sleep 1`n",[Text.UTF8Encoding]::new($false))
[IO.File]::SetUnixFileMode($envDummy,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
$envHash = (Get-FileHash $envDummy -Algorithm SHA256).Hash
$cases = @(
	@{ name = 'positive-dummy'; expected = 'finished' },
	@{ name = 'home-collision'; expected = 'finished'; collision = $true },
	@{ name = 'fast-exit'; expected = 'START_IDENTITY_UNAVAILABLE'; fast = $true },
	@{ name = 'hostile-ambient'; expected = 'finished'; envDummy = $true },
	@{ name = 'user-home'; expected = 'UNSAFE_ROOT'; root = '/Users/daeken' },
	@{ name = 'dev-home'; expected = 'UNSAFE_ROOT'; root = '/Users/daeken/projects/Ainur/src/Ainur.Server/.ainur/dev' },
	@{ name = 'symlink-root'; expected = 'STALE_ROOT'; symlink = $true },
	@{ name = 'stale-marker'; expected = 'STALE_ROOT'; stale = $true },
	@{ name = 'missing-release'; expected = 'MISSING_RELEASE'; missing = $true },
	@{ name = 'bad-pin'; expected = 'EXECUTABLE_MISMATCH'; wrongHash = $true },
	@{ name = 'missing-evidence'; expected = 'MISSING_EVIDENCE'; noEvidence = $true },
	@{ name = 'argv-home-override'; expected = 'ARGV_MISMATCH'; override = $true }
)
$results = @()
foreach($case in $cases) {
	$name = $case.name
	$root = if($case.ContainsKey('root')) { $case.root } else { "/tmp/ainur-rr-guard-$name-$([Guid]::NewGuid().ToString('N'))" }
	$evidence = "/tmp/ainur-rr-guard-test-$([Guid]::NewGuid().ToString('N')).json"
	if(!$case.ContainsKey('noEvidence')) { [IO.File]::WriteAllText($evidence,'{}',[Text.UTF8Encoding]::new($false)) }
	if($case.ContainsKey('symlink')) { [IO.Directory]::CreateSymbolicLink($root,$fixture) | Out-Null }
	if($case.ContainsKey('stale')) { [IO.Directory]::CreateDirectory($root) | Out-Null; [IO.File]::WriteAllText((Join-Path $root '.scratch-owner'),'stale') }
	if($case.ContainsKey('collision')) {
		try { Set-Variable -Name HOME -Value '/tmp/adversarial' -ErrorAction Stop; throw 'HOME_NOT_READ_ONLY' }
		catch { if($_.Exception.Message -eq 'HOME_NOT_READ_ONLY') { throw } }
	}
	$exe = if($case.ContainsKey('fast')) { Join-Path $fixture 'fast-dummy' } elseif($case.ContainsKey('envDummy')) { $envDummy } else { Join-Path $fixture 'dummy' }
	$args = @{ TempRoot=$root; Executable=$exe; ReleaseDirectory=$(if($case.ContainsKey('missing')) { "/tmp/ainur-rr-missing-$([Guid]::NewGuid().ToString('N'))" } else { $fixture }); ExpectedExecutableSha256=$(if($case.ContainsKey('wrongHash')) { '0'*64 } elseif($case.ContainsKey('fast')) { $fastHash } elseif($case.ContainsKey('envDummy')) { $envHash } else { $hash }); ExpectedReleaseId='rdummy1'; Port=57001; EvidencePath=$evidence; ChildArguments=$(if($case.ContainsKey('fast') -or $case.ContainsKey('envDummy')) { @() } else { @('1') }) }
	if($case.ContainsKey('override')) { $args.ChildArguments = @('--home','/Users/daeken') }
	$outcome = $null
	$oldValidation = [Environment]::GetEnvironmentVariable('AINUR_VALIDATION')
	$oldRoute = [Environment]::GetEnvironmentVariable('AINUR_OPENAI_ROUTE')
	try {
		if($case.ContainsKey('envDummy')) { $env:AINUR_VALIDATION = '0'; $env:AINUR_OPENAI_ROUTE = 'api' }
		try { $reply = & $guard @args; $outcome = $reply.stage }
		catch { $outcome = if(Test-Path -LiteralPath $evidence) { (Get-Content $evidence -Raw | ConvertFrom-Json).error_code } else { 'MISSING_EVIDENCE' } }
	} finally {
		[Environment]::SetEnvironmentVariable('AINUR_VALIDATION',$oldValidation)
		[Environment]::SetEnvironmentVariable('AINUR_OPENAI_ROUTE',$oldRoute)
	}
	if($case.ContainsKey('envDummy') -and (Get-Content -LiteralPath $envResult -Raw).Trim() -cne 'PINNED') { throw 'CHILD_ENV_NOT_PINNED' }
	if($case.ContainsKey('fast') -and $outcome -ceq 'finished') {
		$captured = Get-Content $evidence -Raw | ConvertFrom-Json
		if(!$captured.process_started_utc -or !$captured.stopped -or !$captured.process_id) { throw 'FAST_EXIT_FALSE_SUCCESS' }
	} elseif($outcome -cne $case.expected) { throw "CASE_FAILED name=$name expected=$($case.expected) observed=$outcome" }
	if($case.expected -ne 'finished' -and $case.ContainsKey('root')) { if(!(Test-Path -LiteralPath $root -PathType Any)) { throw 'FORBIDDEN_TARGET_REMOVED' } }
	$results += [pscustomobject]@{case=$name; outcome=$outcome; pass=$true}
}
$results | Format-Table -AutoSize
"PASSED=$($results.Count)"
