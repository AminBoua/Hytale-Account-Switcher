# Shared by the local build gate and the disposable-VM evidence exporter. No privileged actions.
function Get-SecurityEnvironment {
    $os = Get-CimInstance Win32_OperatingSystem
    @{ OS=$os.Caption; WindowsVersion=$os.Version; WindowsBuild=$os.BuildNumber; Architecture=$env:PROCESSOR_ARCHITECTURE;
        PowerShell=$PSVersionTable.PSVersion.ToString(); DotnetSdk=(& dotnet --version | Out-String).Trim();
        FileSystem=([IO.DriveInfo]::new([IO.Path]::GetPathRoot($env:LOCALAPPDATA))).DriveFormat }
}
function Get-SecuritySource([string]$Repository) {
    $revision = (& git -C $Repository rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE) { throw 'Cannot identify tested Git revision.' }
    $paths = @(& git -C $Repository ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE) { throw 'Cannot enumerate security-sensitive source.' }
    $files = @($paths | Sort-Object -Unique | Where-Object {
        ($_ -match '^(Launcher|Checks)/' -and $_ -notmatch '\.md$') -or
        ($_ -notmatch '/' -and $_ -match '\.(ps1|cs|props|targets|sln|slnx)$') -or
        $_ -match '(^|/)(global\.json|NuGet\.Config|Directory\.Build\.(props|targets))$' -or $_ -match '^\.github/workflows/'
    } | ForEach-Object {
        $path = Join-Path $Repository $_
        @{ Path=$_; Hash=$(if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { 'DELETED' }) }
    })
    if (!$files.Count) { throw 'Empty source fingerprint.' }
    $canonical = ($files | ForEach-Object { $_.Path + "`0" + $_.Hash + "`n" }) -join ''
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','') } finally { $sha.Dispose() }
    @{ Revision=$revision; Dirty=(@(& git -C $Repository status --porcelain).Count -gt 0); Fingerprint=$digest; Files=$files }
}
function Get-RequiredVmEvidence($Run) {
    @('run.json','evidence\system-stage.json','system-repair\result.json','limited-repair\result.json',
        'evidence\task-System.xml','evidence\task-Limited.xml','evidence\task-System-completion.json','evidence\task-Limited-completion.json')
    foreach ($mode in @('normal','ots')) {
        $sid = if ($mode -eq 'normal') { $Run.AdminSid } else { $Run.UserSid }
        @("evidence\stage-ready-$mode.json","evidence\elevated-$mode.json","channels\$sid\begin-$mode.json","channels\$sid\completed-$mode.json",
            "channels\$($Run.AttackerSid)\source-$mode.json","channels\$($Run.AttackerSid)\stage-$mode.json")
    }
}
function Test-VmEvidence([string]$Directory, $Source, $Environment) {
    if (!$Directory) { return @{ Status='unverified'; Reason='No actual VM evidence supplied.' } }
    try {
        function Require($Value, [string]$Message) { if (!$Value) { throw $Message } }
        function Read-Evidence([string]$Name) { Get-Content -LiteralPath (Join-Path $Directory $Name.Replace('\','_')) -Raw | ConvertFrom-Json }
        function Require-StagingAcl([string]$Sddl, [bool]$DirectoryAcl) {
            $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($Sddl)
            Require ($descriptor.Owner.Value -in @('S-1-5-18','S-1-5-32-544') -and $null -ne $descriptor.DiscretionaryAcl) 'Missing administrator/SYSTEM staging owner/DACL.'
            if ($DirectoryAcl) { Require ($descriptor.Owner.Value -eq 'S-1-5-32-544' -and ($descriptor.ControlFlags -band 0x1000)) 'Staging directory DACL must be protected and administrator-owned.' }
            foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
                Require (@($descriptor.DiscretionaryAcl | Where-Object { $_ -is [Security.AccessControl.CommonAce] -and !$_.IsCallback -and $_.AceQualifier -eq 'AccessAllowed' -and $_.SecurityIdentifier.Value -eq $sid -and ($_.AccessMask -band 0x1f01ff) -eq 0x1f01ff }).Count -gt 0) 'Staging lacks SYSTEM/admin full access.'
            }
            foreach ($ace in $descriptor.DiscretionaryAcl) {
                Require ($ace -is [Security.AccessControl.CommonAce] -and !$ace.IsCallback -and ($ace.AceQualifier -ne 'AccessAllowed' -or $ace.SecurityIdentifier.Value -in @('S-1-5-18','S-1-5-32-544'))) 'Staging permits another principal.'
            }
        }
        $summary = Read-Evidence 'evidence\summary.json'
        Require ($summary.Schema -eq 1 -and $summary.Kind -eq 'actual-vm' -and $summary.Success -eq $true) 'Missing successful actual-VM summary (simulations are not evidence).'
        $run = Read-Evidence 'run.json'
        Require ($run.Id -match '^[a-f0-9]{32}$' -and $summary.RunId -eq $run.Id) 'Mismatched VM run identity.'
        foreach ($sid in @($run.AdminSid,$run.UserSid,$run.AttackerSid)) { Require ($sid -match '^S-1-5-21-\d+-\d+-\d+-\d+$') 'Invalid VM actor SID.' }
        Require (@($run.AdminSid,$run.UserSid,$run.AttackerSid | Select-Object -Unique).Count -eq 3) 'VM actors must be distinct.'
        Require ($run.VmUuid -and $run.VmUuid -ne $run.HostUuid -and $run.ComputerName) 'Missing isolated VM identity.'
        Require ($summary.Source.Fingerprint -eq $Source.Fingerprint -and $run.Source.Fingerprint -eq $Source.Fingerprint) 'Security-sensitive source changed; fresh VM validation is required.'
        Require ($run.BuildEnvironment.DotnetSdk -eq $Environment.DotnetSdk) 'Build SDK changed; fresh VM validation is required.'
        Require ($summary.Environment.WindowsBuild -and $summary.Environment.Architecture -eq 'AMD64' -and $summary.Environment.DotnetRuntime) 'Missing tested guest environment.'
        Require ($summary.Utc -and [DateTime]::Parse($summary.Utc).ToUniversalTime() -le [DateTime]::UtcNow.AddMinutes(5)) 'Invalid VM completion timestamp.'
        $required = @(Get-RequiredVmEvidence $run)
        Require (@($summary.Evidence).Count -eq $required.Count) 'Incomplete VM evidence manifest.'
        foreach ($relative in $required) {
            $name = $relative.Replace('\','_')
            $entry = @($summary.Evidence | Where-Object Name -eq $name)
            Require ($entry.Count -eq 1) "Missing/duplicate evidence: $name"
            Require ((Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash -eq $entry[0].Hash) "Evidence hash mismatch: $name"
        }
        foreach ($mode in @('normal','ots')) {
            $sid = if ($mode -eq 'normal') { $run.AdminSid } else { $run.UserSid }
            $begin = Read-Evidence "channels\$sid\begin-$mode.json"
            $done = Read-Evidence "channels\$sid\completed-$mode.json"
            $elevated = Read-Evidence "evidence\elevated-$mode.json"
            Require ($done.Success -eq $true -and $done.Identity.Sid -eq $sid -and $begin.Identity.Sid -eq $sid -and !$begin.Identity.Administrator) 'Missing unelevated initiator result.'
            Require ($begin.Identity.ElevationType -eq $(if ($mode -eq 'normal') { 3 } else { 1 })) 'Wrong initiating UAC token.'
            Require ($elevated.Success -eq $true -and $elevated.Identity.Sid -eq $run.AdminSid -and $elevated.Identity.Administrator -eq $true -and $elevated.Identity.ElevationType -eq 2 -and $elevated.InitiatorSid -eq $sid) 'Missing actual UAC identity transition.'
            Require ($elevated.OtsForeignSourceRejected -eq ($mode -eq 'ots') -and $elevated.FailureExit -eq 23 -and $elevated.StagingRemoved -eq $true -and $elevated.VictimsPreserved -eq $true) 'Missing staging/failure/OTS outcome.'
            Require-StagingAcl $elevated.StagingSddl $true
            foreach ($name in @('HytaleAccounts.exe','Install.ps1','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')) {
                $file = @($elevated.PayloadAcls | Where-Object Name -eq $name)
                Require ($file.Count -eq 1) "Missing staged payload ACL: $name"
                Require-StagingAcl $file[0].Sddl $false
            }
            foreach ($phase in @('source','stage')) {
                $attack = Read-Evidence "channels\$($run.AttackerSid)\$phase-$mode.json"
                Require ($attack.Success -eq $true -and $attack.PositiveControl -eq $true -and $attack.Identity.Sid -eq $run.AttackerSid -and !$attack.Identity.Administrator -and $attack.Identity.ElevationType -eq 1) 'Missing actual unrelated-user token/positive control.'
                $operations = @('read','write','replace','delete-leaf','rename-directory','insert-file','cleanup-interference-handle','delete-directory')
                if ($phase -eq 'source') { $operations += @('account-read','elevated-runner-substitution','helper-library-substitution') }
                foreach ($operation in $operations) {
                    $row = @($attack.Operations | Where-Object Operation -eq $operation)
                    Require ($row.Count -eq 1 -and $row[0].Denied -eq $true -and $row[0].WindowsError -in @(5,32,33)) "Missing denied operation: $operation"
                }
            }
        }
        $stage = Read-Evidence 'evidence\system-stage.json'
        Require ($stage.Success -eq $true -and $stage.Identity.Sid -eq 'S-1-5-18' -and $stage.StagingRemoved -eq $true) 'Missing SYSTEM staging result.'
        foreach ($role in @('System','Limited')) {
            $repair = Read-Evidence ($role.ToLower() + '-repair\result.json')
            $task = Read-Evidence "evidence\task-$role-completion.json"
            $sid = if ($role -eq 'System') { 'S-1-5-18' } else { $run.UserSid }
            Require ($repair.Success -eq $true -and $repair.Identity.Sid -eq $sid -and $repair.Identity.Administrator -eq ($role -eq 'System') -and $repair.ProductionRepair -eq $true -and $repair.UnsafeJournalRejected -eq $true -and $repair.VictimPreserved -eq $true) 'Missing repair principal/outcomes.'
            Require ($task.Success -eq $true -and $task.LastTaskResult -eq 0 -and $task.Identity.Sid -eq $sid -and $task.TaskName -eq "ModLauncherSecurityVM-$($run.Id)-$role") 'Missing successful actual task completion.'
        }
        @{ Status='passed'; Reason='Complete recorded actual-VM validation matches source and SDK.'; RunId=$run.Id; TestedRevision=$run.Source.Revision; Directory=[IO.Path]::GetFullPath($Directory);
            Environment=$summary.Environment; SummaryHash=(Get-FileHash -LiteralPath (Join-Path $Directory 'evidence_summary.json')).Hash }
    } catch { @{ Status='unverified'; Reason=$_.Exception.Message } }
}
