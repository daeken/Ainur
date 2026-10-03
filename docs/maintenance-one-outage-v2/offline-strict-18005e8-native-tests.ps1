#requires -Version 7.0
# Focused offline native pre-spawn gate + controller hard-HOLD checks.
# No service/network/provider/home access. Uses the immutable public staged artifact read-only.
$ErrorActionPreference='Stop'
$repo=(Resolve-Path "$PSScriptRoot/../../").Path
$fixture=Join-Path $PSScriptRoot 'native-gate-probe.csproj'
$stage='/tmp/ainur-ulmo-full-ui-release-18005e8-verified'
if(!(Test-Path $fixture) -or !(Test-Path $stage)){throw 'OFFLINE_FIXTURE_UNAVAILABLE'}
dotnet restore $fixture --ignore-failed-sources --nologo -v:q
if($LASTEXITCODE -ne 0){throw 'OFFLINE_FIXTURE_RESTORE_FAILED'}
$doc=Get-Content (Join-Path $PSScriptRoot 'strict-18005e8-native-target.json') -Raw|ConvertFrom-Json
$manifest=Join-Path $repo 'src/Ainur.Supervisor/strict-18005e8-files.json'
if($doc.enabled -ne $false -or $doc.nativeAdapterSupplied -ne $false -or
   (Get-FileHash $manifest).Hash -cne $doc.release.filesManifestSha256 -or
   (Get-FileHash (Join-Path $stage 'release.json')).Hash -cne $doc.release.releaseJsonSha256 -or
   (Get-FileHash $doc.release.authenticatedStagedReceiptPath).Hash -cne $doc.release.authenticatedStagedReceiptSha256){throw 'PINS_OR_HOLD_CHANGED'}
$source=Get-Content (Join-Path $repo 'src/Ainur.Supervisor/Supervisor.cs') -Raw
$program=Get-Content (Join-Path $repo 'src/Ainur.Supervisor/Program.cs') -Raw
if($program -notmatch 'HOLD_NO_SPAWN: source-build is disabled' -or $program -notmatch 'CLI activation disabled' -or
   $source -notmatch 'StrictReleaseGate\.Verify\(releaseId, dir, out var reason\)' -or
   $source -notmatch 'StrictReleaseGate\.VerifyProtectedInstall\(releaseId, dir, StrictReleaseGate\.CurrentUid\(\), out reason\)' -or
   $source -notmatch 'StrictReleaseGate\.VerifyProtectedFile\(dotnetHost, StrictReleaseGate\.CurrentUid\(\)\)' -or
   $source -notmatch 'StrictReleaseGate\.SetStrictChildEnvironment\(psi\)' -or
   $source -notmatch 'StrictReleaseGate\.IsIndependentRollback\(req.ReleaseId, previous\)' -or
   $source -match 'releases\.BuildAsync|selecting newest built release') { throw 'SPAWN_PATH_NOT_GATED' }
$binary=Join-Path $repo 'src/Ainur.Supervisor/bin/Debug/net10.0/Ainur.Supervisor.dll'
if(!(Test-Path $binary)){throw 'SUPERVISOR_BUILD_REQUIRED'}
$out=@(dotnet $binary build 2>&1)
if($LASTEXITCODE -ne 3 -or @($out|Where-Object {$_ -match 'HOLD_NO_SPAWN'}).Count -ne 1){throw "CLI_BUILD_NOT_DISABLED $($out -join '; ')"}
'PASS native CLI source-build rejects before home access'
foreach($case in @('valid','old-e022','old-766','old-461','missing','tampered','extra','link','extra-directory','protected-staged','protected-wrong-id','protected-record','protected-same-uid','protected-group-write','protected-acl','protected-hardlink','protected-dotnet','hostile-env')){
 $out=@(dotnet run --no-restore --project $fixture -- $stage $case 2>&1)
 if($LASTEXITCODE -ne 0 -or @($out|Where-Object {$_ -match 'FAIL'}).Count -ne 0 -or @($out|Where-Object {$_ -match ' PASS$'}).Count -ne 1){throw "FAIL $case $($out -join '; ')"}
 $out[-1]
}
$ctrl=Get-Content (Join-Path $PSScriptRoot 'controller.ps1') -Raw
if($ctrl -notmatch 'Require \$false ''strict candidate' -or $ctrl -notmatch 'expectedRelease -ceq ''r20261002014730-766fa9f930'''){
 throw 'HISTORICAL_CONTROLLER_DISABLED_GATE_CHANGED'
}
# Merely parse; NEVER invoke controller against native adapter or private runtime.
$tokens=$null;$parseErrors=$null
[System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'controller.ps1'),[ref]$tokens,[ref]$parseErrors)|Out-Null
if($parseErrors.Count){throw 'CONTROLLER_PARSE_FAILURE'}
'PASS native controller historical mock-only branch unconditionally rejects; one accepted strict payload, no distinct rollback, no live operation'
$global:LASTEXITCODE=0
