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

function Rebind-Runtime {
    $readyHash=(Get-FileHash -LiteralPath (Join-Path $root ("channels_"+$admin+"_runtime-ready-same-user.json"))).Hash
    foreach ($relative in @("channels\$admin\runtime-same-user.json","channels\$other\runtime-attack-same-user.json","channels\$admin\runtime-owner-attack-same-user.json")) {
        $row=Read-Fixture $relative; $row.ReadyHash=$readyHash; Write-Fixture $relative $row
    }
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
    $collisionFingerprint = $null
    foreach ($mode in @('untracked','tracked')) {
        $repo = Join-Path $root ("collision-$mode")
        $null = New-Item -ItemType Directory -Path $repo
        & git init --quiet $repo
        if ($LASTEXITCODE) { throw 'Cannot initialize colliding filename fixture.' }
        & git -C $repo -c core.hooksPath=disabled-fixture-hooks -c user.name=Fixture -c user.email=fixture.invalid -c commit.gpgsign=false commit --quiet --allow-empty -m fixture
        if ($LASTEXITCODE) { throw 'Cannot commit colliding filename fixture.' }
        $relatives = @('Build.ps1', "$([char]0xfeff)Build.ps1")
        foreach ($relative in $relatives[1..0]) {
            "collision source: $relative" | Set-Content -LiteralPath (Join-Path $repo $relative)
        }
        if ($mode -eq 'tracked') {
            & git -C $repo add -- $relatives
            if ($LASTEXITCODE) { throw 'Cannot track colliding filename fixture.' }
        }
        $current = Get-SecuritySource $repo
        Check ($current.Files.Count -eq 2) "$mode colliding filenames both included"
        foreach ($index in 0..1) {
            $relative = $relatives[$index]
            Check ([string]::Equals($current.Files[$index].Path, $relative, [StringComparison]::Ordinal) -and $current.Files[$index].Hash -eq (Get-FileHash -LiteralPath (Join-Path $repo $relative) -Algorithm SHA256).Hash) "$mode colliding filename has exact path and real hash in ordinal order: $relative"
        }
        Check ((Get-SecuritySource $repo).Fingerprint -eq $current.Fingerprint) "$mode colliding filename fingerprint is repeatable"
        if ($mode -eq 'untracked') { $collisionFingerprint = $current.Fingerprint }
        else { Check ($current.Fingerprint -eq $collisionFingerprint) 'tracking colliding filenames preserves fingerprint and ordering' }
        foreach ($relative in $relatives) {
            $before = $current
            "edited collision source: $relative" | Set-Content -LiteralPath (Join-Path $repo $relative)
            $current = Get-SecuritySource $repo
            Check ($current.Fingerprint -ne $before.Fingerprint) "$mode colliding filename edit invalidates source fingerprint: $relative"
            foreach ($expected in $relatives) {
                $entry = @($current.Files | Where-Object { [string]::Equals($_.Path, $expected, [StringComparison]::Ordinal) })
                Check ($entry.Count -eq 1 -and $entry[0].Hash -eq (Get-FileHash -LiteralPath (Join-Path $repo $expected) -Algorithm SHA256).Hash) "$mode colliding filename retains exact path and real hash after editing ${relative}: $expected"
            }
        }
    }
    Check ((Test-VmEvidence '' $source $environment).Status -eq 'unverified') 'missing VM evidence is unverified'
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'empty VM evidence is unverified'
    # Fabricated schema fixtures ONLY, never exported as a real VM validation report.
    $admin='S-1-5-21-1-2-3-1001'; $user='S-1-5-21-1-2-3-1002'; $other='S-1-5-21-1-2-3-1003'
    $run=@{StartedUtc=[DateTime]::UtcNow.AddMinutes(-10).ToString('o');ExpiresUtc=[DateTime]::UtcNow.AddHours(1).ToString('o');Id=('a'*32);AdminSid=$admin;UserSid=$user;AttackerSid=$other;VmUuid='fixture-guest';HostUuid='fixture-host';ComputerName='SCHEMA-FIXTURE';Source=$source;BuildEnvironment=$environment}
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
    $summary=@{Schema=2;Kind='actual-vm';Success=$true;RunId=$run.Id;Source=$source;Utc=[DateTime]::UtcNow.ToString('o');Environment=@{WindowsBuild='fixture';Architecture='AMD64';DotnetRuntime='fixture'}}

    # Old staging/task-only evidence cannot stand in for native runtime observations.
    $summary.Evidence = @(Get-RequiredVmEvidence $run | Where-Object { $_ -notmatch 'runtime-' } | ForEach-Object { @{Name=$_.Replace('\','_');Hash=(Get-FileHash -LiteralPath (Join-Path $root $_.Replace('\','_'))).Hash} })
    Write-Fixture 'evidence\summary.json' $summary
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'earlier evidence without runtime matrix rejected despite current fingerprint'
    foreach ($mode in @('ordinary','same-user','alternate-admin')) {
        $elevated = $mode -ne 'ordinary'
        $initiator = if ($mode -eq 'same-user') {$admin} else {$user}
        $worker = if ($elevated) {$admin} else {$user}
        $cache = "C:\Program Files\VM\runtime-$mode"
        $acl = $sddl
        $libraries = 'C:\Program Files\VM\bin'
        $nativeNames = @('D3DCompiler_47_cor3.dll','PenImc_cor3.dll','PresentationNative_cor3.dll','vcruntime140_cor3.dll','wpfgfx_cor3.dll')
        $run.Files = @($nativeNames | ForEach-Object { @{Name=$_;Hash=('D'*64)} })
        Write-Fixture 'run.json' $run
        $readyName = "channels\$worker\runtime-ready-$mode.json"
        $observed = [DateTime]::UtcNow.AddMinutes(-2).ToString('o')
        Write-Fixture $readyName @{Contract='runtime-sidecars-v2';RunId=$run.Id;Mode=$mode;SourceFingerprint=$source.Fingerprint;Utc=$observed;
            Identity=@{Sid=$worker;Administrator=$elevated;ElevationType=$(if ($elevated) {2} else {1});Pid=123};
            Cache=@{Path=$cache;Sddl=$acl};LibraryRoot=@{Path=$libraries;Sddl=$acl};Directories=@();Files=@($nativeNames | ForEach-Object { @{Path="$libraries\$_";Sddl=$acl;Hash=('D'*64)} });
            Modules=@("$libraries\PresentationNative_cor3.dll");BeforeApplication=@()}
        $readyHash=(Get-FileHash -LiteralPath (Join-Path $root $readyName.Replace('\','_'))).Hash
        Write-Fixture "channels\$initiator\runtime-$mode.json" @{Contract='runtime-sidecars-v2';RunId=$run.Id;Mode=$mode;SourceFingerprint=$source.Fingerprint;Success=$true;
            Identity=@{Sid=$initiator;Administrator=$false;ElevationType=$(if ($mode -eq 'same-user') {3} else {1});Pid=122};
            StartedUtc=[DateTime]::UtcNow.AddMinutes(-3).ToString('o');CompletedUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('o');
            ReadyHash=$readyHash;WorkerPid=123;ExitCode=23;CacheRemoved=$true;StartFailureCacheRemoved=$true;LibrariesPreserved=$true;PoisonPreserved=$true;ProfilePreserved=$true}
        foreach ($actor in @('unrelated') + @($(if ($elevated) {'owner'}))) {
            if (!$actor) {continue}
            $actorSid=if ($actor -eq 'owner') {$admin} else {$other}
            $name=if ($actor -eq 'owner') {'runtime-owner-attack'} else {'runtime-attack'}
            Write-Fixture "channels\$actorSid\$name-$mode.json" @{RunId=$run.Id;Mode=$mode;Success=$true;Identity=@{Sid=$actorSid;Administrator=$false;ElevationType=$(if ($actor -eq 'owner') {3} else {1});Pid=124};
                WorkerPid=123;ReadyHash=$readyHash;Utc=$observed;PositiveControl=$true;
                Operations=@(@('write','replace','delete-leaf','rename-directory','insert-file','delete-directory','file-write-dac','directory-write-dac') | ForEach-Object {@{Operation=$_;Denied=$true;WindowsError=5}});
                CacheOperations=@(@('rename-directory','insert-file','delete-directory','directory-write-dac') | ForEach-Object {@{Operation=$_;Denied=$true;WindowsError=5}})}
        }
    }
    Seal
    $baseline = @{}
    foreach ($relative in @(Get-RequiredVmEvidence $run | Where-Object {$_ -match 'runtime-'})) {
        $name=$relative.Replace('\','_'); $path=Join-Path $root $name
        $baseline[$relative]=[IO.File]::ReadAllText($path)
        Remove-Item -LiteralPath $path
        Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') "missing runtime record rejected: $relative"
        [IO.File]::WriteAllText($path,$baseline[$relative],[Text.UTF8Encoding]::new($true))
        Add-Content -LiteralPath $path -Value 'tamper'
        Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') "tampered runtime record rejected: $relative"
        [IO.File]::WriteAllText($path,$baseline[$relative],[Text.UTF8Encoding]::new($true))
    }
    foreach ($mutation in @('contract','source','run','worker-token','loaded-path','no-modules','no-files','acl','owner','before','cleanup','failure-cleanup','profile','poison','exit','pid','hash','time','failed','attack-token','attack-control','attack-operation','attack-time','owner-dac','no-directories','no-native-hash','native-parent','trusted-access','unknown-operation','cache-operation','libraries-changed','package-hash','library-root')) {
        $relative="channels\$admin\runtime-ready-same-user.json"
        if ($mutation -in @('cleanup','failure-cleanup','profile','poison','exit','pid','hash','time','failed','libraries-changed')) {$relative="channels\$admin\runtime-same-user.json"}
        if ($mutation.StartsWith('attack-') -or $mutation -eq 'unknown-operation') {$relative="channels\$other\runtime-attack-same-user.json"}
        if ($mutation -eq 'owner-dac') {$relative="channels\$admin\runtime-owner-attack-same-user.json"}
        if ($mutation -eq 'cache-operation') {$relative="channels\$other\runtime-attack-same-user.json"}
        $row=Read-Fixture $relative
        switch ($mutation) {
            'contract' {$row.Contract='earlier'}
            'source' {$row.SourceFingerprint='old'}
            'run' {$row.RunId='old'}
            'worker-token' {$row.Identity.ElevationType=3}
            'loaded-path' {$row.Modules=@('C:\untrusted\coreclr.dll')}
            'no-modules' {$row.Modules=@()}
            'no-files' {$row.Files=@()}
            'acl' {$row.Files[0].Sddl='O:BAG:BAD:(A;;FA;;;WD)'}
            'owner' {$row.Files[0].Sddl="O:$admin"+"G:BAD:(A;;FA;;;BA)(A;;FA;;;SY)"}
            'before' {$row.BeforeApplication=@('C:\untrusted\coreclr.dll')}
            'cleanup' {$row.CacheRemoved=$false}
            'failure-cleanup' {$row.StartFailureCacheRemoved=$false}
            'profile' {$row.ProfilePreserved=$false}
            'poison' {$row.PoisonPreserved=$false}
            'exit' {$row.ExitCode=0}
            'pid' {$row.WorkerPid=999}
            'hash' {$row.ReadyHash=('F'*64)}
            'time' {$row.StartedUtc='2000-01-01T00:00:00Z'}
            'failed' {$row.Success=$false}
            'attack-token' {$row.Identity.Sid=$user}
            'attack-control' {$row.PositiveControl=$false}
            'attack-operation' {$row.Operations[0].Denied=$false}
            'attack-time' {$row.Utc='2000-01-01T00:00:00Z'}
            'no-directories' { $row.PSObject.Properties.Remove('Directories') }
            'no-native-hash' { $row.Files[0].Hash='' }
            'native-parent' { $row.Files[0].Path += '\nested\coreclr.dll'; $row.BeforeApplication=@($row.Files[0].Path); $row.Modules=@($row.Files[0].Path) }
            'trusted-access' { $row.Files[0].Sddl='O:BAG:BAD:P(A;;FR;;;SY)(A;;FR;;;BA)' }
            'unknown-operation' { $row.Operations[0].WindowsError=2 }
            'owner-dac' {$row.Operations=@($row.Operations | Where-Object Operation -ne 'file-write-dac')}
            'cache-operation' {$row.CacheOperations=@()}
            'libraries-changed' {$row.LibrariesPreserved=$false}
            'package-hash' {$row.Files[0].Hash=('E'*64)}
            'library-root' {$row.LibraryRoot.Sddl="O:$user"+"G:BAD:P(A;;FA;;;$user)"}
        }
        Write-Fixture $relative $row
        if ($relative -like '*runtime-ready-same-user.json') { Rebind-Runtime }
        Seal
        Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') "inconsistent runtime $mutation rejected even after rehash"
        [IO.File]::WriteAllText((Join-Path $root $relative.Replace('\','_')),$baseline[$relative],[Text.UTF8Encoding]::new($true))
        Rebind-Runtime
        Seal
    }
    Set-StrictMode -Version Latest
    $result=Test-VmEvidence $root $source $environment
    Set-StrictMode -Off
    Check ($result.Status -eq 'passed') ('complete fabricated schema accepted (NOT a VM result): '+$result.Reason)
    Check ((Test-VmEvidence $root @{Fingerprint=('C'*64)} $environment).Status -eq 'unverified') 'changed security source invalidates VM evidence'
    Check ((Test-VmEvidence $root $source @{DotnetSdk='changed'}).Status -eq 'unverified') 'changed SDK invalidates VM evidence'
    $summary.Schema=1; Seal
    Check ((Test-VmEvidence $root $source $environment).Status -eq 'unverified') 'legacy schema rejected even with runtime records and fresh fingerprint'
    $summary.Schema=2
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
