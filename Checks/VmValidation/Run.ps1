param(
    [Parameter(Mandatory)][ValidateSet('Initialize','Tasks','Verify','Cleanup')][string]$Action,
    [string]$Root, [string]$VmUuid, [string]$AdminUser, [string]$StandardUser, [string]$AttackerUser,
    [switch]$DesignatedDisposableVM
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Guard.ps1')
. (Join-Path $PSScriptRoot 'SecurityEvidence.ps1')
function Check($Value, [string]$Message) { if (!$Value) { throw $Message } }
Check ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) 'Run this guest-only controller from an elevated VM console.'
function New-FixtureDirectory([string]$Path, [string]$Owner = 'S-1-5-32-544', [bool]$Readable = $true) {
    Check (!(Test-Path -LiteralPath $Path)) "Refusing an existing fixture directory: $Path"
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')); $acl.SetAccessRuleProtection($true,$false)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544',$Owner) | Select-Object -Unique) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    if ($Readable) { $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')) }
    [IO.DirectoryInfo]::new($Path).Create($acl)
}
if ($Action -eq 'Initialize') {
    Check $DesignatedDisposableVM 'Explicit -DesignatedDisposableVM designation is required.'
    Check (!$Root) 'Initialize generates a fresh root; do not supply -Root.'
    $package = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package.json') -Raw | ConvertFrom-Json
    Check ($package.Schema -eq 1 -and $package.Kind -eq 'vm-validation-package' -and $package.Source.Fingerprint -match '^[A-F0-9]{64}$' -and $package.Source.Revision -match '^[a-f0-9]{40}$' -and $package.BuildEnvironment.DotnetSdk) 'Rebuild the validation package with source and SDK provenance.'
    Assert-VmIdentity (Get-CimInstance Win32_ComputerSystem) (Get-CimInstance Win32_ComputerSystemProduct).UUID $VmUuid $package.HostUuid (@(Get-NetAdapter | Where-Object Status -eq 'Up').Count -gt 0)
    Check ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System').EnableLUA -eq 1) 'UAC must be enabled.'
    $users = @($AdminUser,$StandardUser,$AttackerUser) | ForEach-Object {
        Check ($_ -match '^MLSV-(Admin|User|Other)-[a-f0-9]{6}$') 'Only uniquely named, disposable MLSV test accounts are accepted.'
        $user = Get-LocalUser -Name $_
        Check $user.Enabled 'Each disposable test account must be enabled.'
        $user
    }
    Check (@($users.SID.Value | Select-Object -Unique).Count -eq 3) 'Three distinct test accounts are required.'
    $admins = @(Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object { $_.SID.Value })
    $ordinary = @(Get-LocalGroupMember -SID 'S-1-5-32-545' | ForEach-Object { $_.SID.Value })
    Check ($users[0].SID.Value -in $admins -and $users[1].SID.Value -notin $admins -and $users[2].SID.Value -notin $admins) 'Expected one administrator and two unrelated standard users.'
    Check ($users[1].SID.Value -in $ordinary -and $users[2].SID.Value -in $ordinary) 'Standard actors must belong to local Users.'
    Check ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq $users[0].SID.Value) 'Initialize as the designated test administrator.'
    foreach ($entry in $package.Files) {
        Check ($entry.Name -match '^[A-Za-z0-9_.-]+$') 'Invalid package filename.'
        Check ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $entry.Name)).Hash -eq $entry.Hash) "Package hash mismatch: $($entry.Name)"
    }
    $id = [Guid]::NewGuid().ToString('N')
    $Root = 'C:\Program Files\ModLauncherSecurityVM-' + $id
    New-FixtureDirectory $Root
    New-FixtureDirectory (Join-Path $Root 'bin')
    foreach ($entry in $package.Files) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $entry.Name) -Destination (Join-Path $Root 'bin') }
    foreach ($name in @('channels','evidence','victims','system-repair')) { New-FixtureDirectory (Join-Path $Root $name) }
    foreach ($user in $users) { New-FixtureDirectory (Join-Path (Join-Path $Root 'channels') $user.SID.Value) $user.SID.Value }
    New-FixtureDirectory (Join-Path $Root 'limited-repair') $users[1].SID.Value
    foreach ($path in @((Join-Path $Root 'victim.txt'),(Join-Path $Root 'victims\sentinel.txt'))) { [IO.File]::WriteAllText($path, 'VM VICTIM SENTINEL') }
    $run = @{ Id=$id; VmUuid=$VmUuid; HostUuid=$package.HostUuid; ComputerName=$env:COMPUTERNAME; ExpiresUtc=[DateTime]::UtcNow.AddHours(8).ToString('o');
        Consent='DISPOSABLE VM ONLY; SNAPSHOT MAY BE DESTROYED'; AdminSid=$users[0].SID.Value; UserSid=$users[1].SID.Value; AttackerSid=$users[2].SID.Value; Files=$package.Files;
        Source=$package.Source; BuildEnvironment=$package.BuildEnvironment }
    $run | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Root 'run.json') -Encoding UTF8
    $null = Get-Guard $Root
    $registry = 'HKLM:\Software\ModLauncherSecurityVM-' + $id
    Check (!(Test-Path -LiteralPath $registry)) 'Refusing existing registry entry.'
    $null = New-Item -Path $registry
    $null = New-ItemProperty -LiteralPath $registry -Name Root -Value $Root
    Write-Output "Designated fixture: $Root"
    Write-Output 'Use only bin\Run.ps1 from this immutable fixture for Tasks, Verify and Cleanup. Run each mode once.'
    exit 0
}
$run = Get-Guard $Root
Check ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -eq (Join-Path $Root 'bin')) 'Use the designated package controller.'
$taskNames = @('System','Limited') | ForEach-Object { 'ModLauncherSecurityVM-' + $run.Id + '-' + $_ }
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
function Assert-TestTask($Task) {
    Check ($Task.TaskName -in $taskNames -and $Task.TaskPath -eq '\' -and @($Task.Actions).Count -eq 1) 'Task identity mismatch; refusing task mutation.'
    Check ($Task.Actions[0].Execute -eq $powershell -and $Task.Actions[0].Arguments.Contains('"' + (Join-Path $Root 'bin\RepairProbe.ps1') + '"')) 'Task action mismatch; refusing task mutation.'
}
if ($Action -eq 'Tasks') {
    foreach ($role in @('System','Limited')) {
        $name = 'ModLauncherSecurityVM-' + $run.Id + '-' + $role
        Check (!(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)) 'Task already exists; use a fresh run.'
        $arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $Root 'bin\RepairProbe.ps1') + '" -Root "' + $Root + '" -Role ' + $role
        $taskAction = New-ScheduledTaskAction -Execute $powershell -Argument $arguments
        $principal = if ($role -eq 'System') { New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest }
            else { New-ScheduledTaskPrincipal -UserId $run.UserSid -LogonType Interactive -RunLevel Limited }
        $settings = New-ScheduledTaskSettingsSet -Hidden -ExecutionTimeLimit (New-TimeSpan -Minutes 3) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
        $null = Register-ScheduledTask -TaskName $name -Action $taskAction -Principal $principal -Settings $settings
        Start-ScheduledTask -TaskName $name
        $result = Join-Path $Root (($role.ToLower() + '-repair') + '\result.json')
        $deadline = [DateTime]::UtcNow.AddMinutes(3)
        while (!(Test-Path -LiteralPath $result) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
        Check (Test-Path -LiteralPath $result) "Task $role produced no result; keep the standard user logged in and inspect Task Scheduler."
        # A result file alone does not prove the task finished successfully.
        do { $task = Get-ScheduledTask -TaskName $name; if ($task.State -ne 'Running') { break }; Start-Sleep -Milliseconds 200 } while ([DateTime]::UtcNow -lt $deadline)
        Check ($task.State -ne 'Running' -and (Get-ScheduledTaskInfo -TaskName $name).LastTaskResult -eq 0) "Task $role did not exit successfully."
        $data = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
        Check $data.Success "Task $role failed; inspect its result.json."
        Export-ScheduledTask -TaskName $name | Set-Content -LiteralPath (Join-Path $Root ('evidence\task-' + $role + '.xml')) -Encoding UTF8
        @{ Success=$true; TaskName=$name; LastTaskResult=(Get-ScheduledTaskInfo -TaskName $name).LastTaskResult; Identity=$data.Identity; Utc=[DateTime]::UtcNow.ToString('o') } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Root ('evidence\task-' + $role + '-completion.json')) -Encoding UTF8
    }
}
if ($Action -eq 'Verify') {
    $systemStage = Get-Content -LiteralPath (Join-Path $Root 'evidence\system-stage.json') -Raw | ConvertFrom-Json
    Check ($systemStage.Success -and $systemStage.Identity.Sid -eq 'S-1-5-18') 'No actual SYSTEM staging evidence.'
    foreach ($mode in @('normal','ots')) {
        $initiator = if ($mode -eq 'normal') { $run.AdminSid } else { $run.UserSid }
        foreach ($relative in @("evidence\elevated-$mode.json", "channels\$initiator\completed-$mode.json", "channels\$($run.AttackerSid)\source-$mode.json", "channels\$($run.AttackerSid)\stage-$mode.json")) {
            $data = Get-Content -LiteralPath (Join-Path $Root $relative) -Raw | ConvertFrom-Json
            Check $data.Success "Missing/failed evidence: $relative"
        }
    }
    foreach ($role in @('system','limited')) {
        $data = Get-Content -LiteralPath (Join-Path $Root "$role-repair\result.json") -Raw | ConvertFrom-Json
        Check $data.Success "Repair failed: $role"
        Check ($data.Identity.Sid -eq $(if ($role -eq 'system') { 'S-1-5-18' } else { $run.UserSid })) 'Repair identity mismatch.'
        $taskResult = Get-Content -LiteralPath (Join-Path $Root "evidence\task-$role-completion.json") -Raw | ConvertFrom-Json
        Check ($taskResult.Success -and $taskResult.LastTaskResult -eq 0) 'No successful real task completion evidence.'
    }
    foreach ($relative in @('victim.txt','victims\sentinel.txt')) { Check ([IO.File]::ReadAllText((Join-Path $Root $relative)) -eq 'VM VICTIM SENTINEL') 'Victim sentinel changed.' }
    $identity = & (Join-Path $Root 'bin\Checks.exe') identity $Root | ConvertFrom-Json
    if ($LASTEXITCODE) { throw 'Could not capture guest runtime.' }
    $os = Get-CimInstance Win32_OperatingSystem
    $evidence = @(Get-RequiredVmEvidence $run | ForEach-Object {
        $path = Join-Path $Root $_; Assert-PlainPath $path
        @{Name=$_.Replace('\','_');Hash=(Get-FileHash -LiteralPath $path).Hash}
    })
    @{ Schema=1; Kind='actual-vm'; Success=$true; Scope='Actual VM tokens, UAC and tasks; OTS foreign-owned source expected to fail closed';
        Utc=[DateTime]::UtcNow.ToString('o'); RunId=$run.Id; Source=$run.Source; Evidence=$evidence;
        Environment=@{WindowsBuild=$os.BuildNumber;WindowsVersion=$os.Version;OS=$os.Caption;Architecture=$env:PROCESSOR_ARCHITECTURE;DotnetRuntime=$identity.DotnetRuntime} } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Root 'evidence\summary.json') -Encoding UTF8
    Write-Output 'PASS evidence complete. OTS source rejection is recorded separately from successful staging.'
}
if ($Action -eq 'Cleanup') {
    foreach ($name in $taskNames) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if ($task) { Assert-TestTask $task; Stop-ScheduledTask -TaskName $name; Unregister-ScheduledTask -TaskName $name -Confirm:$false }
    }
    $registry = 'HKLM:\Software\ModLauncherSecurityVM-' + $run.Id
    if (Test-Path -LiteralPath $registry) { Check ((Get-ItemProperty -LiteralPath $registry).Root -eq $Root) 'Registry ownership mismatch.'; Remove-Item -LiteralPath $registry }
    # Export evidence before cleanup. Never recursively follow reparse points, even in an attack fixture.
    $export = 'C:\ProgramData\ModLauncherSecurityVM-Evidence-' + $run.Id
    New-FixtureDirectory $export
    $evidence = @('run.json','evidence\summary.json','evidence\system-stage.json','system-repair\result.json','limited-repair\result.json','evidence\task-System.xml','evidence\task-Limited.xml',
        'evidence\task-System-completion.json','evidence\task-Limited-completion.json')
    foreach ($mode in @('normal','ots')) {
        $sid = if ($mode -eq 'normal') { $run.AdminSid } else { $run.UserSid }
        $evidence += @("evidence\stage-ready-$mode.json","evidence\elevated-$mode.json","channels\$sid\begin-$mode.json","channels\$sid\completed-$mode.json",
            "channels\$($run.AttackerSid)\source-$mode.json","channels\$($run.AttackerSid)\stage-$mode.json","evidence\failed-elevated-$mode.json",
            "channels\$sid\failed-begin-$mode.json","channels\$($run.AttackerSid)\failed-attack-$mode.json")
    }
    foreach ($relative in $evidence) {
        $file = Join-Path $Root $relative
        if (Test-Path -LiteralPath $file) { Assert-PlainPath $file; Copy-Item -LiteralPath $file -Destination (Join-Path $export $relative.Replace('\','_')) }
    }
    function Remove-Fixture([string]$Path) {
        $full = [IO.Path]::GetFullPath($Path)
        Check ($full -eq $Root -or $full.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase)) 'Cleanup escaped fixture.'
        $item = Get-Item -LiteralPath $full -Force
        if ($item.PSIsContainer) {
            if (!($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { foreach ($child in Get-ChildItem -LiteralPath $full -Force) { Remove-Fixture $child.FullName } }
            [IO.Directory]::Delete($full)
        } else { [IO.File]::Delete($full) }
    }
    Remove-Fixture $Root
    Write-Output "Removed unique tasks, registry entry and fixtures. Evidence: $export. Revert the VM checkpoint to remove throwaway accounts."
}
