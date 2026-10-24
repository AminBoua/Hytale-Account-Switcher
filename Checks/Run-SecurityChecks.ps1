param([string]$VmEvidence, [switch]$RegressionControls)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
. (Join-Path $PSScriptRoot 'SecurityEvidence.ps1')
if ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Safe build checks must run unelevated. Do not elevate to bypass a failure.' }
$directory = Join-Path $repository ('dist\security\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $directory
$report = @{ Schema=1; Kind='safe-synthetic'; StartedUtc=[DateTime]::UtcNow.ToString('o'); Source=(Get-SecuritySource $repository);
    Environment=(Get-SecurityEnvironment); Checks=@(); SafeStatus='failed'; VmStatus=@{Status='unverified';Reason='Not evaluated yet.'}; SecurityValidated=$false;
    Limitations=@('Authz is simulated access, not a second Windows user.','No UAC, real task registration, live Hytale, credentials, installation or external services.',
        'Native binary and legacy lifecycle integration are opt-in and not covered.','VM evidence is unsigned maintainer-supplied evidence, not remote attestation.'); ReportPath=(Join-Path $directory 'report.json') }
$report.Environment.Node = (& node --version | Out-String).Trim()
$steps = @(
    @{Name='build';Exe='dotnet';Arguments=@('build','Checks/Checks.csproj','-c','Release','--nologo','--configfile','Checks/VmValidation/NuGet.Config','-p:NuGetAudit=false')},
    @{Name='csharp-safe';Exe='dotnet';Arguments=@('run','--project','Checks/Checks.csproj','-c','Release','--no-build','--no-restore','--','--safe')},
    @{Name='frontend';Exe='node';Arguments=@('Checks/NativeLoginChecks.cjs')}
)
foreach ($entry in @(@('installer-fixtures','Checks/InstallerScriptChecks.ps1'),@('shortcut-races','Checks/ShortcutRaceChecks.ps1'),
    @('acl-races','Checks/AclMigrationRaceChecks.ps1'),@('storage-races','Checks/AclMigrationRaceChecks.ps1','-Storage'),
    @('vm-harness-host-only','Checks/VmValidation/Validate.ps1'),@('evidence-gate','Checks/SecurityGateChecks.ps1'),
    @('runtime-notices','Checks/RuntimeNoticeChecks.ps1'))) {
    $steps += @{Name=$entry[0];Exe='powershell.exe';Arguments=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File') + $entry[1..($entry.Length-1)]}
}
if ($RegressionControls) {
    foreach ($entry in @(
        @('Membership','Checks/AclMigrationRaceChecks.ps1','FAIL inspected protected-file insertion stops initialization'),
        @('Propagation','Checks/AclMigrationRaceChecks.ps1','FAIL uninspected child ACL is never changed'),
        @('Pins','Checks/AclMigrationRaceChecks.ps1','FAIL migration pins root'),
        @('Policy','Checks/AclMigrationRaceChecks.ps1','FAIL storage directory rejects foreign child insertion'),
        @('PrivateReplacement','Checks/AclMigrationRaceChecks.ps1','System.Exception: capture replaces existing permissive snapshot'),
        @('BodyTimeout','Checks/BridgeRegressionChecks.ps1','System.TimeoutException:'),
        @('HandlerTimeout','Checks/BridgeRegressionChecks.ps1','System.TimeoutException:'),
        @('HandlerSlots','Checks/BridgeRegressionChecks.ps1','FAIL timed-out handlers retain their slots until actual completion'),
        @('AdapterJump','Checks/NativeAdapterRegressionChecks.ps1','System.Exception: adapter jump destination: network'),
        @('AdapterMetadata','Checks/NativeAdapterRegressionChecks.ps1','System.Exception: adapter stack pcsp offset is relative to pc table'))) {
        $arguments = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$entry[1],'-Mutation',$entry[0])
        if ($entry[0] -eq 'Pins') { $arguments += '-PinsOnly' }
        if ($entry[0] -in @('Policy','PrivateReplacement')) { $arguments += '-StorageSafety' }
        $steps += @{Name=('negative-'+$entry[0]);Exe='powershell.exe';Arguments=$arguments;ExpectedFailure=$entry[2]}
    }
}
Push-Location $repository
try {
    foreach ($step in $steps) {
        $log = Join-Path $directory ($step.Name + '.txt')
        $row = @{Name=$step.Name;Command=$step.Exe;Arguments=$step.Arguments;StartedUtc=[DateTime]::UtcNow.ToString('o');Status='failed';Log=[IO.Path]::GetFileName($log)}
        $report.Checks += $row
        try {
            if ($step.Name -eq 'csharp-safe' -and $report.Checks[0].Status -ne 'passed') { throw 'Build failed; refusing stale check binaries.' }
            $executable = (Get-Command $step.Exe -ErrorAction Stop).Source
            $arguments = $step.Arguments
            $ErrorActionPreference = 'Continue' # Capture expected negative-control stderr without discarding the exit code.
            & $executable @arguments 2>&1 | ForEach-Object { $line=$_.ToString(); Add-Content -LiteralPath $log -Value $line; Write-Host $line }
            $code = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
            $row.ExitCode = $code
            if ($step.ExpectedFailure) {
                $row.ExpectedFailure = $step.ExpectedFailure
                if ($code -eq 0 -or (Get-Content -LiteralPath $log -Raw) -notmatch $step.ExpectedFailure) { throw 'Mutation did not fail at its expected regression.' }
            } elseif ($code -ne 0) { throw "Check exited $code." }
            $row.Status = 'passed'
        } catch { $row.Error=$_.Exception.Message; Write-Host "FAIL $($step.Name): $($row.Error)" }
        finally {
            $ErrorActionPreference = 'Stop'; $row.FinishedUtc=[DateTime]::UtcNow.ToString('o')
            if (Test-Path -LiteralPath $log) { $row.LogHash=(Get-FileHash -LiteralPath $log).Hash }
        }
    }
    if ((Get-SecuritySource $repository).Fingerprint -ne $report.Source.Fingerprint) { throw 'Source changed during checks; rerun the gate.' }
    if (!@($report.Checks | Where-Object Status -ne 'passed').Count) { $report.SafeStatus='passed' }
    $report.VmStatus = Test-VmEvidence $VmEvidence $report.Source $report.Environment
    $report.SecurityValidated = $report.SafeStatus -eq 'passed' -and $report.VmStatus.Status -eq 'passed'
} finally {
    Pop-Location
    $report.FinishedUtc=[DateTime]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report.ReportPath -Encoding UTF8
    Write-Host "Security evidence: $($report.ReportPath)"
    Write-Host "Safe checks: $($report.SafeStatus); actual VM: $($report.VmStatus.Status); security-validated: $($report.SecurityValidated)"
}
if ($report.SafeStatus -ne 'passed') { throw 'Safe security gate failed. See recorded commands and logs.' }
[pscustomobject]$report
