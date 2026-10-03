# Disposable offline guard tests: only /usr/bin/true is ever started.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$guard = Join-Path $PSScriptRoot 'route-receipt-scratch-guard.ps1'
$fixture = "/tmp/ainur-rr-guard-fixture-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($fixture) | Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'release.json'),'{"id":"rdummy1"}',[Text.UTF8Encoding]::new($false))
[IO.File]::Copy('/usr/bin/true',(Join-Path $fixture 'dummy'),$false)
$hash = (Get-FileHash (Join-Path $fixture 'dummy') -Algorithm SHA256).Hash
$cases = @(
	@{ name = 'positive-dummy'; expected = 'finished' },
	@{ name = 'home-collision'; expected = 'finished'; collision = $true },
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
	$args = @{ TempRoot=$root; Executable=(Join-Path $fixture 'dummy'); ReleaseDirectory=$(if($case.ContainsKey('missing')) { "/tmp/ainur-rr-missing-$([Guid]::NewGuid().ToString('N'))" } else { $fixture }); ExpectedExecutableSha256=$(if($case.ContainsKey('wrongHash')) { '0'*64 } else { $hash }); ExpectedReleaseId='rdummy1'; Port=57001; EvidencePath=$evidence }
	if($case.ContainsKey('override')) { $args.ChildArguments = @('--home','/Users/daeken') }
	$outcome = $null
	try { $reply = & $guard @args; $outcome = $reply.stage }
	catch { $outcome = if(Test-Path -LiteralPath $evidence) { (Get-Content $evidence -Raw | ConvertFrom-Json).error_code } else { 'MISSING_EVIDENCE' } }
	if($outcome -cne $case.expected) { throw "CASE_FAILED name=$name expected=$($case.expected) observed=$outcome" }
	if($case.expected -ne 'finished' -and $case.ContainsKey('root')) { if(!(Test-Path -LiteralPath $root -PathType Any)) { throw 'FORBIDDEN_TARGET_REMOVED' } }
	$results += [pscustomobject]@{case=$name; outcome=$outcome; pass=$true}
}
$results | Format-Table -AutoSize
"PASSED=$($results.Count)"
