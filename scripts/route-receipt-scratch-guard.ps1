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
	# Fixed-purpose receipt probe; never accepts executable callbacks or supplied keys.
	[Parameter()] [string]$ExpectedCoreSha256,
	[Parameter()] [string]$ExpectedServerSha256
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$process = $null
$receiptDeadline = $null
# CLR callback required: PowerShell scriptblock events execute on thread-pool threads
# without a Runspace and crash the host. Store ONLY allowlisted diagnostic codes.
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
public sealed class AinurRestrictedStartupCapture {
    private readonly object gate = new object();
    private int lines;
    private int bytes;
    private string diagnostic = "NONE", exceptionKind = "UNCLASSIFIED", component = "UNKNOWN", site = "UNKNOWN";
    private bool exceptionIsInner;
    // Only fixed vocabulary crosses the process boundary. Never persist raw lines, paths, messages or arguments.
    public string Diagnostic { get { lock (gate) return diagnostic; } }
    public string ExceptionKind { get { lock (gate) return exceptionKind; } }
    public string Component { get { lock (gate) return component; } }
    public string Site { get { lock (gate) return site; } }
    private static readonly Regex Exception = new Regex(
        @"^\s*(?:(?:Unhandled exception\. |---> )?(?:System\.(?:IO\.|Net\.Sockets\.)?|Microsoft\.Data\.Sqlite\.|Microsoft\.Extensions\.DependencyInjection\.))(?<type>IOException|FileNotFoundException|DirectoryNotFoundException|UnauthorizedAccessException|InvalidOperationException|ArgumentException|ArgumentNullException|ArgumentOutOfRangeException|NullReferenceException|TypeInitializationException|TypeLoadException|DllNotFoundException|SqliteException|SocketException|FormatException|NotSupportedException)(?:\s|:|$)", RegexOptions.CultureInvariant);
    private static readonly Regex Frame = new Regex(@"^\s+at (?<ns>Ainur\.(?:Core|Server)|Microsoft\.Data\.Sqlite|Microsoft\.AspNetCore|System\.Net\.Sockets)(?:\.|\b)", RegexOptions.CultureInvariant);
    public void OnLine(object sender, DataReceivedEventArgs args) {
        var data = args.Data;
        if (data == null) return;
        lock (gate) {
            if (++lines > 1000 || (bytes += data.Length) > 65536 || data.Length > 4096) return;
            var text = data.ToLowerInvariant();
            if (text.Contains("address already in use")) diagnostic = "PORT_ALREADY_BOUND";
            else if (text.Contains("failed to bind") && diagnostic == "NONE") diagnostic = "BIND_FAILED";
            else if (text.Contains("permission denied") && diagnostic == "NONE") diagnostic = "PERMISSION_DENIED";
            else if (text.StartsWith("unhandled exception", StringComparison.Ordinal) && diagnostic == "NONE") diagnostic = "UNHANDLED_EXCEPTION";
            else if (text.StartsWith("you must install or update .net", StringComparison.Ordinal) && diagnostic == "NONE") diagnostic = "DOTNET_FRAMEWORK_MISSING";
            var isInner = data.TrimStart().StartsWith("---> ", StringComparison.Ordinal);
            if (exceptionKind == "UNCLASSIFIED" || (isInner && !exceptionIsInner)) {
                var match = Exception.Match(data);
                if (match.Success) {
                    exceptionKind = match.Groups["type"].Value.ToUpperInvariant();
                    exceptionIsInner = isInner;
                }
            }
            if (component == "UNKNOWN") {
                var match = Frame.Match(data);
                if (match.Success) {
                    var ns = match.Groups["ns"].Value;
                    component = ns == "Microsoft.Data.Sqlite" ? "SQLITE" : ns == "Microsoft.AspNetCore" ? "ASPNET_HOST" : ns == "System.Net.Sockets" ? "SOCKET" : ns == "Ainur.Server" ? "AINUR_SERVER" : "AINUR_CORE";
                }
            }
            // Independently fixed source-frame vocabulary, never a path, method name or user data.
            if (site == "UNKNOWN" && Frame.IsMatch(data)) {
                if (data.StartsWith("   at Microsoft.Data.Sqlite.SqliteConnection.Open(", StringComparison.Ordinal)) site = "SQLITE_OPEN";
                else if (data.StartsWith("   at System.Net.Sockets.Socket.Bind(", StringComparison.Ordinal)) site = "SOCKET_BIND";
                else if (data.StartsWith("   at Ainur.Core.Persistence.Db.", StringComparison.Ordinal)) site = "DB_INIT";
                else if (data.StartsWith("   at Ainur.Core.Runtime.AinurRuntime.", StringComparison.Ordinal)) site = "RUNTIME_INIT";
                else if (data.StartsWith("   at Ainur.Server.ServerOptions.FromArgs(", StringComparison.Ordinal)) site = "SERVER_OPTIONS";
                else if (data.StartsWith("   at Ainur.Server.SchedulerLock.TryAcquire(", StringComparison.Ordinal)) site = "SCHEDULER_LOCK";
                else if (data.StartsWith("   at Microsoft.AspNetCore.Hosting.WebHost.Start(", StringComparison.Ordinal)) site = "HOST_START";
            }
        }
    }
}
'@ -ErrorAction Stop
$capture = [AinurRestrictedStartupCapture]::new()
$record = [ordered]@{ stage = 'setup'; created = $false; process_id = $null; process_started_utc = $null; process_executable = $null; port = $Port; exit_code = $null; stopped = $false; child_disposition = 'NOT_STARTED'; startup_diagnostic = 'NONE'; startup_exception_kind = 'UNCLASSIFIED'; startup_component = 'UNKNOWN'; startup_site = 'UNKNOWN'; scratch_root = $null; error_code = $null }
function Remaining-ReceiptTime([Diagnostics.Stopwatch]$Clock) {
 $remainingMs = 12000 - [int]$Clock.ElapsedMilliseconds
 if($remainingMs -le 0) { throw 'RECEIPT_TIMEOUT' }
 return [TimeSpan]::FromMilliseconds($remainingMs)
}
function Stop-ExactChild([Diagnostics.Process]$Child) {
 try {
  # WaitForExit drains asynchronous redirected-output callbacks, even when already exited.
  if($Child.WaitForExit(750)) { return $true }
  $Child.Kill($false) # exact child only; never process tree
  return $Child.WaitForExit(2500)
 } catch { return $false } # UNKNOWN_HOLD: no unproven exited claim
}

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
	if($ExpectedExecutableSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $ExpectedReleaseId -notmatch '^r[A-Za-z0-9-]+$' -or
		($PSBoundParameters.ContainsKey('ExpectedCoreSha256') -and ($ExpectedCoreSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $ExpectedServerSha256 -notmatch '^[A-Fa-f0-9]{64}$')) -or
		(!$PSBoundParameters.ContainsKey('ExpectedCoreSha256') -and $PSBoundParameters.ContainsKey('ExpectedServerSha256'))) { throw 'INVALID_PIN' }
	if($evidence.StartsWith($root + '/', [StringComparison]::Ordinal) -or ![IO.Directory]::Exists([IO.Path]::GetDirectoryName($evidence)) -or
		$evidence -cnotmatch '^/tmp/ainur-rr-(?:fixed-evidence|guard-test|guard-evidence)-[A-Fa-f0-9]{32}\.json$') { throw 'UNSAFE_EVIDENCE' }
	if(![IO.File]::Exists($evidence) -or [IO.Path]::GetDirectoryName($evidence) -cne '/tmp') { throw 'MISSING_EVIDENCE' }
	# macOS /tmp is the well-known system symlink to /private/tmp; pin it exactly.
	if([IO.Path]::GetFullPath((Get-Item -LiteralPath '/tmp' -Force).ResolvedTarget) -cne '/private/tmp') { throw 'UNSAFE_TEMP_ALIAS' }
	Assert-NoSymbolicComponents '/private/tmp'
	if((Get-Item -LiteralPath $evidence -Force).LinkTarget) { throw 'UNSAFE_EVIDENCE' }
	Assert-NoSymbolicComponents $release
	Assert-NoSymbolicComponents $exe
	if((Get-Item -LiteralPath $evidence -Force).LinkTarget) { throw 'UNSAFE_EVIDENCE' }
	if((Get-Item -LiteralPath $evidence -Force).Length -ne 2) { throw 'EVIDENCE_NOT_EMPTY' }
	if([IO.File]::GetUnixFileMode($evidence) -ne ([IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)) { throw 'EVIDENCE_NOT_PRIVATE' }
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
	$scratchHomePath = Join-Path $root 'home'
	[IO.Directory]::CreateDirectory($scratchHomePath) | Out-Null
	[IO.File]::SetUnixFileMode($scratchHomePath,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
	Assert-ScratchHome $scratchHomePath $root
	$psi = [Diagnostics.ProcessStartInfo]::new($exe)
	$psi.WorkingDirectory = $release
	$psi.UseShellExecute = $false
	# No inherited provider keys, user HOME, proxy settings or shell tool search paths.
	$psi.Environment.Clear()
	$psi.Environment['HOME'] = $scratchHomePath
	$psi.Environment['TMPDIR'] = $root
	$psi.Environment['PATH'] = '/usr/bin:/bin'
	$psi.Environment['AINUR_VALIDATION'] = '1'
	$psi.Environment['AINUR_OPENAI_ROUTE'] = 'subscription'
	$psi.RedirectStandardOutput = $true
	$psi.RedirectStandardError = $true
	foreach($arg in @('--home',$scratchHomePath,'--port',"$Port",'--release',$ExpectedReleaseId) + $ChildArguments) { $psi.ArgumentList.Add($arg) }
	if($psi.FileName -cne $exe -or $psi.WorkingDirectory -cne $release -or
		$psi.Environment['HOME'] -cne $scratchHomePath -or $psi.Environment['TMPDIR'] -cne $root -or
		$psi.Environment['AINUR_VALIDATION'] -cne '1' -or $psi.Environment['AINUR_OPENAI_ROUTE'] -cne 'subscription' -or
		$psi.ArgumentList.Count -lt 6 -or $psi.ArgumentList[0] -cne '--home' -or
		$psi.ArgumentList[2] -cne '--port' -or $psi.ArgumentList[3] -cne "$Port" -or
		$psi.ArgumentList[4] -cne '--release' -or $psi.ArgumentList[5] -cne $ExpectedReleaseId -or
		@($ChildArguments | Where-Object { $_ -match '^--(home|port|release)(=|$)' }).Count -ne 0) { throw 'ARGV_MISMATCH' }
	Assert-ScratchHome $psi.ArgumentList[1] $root
	if((Get-Item -LiteralPath $root -Force).LinkTarget -or (Get-Item -LiteralPath $scratchHomePath -Force).LinkTarget) { throw 'SYMLINK_COMPONENT' }
	if($PSBoundParameters.ContainsKey('ExpectedCoreSha256')) {
		if($ChildArguments.Count -ne 0) { throw 'RECEIPT_EXTRA_ARGS' }
		$coreAssembly = Join-Path $release 'Ainur.Core.dll'
		$serverAssembly = Join-Path $release 'Ainur.Server.dll'
		Assert-NoSymbolicComponents $coreAssembly
		Assert-NoSymbolicComponents $serverAssembly
		if(![IO.File]::Exists($coreAssembly) -or (Get-FileHash -LiteralPath $coreAssembly -Algorithm SHA256).Hash -cne $ExpectedCoreSha256.ToUpperInvariant()) { throw 'CORE_MISMATCH' }
		if(![IO.File]::Exists($serverAssembly) -or (Get-FileHash -LiteralPath $serverAssembly -Algorithm SHA256).Hash -cne $ExpectedServerSha256.ToUpperInvariant()) { throw 'SERVER_MISMATCH' }
		# Provision only this new 0700 home. Key is never in argv, env, output or evidence.
		$supervisor = Join-Path $scratchHomePath 'supervisor'
		$secrets = Join-Path $supervisor 'receipt-secrets'
		foreach($directory in @($supervisor,$secrets)) {
			[IO.Directory]::CreateDirectory($directory) | Out-Null
			[IO.File]::SetUnixFileMode($directory,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
		}
		$keyPath = Join-Path $secrets 'route-receipt.key'
		if([IO.File]::Exists($keyPath) -or [IO.Directory]::Exists($keyPath)) { throw 'KEY_ALREADY_EXISTS' }
		$key = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
		$stream = [IO.FileStream]::new($keyPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
		try {
			[IO.File]::SetUnixFileMode($keyPath,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
			$bytes = [Text.Encoding]::ASCII.GetBytes($key + "`n")
			$stream.Write($bytes)
			$stream.Flush($true)
		} finally { $stream.Dispose() }
		Assert-ScratchHome $scratchHomePath $root
		Assert-NoSymbolicComponents $keyPath
	}
	# Recheck the release and marker immediately before child creation. Only this line may launch.
	if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -cne $ExpectedExecutableSha256.ToUpperInvariant() -or
		($PSBoundParameters.ContainsKey('ExpectedCoreSha256') -and ((Get-FileHash -LiteralPath $coreAssembly -Algorithm SHA256).Hash -cne $ExpectedCoreSha256.ToUpperInvariant() -or
		(Get-FileHash -LiteralPath $serverAssembly -Algorithm SHA256).Hash -cne $ExpectedServerSha256.ToUpperInvariant())) -or
		(Get-Content -LiteralPath $marker -Raw) -cne $nonce) { throw 'PIN_CHANGED' }
	$process = [Diagnostics.Process]::Start($psi)
	$record.stage = 'launched'
	$record.process_id = $process.Id
	$record.process_executable = $exe
	$receiptDeadline = [Diagnostics.Stopwatch]::StartNew()
	# Consume both redirected streams concurrently, storing only allowlisted codes,
	# never raw output, secret-bearing lines, command arguments or environment.
	$process.add_OutputDataReceived([Diagnostics.DataReceivedEventHandler]$capture.OnLine)
	$process.add_ErrorDataReceived([Diagnostics.DataReceivedEventHandler]$capture.OnLine)
	$process.BeginOutputReadLine()
	$process.BeginErrorReadLine()
	# A fast-exiting child can vanish before StartTime is readable. Never claim
	# a successful/identified launch from only a PID and prelaunch timestamp.
	try { $started = $process.StartTime } catch { $started = $null }
	if($null -eq $started) { throw 'START_IDENTITY_UNAVAILABLE' }
	$record.process_started_utc = $started.ToUniversalTime().ToString('O')
	if($PSBoundParameters.ContainsKey('ExpectedCoreSha256')) {
		# One monotonic overall deadline spanning cold-start, auth negatives, positive and bodies.
		$handler = [Net.Http.HttpClientHandler]::new()
		$handler.UseProxy = $false
		$handler.AllowAutoRedirect = $false
		$client = [Net.Http.HttpClient]::new($handler)
		$client.Timeout = [Threading.Timeout]::InfiniteTimeSpan
		$client.MaxResponseContentBufferSize = 65536
		try {
			$base = "http://127.0.0.1:$Port/api/v1"
			$version = $null
			while(!$version) {
				$null = Remaining-ReceiptTime $receiptDeadline
				if($process.HasExited) { throw 'CHILD_EXITED_BEFORE_RECEIPT' }
				try {
					$versionResponse = $client.GetAsync("$base/version").WaitAsync((Remaining-ReceiptTime $receiptDeadline)).GetAwaiter().GetResult()
					try {
						if(!$versionResponse.IsSuccessStatusCode) { throw 'VERSION_HTTP_FAILED' }
						$version = $versionResponse.Content.ReadAsStringAsync().WaitAsync((Remaining-ReceiptTime $receiptDeadline)).GetAwaiter().GetResult() | ConvertFrom-Json
					} finally { $versionResponse.Dispose() }
				} catch {
					if($receiptDeadline.ElapsedMilliseconds -ge 12000) { throw 'RECEIPT_TIMEOUT' }
					if($process.HasExited) { throw 'CHILD_EXITED_BEFORE_RECEIPT' }
					[Threading.Thread]::Sleep(75)
				}
			}
			if(!$version -or $version.release -cne $ExpectedReleaseId -or [int]$version.schema -ne 6) { throw 'VERSION_MISMATCH' }
			# Every negative must fail before accepting the positive authenticated receipt.
			foreach($negative in @('absent','invalid','query')) {
				$uri = if($negative -eq 'query') { "$base/control/route-receipt?key=not-a-token" } else { "$base/control/route-receipt" }
				$deniedRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$uri)
				try {
					if($negative -eq 'invalid') { $deniedRequest.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',('0' * 64)) }
					if($negative -eq 'query') { $deniedRequest.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$key) }
					$denied = $client.SendAsync($deniedRequest).WaitAsync((Remaining-ReceiptTime $receiptDeadline)).GetAwaiter().GetResult()
					try { if([int]$denied.StatusCode -ne 403) { throw 'AUTH_NEGATIVE_FAILED' } }
					finally { $denied.Dispose() }
				} finally { $deniedRequest.Dispose() }
			}
			$request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,"$base/control/route-receipt")
			try {
				$request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$key)
				$response = $client.SendAsync($request).WaitAsync((Remaining-ReceiptTime $receiptDeadline)).GetAwaiter().GetResult()
				try {
					if(!$response.IsSuccessStatusCode) { throw 'RECEIPT_HTTP_FAILED' }
					$receipt = $response.Content.ReadAsStringAsync().WaitAsync((Remaining-ReceiptTime $receiptDeadline)).GetAwaiter().GetResult() | ConvertFrom-Json
				} finally { $response.Dispose() }
			} finally { $request.Dispose() }
			$null = Remaining-ReceiptTime $receiptDeadline
			if($process.HasExited) { throw 'RECEIPT_CHILD_EXITED' }
			if($receipt.route_class -cne 'subscription' -or $receipt.provider_policy -cne 'openai_subscription_strict') { throw 'RECEIPT_ROUTE_MISMATCH' }
			if($receipt.release -cne $ExpectedReleaseId -or $receipt.core_sha256 -cne $ExpectedCoreSha256.ToUpperInvariant()) { throw 'RECEIPT_PIN_MISMATCH' }
			if([int]$receipt.process_id -ne $process.Id -or [int]$receipt.generation -ne [int]$version.generation) { throw 'RECEIPT_IDENTITY_MISMATCH' }
			$reportedStart = [DateTimeOffset]::Parse([string]$receipt.process_started_utc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal)
			$startDelta = [Math]::Abs(($reportedStart.ToUniversalTime() - $started.ToUniversalTime()).TotalSeconds)
			if($startDelta -gt 2) { throw 'RECEIPT_START_MISMATCH' }
			$record.route_class = 'subscription'
			$record.provider_policy = 'openai_subscription_strict'
			$record.core_sha256 = $ExpectedCoreSha256.ToUpperInvariant()
			$record.server_sha256 = $ExpectedServerSha256.ToUpperInvariant()
			$record.schema = 6
			$record.auth_negative = 'absent-invalid-query-forbidden'
			$record.generation = [int]$receipt.generation
		} finally { $client.Dispose() }
	}
	$record.stage = 'finished'
} catch {
	$record.error_code = if($receiptDeadline -and $receiptDeadline.ElapsedMilliseconds -ge 12000) { 'RECEIPT_TIMEOUT' }
		elseif($_.Exception.Message -match '^[A-Z_]+$') { $_.Exception.Message } else { 'SETUP_FAILED' }
	$record.stage = 'failed'
} finally {
	if($process) {
		$record.stopped = Stop-ExactChild $process
		$record.child_disposition = if($record.stopped) { 'EXIT_VERIFIED' } else { 'UNKNOWN_HOLD' }
		if($record.stopped) { try { $record.exit_code = $process.ExitCode } catch {} }
		$record.startup_diagnostic = $capture.Diagnostic
		$record.startup_exception_kind = $capture.ExceptionKind
		$record.startup_component = $capture.Component
		$record.startup_site = $capture.Site
		if(!$record.stopped) { $record.stage = 'unknown_hold'; $record.error_code = 'CHILD_DISPOSITION_UNKNOWN_HOLD' }
		$process.Dispose()
	}
	try {
		if([IO.Path]::IsPathFullyQualified($EvidencePath) -and [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath)) -ceq '/tmp' -and
			$EvidencePath -match '^/tmp/ainur-rr-(?:fixed-evidence|guard-test|guard-evidence)-[A-Fa-f0-9]{32}\.json$' -and
			[IO.File]::Exists($EvidencePath) -and !(Get-Item -LiteralPath $EvidencePath -Force).LinkTarget -and
			[IO.File]::GetUnixFileMode($EvidencePath) -eq ([IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite) -and
			[IO.File]::ReadAllText($EvidencePath) -ceq '{}') {
			[IO.File]::WriteAllText($EvidencePath,($record | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
		}
	} catch { $record.stage = 'unknown_hold'; $record.error_code = 'EVIDENCE_WRITE_UNKNOWN_HOLD' }
	# No cleanup here: a separate, independently scoped owner must verify the marker
	# and path identities before touching even a newly created scratch directory.
}
if($record.stage -ne 'finished') { throw "Scratch guard failed: $($record.error_code)" }
$record
