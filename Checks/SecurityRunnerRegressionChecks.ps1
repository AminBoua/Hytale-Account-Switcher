$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$root = Join-Path ([IO.Path]::GetTempPath()) ('security-runner-check-' + [Guid]::NewGuid().ToString('N'))
$locks = [Collections.Generic.List[IO.FileStream]]::new()
function Check($Value,[string]$Name) { if (!$Value) { throw "FAIL $Name" }; Write-Output "PASS $Name" }
try {
    $checks = Join-Path $root 'Checks'
    $null = New-Item -ItemType Directory -Path $checks -Force
    Copy-Item -LiteralPath (Join-Path $repository 'Build-Installer.ps1') -Destination $root
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SecurityEvidence.ps1') -Destination $checks
    $runner = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Run-SecurityChecks.ps1') -Raw
    # Replace only stage selection in the disposable copy; exercise the real runner and build gate.
    $selection = '(?s)\$steps = @\(.*?(?=Push-Location \$repository)'
    if ([regex]::Matches($runner, $selection).Count -ne 1) { throw 'Security runner fixture boundary changed.' }
    $stages = @'
$child = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $repository 'Child.ps1'),'-Code')
$steps = @(
    @{Name='success';Exe='powershell.exe';Arguments=$child + $(if ($case -eq 'child-failure') {7} else {0})},
    @{Name='negative';Exe='powershell.exe';Arguments=$child + $(if ($case -eq 'surviving-negative') {0} else {23});ExpectedFailure=$(if ($case -eq 'wrong-negative') {'missing diagnostic'} else {'fixture stderr before exit'})},
    @{Name='after';Exe='powershell.exe';Arguments=$child + 0}
)
if ($case -in @('locked','exclusive-locked')) {
    $sharing = if ($case -eq 'locked') { [IO.FileShare]::Read } else { [IO.FileShare]::None }
    $lockedLog = Join-Path $directory 'success.txt'
    [IO.File]::WriteAllText($lockedLog, '')
    $locks.Add([IO.File]::Open($lockedLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, $sharing))
}
'@
    [IO.File]::WriteAllText((Join-Path $checks 'Run-SecurityChecks.ps1'), [regex]::Replace($runner, $selection, [Text.RegularExpressions.MatchEvaluator]{param($match) $stages + "`r`n"}))
    @'
param([int]$Code)
Write-Output "fixture stdout before exit $Code"
[Console]::Error.WriteLine("fixture stderr before exit $Code")
Write-Output 'fixture stdout complete'
exit $Code
'@ | Set-Content -LiteralPath (Join-Path $root 'Child.ps1')
    & git init --quiet $root
    if ($LASTEXITCODE) { throw 'Cannot initialize security runner fixture.' }
    & git -C $root -c core.hooksPath=disabled-fixture-hooks -c user.name=Fixture -c user.email=fixture.invalid -c commit.gpgsign=false commit --quiet --allow-empty -m fixture
    if ($LASTEXITCODE) { throw 'Cannot commit security runner fixture.' }
    $reports = @()
    foreach ($case in @('success','locked','exclusive-locked','child-failure','wrong-negative','surviving-negative')) {
        $gateError = $null
        try { & (Join-Path $root 'Build-Installer.ps1') -ChecksOnly } catch { $gateError = $_.Exception.Message }
        finally { foreach ($stream in $locks) { $stream.Dispose() }; $locks.Clear() }
        $newReport = @(Get-ChildItem -LiteralPath (Join-Path $root 'dist/security') -Filter report.json -Recurse | Where-Object FullName -NotIn $reports)
        Check ($newReport.Count -eq 1) "$case records one gate report"
        $reports += $newReport[0].FullName
        $report = Get-Content -LiteralPath $newReport[0].FullName -Raw | ConvertFrom-Json
        Check ($report.Checks.Count -eq 3) "$case runs all stages"
        $first,$negative,$after = $report.Checks
        $expectedCode = if ($case -eq 'child-failure') {7} else {0}
        Check ($first.ExitCode -eq $expectedCode) "$case preserves child exit code $expectedCode"
        Check ($after.Status -eq 'passed' -and $after.ExitCode -eq 0) "$case completes remaining stage"
        if ($case -eq 'success') {
            Check (!$gateError -and $report.SafeStatus -eq 'passed' -and $first.Status -eq 'passed') 'ordinary success passes stage and gate'
        } else {
            Check ($gateError -match 'Safe security gate failed' -and $report.SafeStatus -eq 'failed') "$case fails overall gate"
        }
        if ($case -in @('locked','exclusive-locked')) {
            Check ($first.Status -eq 'failed' -and $first.Error -match 'Cannot write evidence log') "$case fails successful child stage with evidence diagnostic"
        } elseif ($case -eq 'child-failure') {
            Check ($first.Status -eq 'failed' -and $first.Error -eq 'Check exited 7.') 'unexpected child failure fails stage'
        }
        if ($case -in @('wrong-negative','surviving-negative')) {
            Check ($negative.Status -eq 'failed' -and $negative.Error -eq 'Mutation did not fail at its expected regression.') "$case rejects invalid negative control"
        } else {
            Check ($negative.Status -eq 'passed' -and $negative.ExitCode -eq 23) "$case accepts expected negative-control stderr and exit code"
        }
        foreach ($row in $report.Checks) {
            if ($row.Name -eq 'success' -and $case -in @('locked','exclusive-locked')) { continue }
            $log = Join-Path $newReport[0].DirectoryName $row.Log
            $body = Get-Content -LiteralPath $log -Raw
            Check ($body.Contains('fixture stdout complete') -and $body.Contains("fixture stderr before exit $($row.ExitCode)") -and $row.LogHash -eq (Get-FileHash -LiteralPath $log).Hash) "$case $($row.Name) retains stdout, stderr and evidence hash"
        }
    }
} finally {
    foreach ($stream in $locks) { $stream.Dispose() }
    $full = [IO.Path]::GetFullPath($root); $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $full -Leaf) -notmatch '^security-runner-check-[a-f0-9]{32}$') { throw 'Unsafe security runner fixture cleanup root.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
