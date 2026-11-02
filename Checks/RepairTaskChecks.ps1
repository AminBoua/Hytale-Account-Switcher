$ErrorActionPreference = 'Stop'
Import-Module ScheduledTasks
function Check($ok, $message) { if (!$ok) { throw "FAIL $message" }; Write-Host "PASS $message" }
$source = Get-Content (Join-Path (Split-Path $PSScriptRoot) 'Install.ps1') -Raw
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
Check (!$errors.Count) 'installer parses'
# Execute the production task blocks with all task mutations mocked; no real task or shortcut is touched.
$preflight = $ast.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith("if (!`$UserOnly) {`n    `$powershell") }, $true)
if (!$preflight) { $preflight = $ast.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith("if (!`$UserOnly) {`r`n    `$powershell") }, $true) }
Check ($null -ne $preflight) 'task preflight block found'
$begin = $source.IndexOf('$trigger = New-ScheduledTaskTrigger -AtStartup')
$end = $source.IndexOf("Write-Output 'Installed.", $begin)
$register = [scriptblock]::Create($source.Substring($begin, $end - $begin))
$preflight = [scriptblock]::Create($preflight.Extent.Text)
$destination = Join-Path ([IO.Path]::GetTempPath()) ('repair-task-fixture-' + [guid]::NewGuid().ToString('N'))
$installed = Join-Path $destination 'HytaleAccounts.exe'
$UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$UserOnly = $false
$userName = 'HytaleAccounts-Repair-' + $UserSid
function Assert-LauncherSnapshot { param($Official,$Before) }
function Get-ScheduledTask { param($TaskName,$ErrorAction) $tasks[$TaskName] }
function Disable-ScheduledTask { param($TaskName) $events.Add("disable:$TaskName") }
function Stop-ScheduledTask { param($TaskName) $events.Add("stop:$TaskName") }
function Start-ScheduledTask { param($TaskName) Check ($null -eq $journalLock) 'repair starts after releasing journal'; $events.Add("start:$TaskName") }
function Stop-InstalledLauncher { param($Installed,[switch]$IncludeOfficial) $events.Add('launcher') }
function Register-ScheduledTask {
    param($TaskName,$Action,$Trigger,$Settings,$User,$RunLevel,$Principal,[switch]$Force)
    Check ($Action.Execute -ceq $installed) 'task launches installed WinExe'
    Check (!$Trigger.Repetition.Interval -and $Settings.RestartCount -eq 0 -and $Settings.ExecutionTimeLimit -eq 'PT0S') 'no timer or failure restart relaunches worker'
    Check ($Settings.MultipleInstances -eq 'IgnoreNew') 'duplicate workers are ignored'
    if ($Principal) {
        Check (([Security.Principal.NTAccount]::new($Principal.UserId)).Translate([Security.Principal.SecurityIdentifier]).Value -eq $UserSid -and $Principal.RunLevel -eq 'Limited' -and $Principal.LogonType -eq 'Interactive') 'user task remains limited and interactive'
        Check ($Trigger.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and $Trigger.UserId -eq $UserSid -and $Action.Arguments -ceq '--repair-integration-user') 'user task starts at owner logon with user scope'
    } else {
        Check ($User -eq 'SYSTEM' -and $RunLevel -eq 'Highest' -and $Trigger.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger' -and $Action.Arguments -ceq '--repair-integration') 'machine task retains SYSTEM and starts at boot'
    }
    $tasks[$TaskName] = [pscustomobject]@{TaskName=$TaskName; Actions=@($Action); State='Ready'}
}
foreach ($mode in @('new','legacy','current','foreign-machine','foreign-user','extra-action')) {
    $tasks = @{}; $events = [Collections.Generic.List[string]]::new()
    if ($mode -ne 'new') {
        foreach ($name in @('HytaleAccounts-Repair',$userName)) {
            $user = $name -eq $userName
            $exe = $installed; $arguments = if ($user) { '--repair-integration-user' } else { '--repair-integration' }
            if ($mode -eq 'legacy') {
                $exe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
                $arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $destination 'Repair-Integration.ps1') + '"'
                if ($user) { $arguments += ' -UserOnly' }
            }
            if (($mode -eq 'foreign-machine' -and !$user) -or ($mode -eq 'foreign-user' -and $user)) { $arguments += ' --foreign' }
            $actions = @([pscustomobject]@{Execute=$exe;Arguments=$arguments})
            if ($mode -eq 'extra-action') { $actions += $actions[0] }
            $tasks[$name] = [pscustomobject]@{TaskName=$name;Actions=$actions;State='Ready'}
        }
    }
    if ($mode -like 'foreign-*' -or $mode -eq 'extra-action') {
        $failure = $null
        try { . $preflight } catch { $failure = $_ }
        Check ($failure -and $failure.Exception.Message -like 'Another task owns*' -and !$events.Count) "$mode rejected before stopping or changing anything"
        continue
    }
    . $preflight
    if ($mode -ne 'new') { Check ($events.Count -eq 5 -and $events[4] -eq 'launcher') "$mode workers stop before launcher/payload replacement" }
    $journalLock = [IO.MemoryStream]::new()
    $heldJournal = $journalLock
    . $register
    Check (!$heldJournal.CanWrite -and $null -eq $journalLock) 'installer releases journal before launching repair'
    Check ($tasks.Count -eq 2 -and $events[$events.Count-2] -eq 'start:HytaleAccounts-Repair' -and $events[$events.Count-1] -eq "start:$userName") "$mode preserves task names and starts repair immediately"
}
