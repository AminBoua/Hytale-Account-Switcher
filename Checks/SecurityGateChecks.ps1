$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SecurityEvidence.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('security-gate-check-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$source = @{Fingerprint=('A'*64);Revision=('b'*40);Dirty=$true}; $environment = @{DotnetSdk='synthetic-sdk'}
function Check($Value,[string]$Name) { if (!$Value) { throw "FAIL $Name" }; Write-Output "PASS $Name" }
function Write-Fixture([string]$Name,$Value) { $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root $Name.Replace('\','_')) -Encoding UTF8 }
function Read-Fixture([string]$Name) { Get-Content -LiteralPath (Join-Path $root $Name.Replace('\','_')) -Raw | ConvertFrom-Json }
function Seal {
    $summary.Evidence = @(Get-RequiredVmEvidence $run | ForEach-Object { @{Name=$_.Replace('\','_');Hash=(Get-FileHash -LiteralPath (Join-Path $root $_.Replace('\','_'))).Hash} })
    Write-Fixture 'evidence\summary.json' $summary
}
try {
    $repo = Join-Path $root 'source-fixture'
    $null = New-Item -ItemType Directory -Path (Join-Path $repo 'Launcher') -Force
    & git init --quiet $repo
    & git -C $repo config core.quotePath true
    if ($LASTEXITCODE) { throw 'Cannot configure temporary source fingerprint fixture.' }
    & git -C $repo -c core.hooksPath=disabled-fixture-hooks -c user.name=Fixture -c user.email=fixture.invalid -c commit.gpgsign=false commit --quiet --allow-empty -m fixture
    if ($LASTEXITCODE) { throw 'Cannot create temporary source fingerprint fixture.' }
    $view = Join-Path $repo 'Launcher/App.xaml'
    'synthetic view' | Set-Content -LiteralPath $view
    $original = Get-SecuritySource $repo
    Check ($original.Dirty -and $original.Files.Count -eq 1) 'uncommitted resources included in source fingerprint'
    Check ($original.Fingerprint -eq '1C0EA0C6AFA0763B330AAC65148E075B39147F2DC32F7618325288018220E857') 'ASCII-only source fingerprint remains unchanged'
    'documentation' | Set-Content -LiteralPath (Join-Path $repo 'Launcher/README.md')
    Check ((Get-SecuritySource $repo).Fingerprint -eq $original.Fingerprint) 'documentation-only edits preserve source fingerprint'
    'changed view' | Set-Content -LiteralPath $view
    Check ((Get-SecuritySource $repo).Fingerprint -ne $original.Fingerprint) 'resource changes invalidate source fingerprint'
    & git -C $repo add -- Launcher/App.xaml
    Remove-Item -LiteralPath $view
    $deleted = Get-SecuritySource $repo
    Check ($deleted.Files[0].Hash -eq 'DELETED' -and $deleted.Fingerprint -ne $original.Fingerprint) 'tracked deletions invalidate source fingerprint'
    # Character codes keep these fixtures portable to Windows PowerShell 5.1 without a script BOM.
    foreach ($relative in @("Launcher/$([char]0x00e9).cs", "Checks/$([char]0x6e2c)/fixture.cs")) {
        $before = Get-SecuritySource $repo
        $path = Join-Path $repo $relative
        $null = New-Item -ItemType Directory -Path (Split-Path $path) -Force
        'Unicode source' | Set-Content -LiteralPath $path
        $added = Get-SecuritySource $repo
        Check ($added.Fingerprint -ne $before.Fingerprint) "untracked Unicode addition invalidates source fingerprint: $relative"
        Check (@($added.Files | Where-Object { [string]::Equals($_.Path, $relative, [StringComparison]::Ordinal) }).Count -eq 1) "untracked Unicode path included verbatim: $relative"
        'edited untracked source' | Set-Content -LiteralPath $path
        $edited = Get-SecuritySource $repo
        Check ($edited.Fingerprint -ne $added.Fingerprint) "untracked Unicode edit invalidates source fingerprint: $relative"
        & git -C $repo add -- $relative
        if ($LASTEXITCODE) { throw 'Cannot track Unicode source fingerprint fixture.' }
        $tracked = Get-SecuritySource $repo
        Check (@($tracked.Files | Where-Object { [string]::Equals($_.Path, $relative, [StringComparison]::Ordinal) }).Count -eq 1 -and $tracked.Fingerprint -eq $edited.Fingerprint -and $tracked.Fingerprint -ne $before.Fingerprint) "tracked Unicode addition included with same fingerprint: $relative"
        'edited tracked source' | Set-Content -LiteralPath $path
        $changed = Get-SecuritySource $repo
        Check ($changed.Fingerprint -ne $tracked.Fingerprint) "tracked Unicode edit invalidates source fingerprint: $relative"
        Remove-Item -LiteralPath $path
        $removed = Get-SecuritySource $repo
        $entry = @($removed.Files | Where-Object { [string]::Equals($_.Path, $relative, [StringComparison]::Ordinal) })
        Check ($entry.Count -eq 1 -and $entry[0].Hash -eq 'DELETED' -and $removed.Fingerprint -ne $changed.Fingerprint) "tracked Unicode deletion retains DELETED marker and invalidates source fingerprint: $relative"
    }
    foreach ($mode in @('untracked-first','tracked-first')) {
        $repo = Join-Path $root $mode
        $null = New-Item -ItemType Directory -Path $repo
        & git init --quiet $repo
        & git -C $repo config core.quotePath true
        if ($LASTEXITCODE) { throw 'Cannot configure leading U+FEFF source fingerprint fixture.' }
        if ($mode -eq 'untracked-first') {
            $null = New-Item -ItemType Directory -Path (Join-Path $repo 'Launcher')
            'baseline source' | Set-Content -LiteralPath (Join-Path $repo 'Launcher/Base.cs')
            & git -C $repo add -- Launcher/Base.cs
            if ($LASTEXITCODE) { throw 'Cannot track leading U+FEFF fixture baseline.' }
        }
        & git -C $repo -c core.hooksPath=disabled-fixture-hooks -c user.name=Fixture -c user.email=fixture.invalid -c commit.gpgsign=false commit --quiet --allow-empty -m fixture
        if ($LASTEXITCODE) { throw 'Cannot commit leading U+FEFF source fingerprint fixture.' }
        if ($mode -eq 'untracked-first') { $before = Get-SecuritySource $repo }
        $relative = "$([char]0xfeff)Build.ps1"
        $path = Join-Path $repo $relative
        'leading U+FEFF source' | Set-Content -LiteralPath $path
        if ($mode -eq 'tracked-first') {
            & git -C $repo add -- $relative
            if ($LASTEXITCODE) { throw 'Cannot track leading U+FEFF source fingerprint fixture.' }
        }
        # The sole untracked path (or sole tracked path) is first in Git's NUL-separated output.
        $added = Get-SecuritySource $repo
        $entry = @($added.Files | Where-Object { [string]::Equals($_.Path, $relative, [StringComparison]::Ordinal) })
        Check ($entry.Count -eq 1 -and $entry[0].Hash -eq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) "$mode leading U+FEFF filename preserved verbatim with real file hash"
        Check (@($added.Files | Where-Object { [string]::Equals($_.Path, 'Build.ps1', [StringComparison]::Ordinal) }).Count -eq 0) "$mode has no phantom Build.ps1 entry"
        if ($mode -eq 'untracked-first') {
            Check ($added.Fingerprint -ne $before.Fingerprint) 'untracked leading U+FEFF addition invalidates source fingerprint'
        }
        'edited leading U+FEFF source' | Set-Content -LiteralPath $path
        $edited = Get-SecuritySource $repo
        $entry = @($edited.Files | Where-Object { [string]::Equals($_.Path, $relative, [StringComparison]::Ordinal) })
        Check ($edited.Fingerprint -ne $added.Fingerprint -and $entry.Count -eq 1 -and $entry[0].Hash -eq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) "$mode leading U+FEFF edit invalidates fingerprint with exact path and real file hash"
        & git -C $repo add -- $relative
        if ($LASTEXITCODE) { throw 'Cannot stage leading U+FEFF source fingerprint fixture.' }
        $tracked = Get-SecuritySource $repo
        $entry = @($tracked.Files | Where-Object { [string]::Equals($_.Path, $relative, [StringComparison]::Ordinal) })
        Check ($tracked.Fingerprint -eq $edited.Fingerprint -and $entry.Count -eq 1 -and $entry[0].Hash -eq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) "$mode tracking leading U+FEFF path preserves fingerprint and real file hash"
        'edited tracked leading U+FEFF source' | Set-Content -LiteralPath $path
        $changed = Get-SecuritySource $repo
        $entry = @($changed.Files | Where-Object { [string]::Equals($_.Path, $relative, [StringComparison]::Ordinal) })
        Check ($changed.Fingerprint -ne $tracked.Fingerprint -and $entry.Count -eq 1 -and $entry[0].Hash -eq (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash) "$mode tracked leading U+FEFF edit invalidates fingerprint with exact path and real file hash"
        Remove-Item -LiteralPath $path
        $removed = Get-SecuritySource $repo
        $entry = @($removed.Files | Where-Object { [string]::Equals($_.Path, $relative, [StringComparison]::Ordinal) })
        Check ($removed.Fingerprint -ne $changed.Fingerprint -and $entry.Count -eq 1 -and $entry[0].Hash -eq 'DELETED') "$mode leading U+FEFF deletion retains exact path and DELETED marker"
    }
    Check ((Test-VmEvidence '' $source $environment).Status -eq 'unverified') 'missing VM evidence is unverified'
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'empty VM evidence is unverified'
    # Fabricated schema fixtures ONLY, never exported as a real VM validation report.
    $admin='S-1-5-21-1-2-3-1001'; $user='S-1-5-21-1-2-3-1002'; $other='S-1-5-21-1-2-3-1003'
    $run=@{Id=('a'*32);AdminSid=$admin;UserSid=$user;AttackerSid=$other;VmUuid='fixture-guest';HostUuid='fixture-host';ComputerName='SCHEMA-FIXTURE';Source=$source;BuildEnvironment=$environment}
    Write-Fixture 'run.json' $run
    $full=@{Sid=$admin;Administrator=$true;ElevationType=2}
    $sddl='O:BAG:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)'
    foreach ($mode in @('normal','ots')) {
        $sid=if ($mode -eq 'normal') {$admin} else {$user}
        $identity=@{Sid=$sid;Administrator=$false;ElevationType=$(if ($mode -eq 'normal') {3} else {1})}
        Write-Fixture "channels\$sid\begin-$mode.json" @{Identity=$identity}
        Write-Fixture "channels\$sid\completed-$mode.json" @{Success=$true;Identity=$identity}
        Write-Fixture "evidence\stage-ready-$mode.json" @{Identity=$full;Sddl=$sddl}
        Write-Fixture "evidence\elevated-$mode.json" @{Success=$true;Identity=$full;InitiatorSid=$sid;OtsForeignSourceRejected=($mode -eq 'ots');FailureExit=23;StagingRemoved=$true;VictimsPreserved=$true;StagingSddl=$sddl;
            PayloadAcls=@(@('HytaleAccounts.exe','Install.ps1','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','ShortcutIntegration.ps1','ShortcutFile.cs') | ForEach-Object {@{Name=$_;Sddl=$sddl}})}
        foreach ($phase in @('source','stage')) {
            $operations=@('read','write','replace','delete-leaf','rename-directory','insert-file','cleanup-interference-handle','delete-directory')
            if ($phase -eq 'source') {$operations+=@('account-read','elevated-runner-substitution','helper-library-substitution')}
            Write-Fixture "channels\$other\$phase-$mode.json" @{Success=$true;PositiveControl=$true;Identity=@{Sid=$other;Administrator=$false;ElevationType=1};Operations=@($operations | ForEach-Object {@{Operation=$_;Denied=$true;WindowsError=5}})}
        }
    }
    Write-Fixture 'evidence\system-stage.json' @{Success=$true;Identity=@{Sid='S-1-5-18'};StagingRemoved=$true}
    foreach ($role in @('System','Limited')) {
        $identity=@{Sid=$(if ($role -eq 'System') {'S-1-5-18'} else {$user});Administrator=($role -eq 'System')}
        Write-Fixture ($role.ToLower()+'-repair\result.json') @{Success=$true;Identity=$identity;ProductionRepair=$true;UnsafeJournalRejected=$true;VictimPreserved=$true}
        Write-Fixture "evidence\task-$role-completion.json" @{Success=$true;LastTaskResult=0;Identity=$identity;TaskName="ModLauncherSecurityVM-$($run.Id)-$role"}
        '<Task>synthetic schema fixture</Task>' | Set-Content -LiteralPath (Join-Path $root "evidence_task-$role.xml")
    }
    $summary=@{Schema=1;Kind='actual-vm';Success=$true;RunId=$run.Id;Source=$source;Utc=[DateTime]::UtcNow.ToString('o');Environment=@{WindowsBuild='fixture';Architecture='AMD64';DotnetRuntime='fixture'}}
    Seal
    $result=Test-VmEvidence $root $source $environment
    Check ($result.Status -eq 'passed') ('complete fabricated schema accepted (NOT a VM result): '+$result.Reason)
    Check ((Test-VmEvidence $root @{Fingerprint=('C'*64)} $environment).Status -eq 'unverified') 'changed security source invalidates VM evidence'
    Check ((Test-VmEvidence $root $source @{DotnetSdk='changed'}).Status -eq 'unverified') 'changed SDK invalidates VM evidence'
    $summary.Kind='simulation'; Seal
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'simulated evidence cannot validate a release'
    $summary.Kind='actual-vm'; $summary.Success=$false; Seal
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'failed VM run cannot validate a release'
    $summary.Success=$true; Seal
    Add-Content -LiteralPath (Join-Path $root 'evidence_task-System.xml') -Value 'tampered'
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'tampered evidence hash rejected'
    Seal
    $elevated=Read-Fixture 'evidence\elevated-ots.json'; $elevated.Identity.Sid=$user; Write-Fixture 'evidence\elevated-ots.json' $elevated; Seal
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'wrong elevated identity rejected even with refreshed hashes'
    $elevated.Identity.Sid=$admin; $elevated.StagingSddl='O:BAG:BAD:(A;;FA;;;WD)'; Write-Fixture 'evidence\elevated-ots.json' $elevated; Seal
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'permissive staging ACL evidence rejected'
    $elevated.StagingSddl=$sddl; Write-Fixture 'evidence\elevated-ots.json' $elevated; Seal
    Remove-Item -LiteralPath (Join-Path $root 'limited-repair_result.json')
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'missing repair evidence rejected'
} finally {
    $full=[IO.Path]::GetFullPath($root); $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if (!$full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $full -Leaf) -notmatch '^security-gate-check-[a-f0-9]{32}$') { throw 'Unsafe test cleanup root.' }
    Remove-Item -LiteralPath $full -Recurse -Force
}
