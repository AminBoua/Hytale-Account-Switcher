param(
    [string]$ProgramsDirectory = [Environment]::GetFolderPath('Programs'),
    [string]$DesktopDirectory = [Environment]::GetFolderPath('DesktopDirectory'),
    [string]$CommonProgramsDirectory = [Environment]::GetFolderPath('CommonPrograms'),
    [string]$CommonDesktopDirectory = [Environment]::GetFolderPath('CommonDesktopDirectory'),
    [switch]$UserOnly,
    [string]$UserSid = '',
    [string]$RegistryPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HytaleAccounts'
)
$ErrorActionPreference = 'Stop'
try {
. (Join-Path $PSScriptRoot 'InstallerSafety.ps1')
$helper = Join-Path $PSScriptRoot 'HytaleAccounts.exe'
if (!(Test-Path -LiteralPath $helper)) { $helper = Join-Path $PSScriptRoot 'dist\build\HytaleAccounts.exe' }
if (!(Test-Path -LiteralPath $helper)) { throw 'Build first with Build-Installer.ps1.' }
$official = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
$destination = Join-Path (Split-Path $official) '.hytale-accounts-addon'
$installed = Join-Path $destination 'HytaleAccounts.exe'
. (Join-Path $PSScriptRoot 'ShortcutIntegration.ps1')
$state = if ($UserOnly) { Join-Path $env:LOCALAPPDATA 'HytaleAccounts\ShortcutIntegration' } else { $destination }
$scanRoots = if ($UserOnly) { @($DesktopDirectory, $ProgramsDirectory) } else { @($CommonDesktopDirectory, $CommonProgramsDirectory) }
$roots = $scanRoots
$repairRoots = $scanRoots
$ownerSid = $UserSid
if ($UserOnly -and [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Per-user shortcut integration must run without elevation.' }
foreach ($path in @($helper, $destination) + $roots) { Assert-RegularPath $path }
# A per-user journal must be restored by the Windows account that owns it.
$machineManifest = Join-Path $destination 'shortcuts.json'
if (Test-Path -LiteralPath $machineManifest) {
    Assert-RegularPath $machineManifest
    $machineJournal = Get-Content -LiteralPath $machineManifest -Raw | ConvertFrom-Json
    $callerSid = if ($UserOnly) { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value } else { $UserSid }
    if ($machineJournal.UserSid -and $machineJournal.UserSid -ne $callerSid) { throw 'Remove the addon from the Windows account that installed it before installing for another user.' }
    if ($UserOnly) { $ownerSid = $callerSid }
}
$before = Get-LauncherSnapshot $official
if (!$UserOnly -and (Test-Path -LiteralPath $RegistryPath)) {
    if ((Get-ItemProperty -LiteralPath $RegistryPath).InstallLocation -ne $destination) { throw 'Another installation owns the uninstall entry; preserved.' }
}
if (!$UserOnly) {
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action = New-ScheduledTaskAction -Execute $powershell -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $destination 'Repair-Integration.ps1') + '"')
    $repairTask = Get-ScheduledTask -TaskName 'HytaleAccounts-Repair' -ErrorAction SilentlyContinue
    if ($repairTask -and (@($repairTask.Actions).Count -ne 1 -or $repairTask.Actions[0].Execute -cne $powershell -or $repairTask.Actions[0].Arguments -cne $action.Arguments)) { throw 'Another task owns the shortcut repair entry; preserved.' }
    if ($UserSid) {
        $sid = [Security.Principal.SecurityIdentifier]::new($UserSid)
        if (!$sid.IsAccountSid()) { throw 'Invalid shortcut user SID.' }
        $userAction = New-ScheduledTaskAction -Execute $powershell -Argument ($action.Arguments + ' -UserOnly')
        $userTaskName = 'HytaleAccounts-Repair-' + $sid.Value
        $existingTask = Get-ScheduledTask -TaskName $userTaskName -ErrorAction SilentlyContinue
        if ($existingTask -and (@($existingTask.Actions).Count -ne 1 -or $existingTask.Actions[0].Execute -cne $powershell -or $existingTask.Actions[0].Arguments -cne $userAction.Arguments)) { throw 'Another task owns the user shortcut repair entry; preserved.' }
    }
    Stop-InstalledLauncher $installed -IncludeOfficial
}
$journalLock = Open-ShortcutJournal $state
$manifestPath = Join-Path $state 'shortcuts.json'
Assert-RegularPath $manifestPath
$script:records = @()
if (Test-Path -LiteralPath $manifestPath) {
    $journal = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $script:records = @($journal.Shortcuts)
    foreach ($record in $records) { Assert-ShortcutRecord $record $journal.Roots }
    $roots = @(@($journal.Roots) + $scanRoots | Select-Object -Unique)
}
$shell = New-Object -ComObject WScript.Shell
if (!$UserOnly) {
$payload = @()
$retained = @()
$backupPrefix = Join-Path $destination ('.upgrade-' + [Guid]::NewGuid().ToString('N') + '-')
$keepBackups = $false
try {
    # Stage every source before acquiring destinations; setup holds its sources read-only.
    $payloadNames = @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs','Repair-Integration.ps1','launcher-baseline.json','HytaleAccounts.exe')
    foreach ($name in $payloadNames) {
        $path = Join-Path $destination $name
        Assert-RegularPath $path
        $bytes = if ($name -eq 'launcher-baseline.json') { ,([Text.Encoding]::UTF8.GetBytes(($before | ConvertTo-Json))) }
        else {
            $source = if ($name -eq 'HytaleAccounts.exe') { $helper } else { Join-Path $PSScriptRoot $name }
            Assert-RegularPath $source
            ,([IO.File]::ReadAllBytes($source))
        }
        $payload += [pscustomobject]@{ Path = $path; Bytes = [byte[]]$bytes; File = $null; Original = $null; Changed = $false; Backup = $null; BackupPath = ($backupPrefix + $name + '.bak'); Stage = $null; StagePath = ($backupPrefix + $name + '.new') }
    }
    # Retain interrupted attempts (including legacy backups) until this retry publishes the entire set.
    foreach ($file in Get-ChildItem -LiteralPath $destination -Filter '.upgrade-*' -Force) {
        if ($file.Name -cmatch '^\.upgrade-[a-f0-9]{32}-(.+)\.(bak|new)$' -and $Matches[1] -cin $payloadNames) {
            $retained += Open-ShortcutFile $file.FullName
        }
    }
    # Hold the entire existing set before staging or replacing any payload file.
    foreach ($entry in $payload) {
        $entry.File = Open-ShortcutFile $entry.Path
        if ($entry.File.Exists) { $entry.Original = $entry.File.Read() }
    }
    foreach ($entry in $payload) {
        if ($entry.File.Exists) {
            $entry.Backup = Open-ShortcutFile $entry.BackupPath -Create
            $entry.Backup.Write($entry.Original)
        }
        $entry.Stage = Open-ShortcutFile $entry.StagePath -Create
        $entry.Stage.Write($entry.Bytes)
    }
    # Every sibling is complete and flushed before publication; the helper is the last commit point.
    foreach ($entry in $payload) {
        $entry.Changed = $true # ReplaceFile failures also need rollback.
        $entry.File.ReplaceFrom($entry.Stage)
    }
    foreach ($file in $retained) { $file.Delete() }
} catch {
    $failure = $_
    $rollbackErrors = @()
    for ($i = $payload.Count - 1; $i -ge 0; $i--) {
        $entry = $payload[$i]
        try {
            if ($entry.Changed) {
                if ($null -eq $entry.Original) { if ($entry.File.Exists) { $entry.File.Delete() } }
                else {
                    if (!$entry.Stage.Exists) {
                        $entry.Stage.Dispose()
                        $entry.Stage = Open-ShortcutFile $entry.StagePath -Create
                    }
                    $entry.Stage.Write($entry.Original)
                    $entry.File.ReplaceFrom($entry.Stage)
                }
            }
        } catch { $rollbackErrors += $_.Exception.Message }
    }
    if ($rollbackErrors.Count) {
        $keepBackups = $true
        throw "Addon replacement failed: $failure. Rollback failed: $($rollbackErrors -join '; '). Previous payload backups retained at $backupPrefix*.bak."
    }
    throw
} finally {
    foreach ($entry in $payload) {
        if ($entry.Stage) {
            try { if ($entry.Stage.Exists) { $entry.Stage.Delete() } }
            catch { Write-Warning "Could not remove upgrade staging file: $($entry.StagePath)" }
            finally { $entry.Stage.Dispose() }
        }
        if ($entry.Backup) {
            try { if (!$keepBackups) { $entry.Backup.Delete() } }
            catch { Write-Warning "Could not remove upgrade backup: $($entry.BackupPath)" }
            finally { $entry.Backup.Dispose() }
        }
        if ($entry.File) { $entry.File.Dispose() }
    }
    foreach ($file in $retained) { $file.Dispose() }
}
# Make removal available before any shortcut can be retargeted, even if the first update fails.
Save-ShortcutJournal
New-Item -Path $RegistryPath -Force | Out-Null
$properties = @{
    DisplayName = 'Hytale Accounts addon'; DisplayVersion = '0.1.3'; InstallLocation = $destination
    UninstallString = '"' + $installed + '" --uninstall'; DisplayIcon = "$official,0"
}
foreach ($name in $properties.Keys) { New-ItemProperty -LiteralPath $RegistryPath -Name $name -Value $properties[$name] -PropertyType String -Force | Out-Null }
}
# Keep every existing launcher's name and location, including official system-wide shortcuts.
foreach ($folder in $scanRoots) {
    if (Test-Path -LiteralPath $folder) {
        foreach ($file in Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -Recurse) {
            Assert-RegularPath $file.FullName
            $record = @($records | Where-Object Path -eq $file.FullName)
            if ($record.Count) { Update-Shortcut $file.FullName Install ($record | Select-Object -First 1); continue }
            $link = $shell.CreateShortcut($file.FullName)
            if ($link.TargetPath -in @($official, $installed) -and !$link.Arguments) {
                Update-Shortcut $file.FullName Install $null
            }
        }
    }
}
Save-ShortcutJournal
if ($UserOnly) {
    if (!$records.Count -and !@($machineJournal.Shortcuts | Where-Object Path).Count) { throw 'No Hytale launcher shortcut was found. Reinstall the official launcher first.' }
    return
}
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
$settings = New-ScheduledTaskSettingsSet -Hidden -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'HytaleAccounts-Repair' -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
if ($UserSid) {
    $principal = New-ScheduledTaskPrincipal -UserId $sid.Value -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $userTaskName -Action $userAction -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
}
Assert-LauncherSnapshot $official $before
Write-Output 'Installed. Official launcher bytes and permissions preserved.'
} catch {
    if ($UserOnly) { throw }
    $errorPath = Join-Path $PSScriptRoot 'install-error.txt'
    Assert-RegularPath $errorPath
    [IO.File]::WriteAllText($errorPath, $_.ToString())
    throw
}

finally { if ($journalLock) { $journalLock.Dispose() } }
