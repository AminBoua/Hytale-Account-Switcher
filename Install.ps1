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
foreach ($name in @('HytaleAccounts.exe','Install.ps1','Uninstall.ps1','InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs','Repair-Integration.ps1','launcher-baseline.json')) { Assert-RegularPath (Join-Path $destination $name) }
Copy-Item -LiteralPath $helper -Destination $installed -Force
foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs','Repair-Integration.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $destination $name) -Force }
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'launcher-baseline.json') -Encoding UTF8
}
# Keep every existing launcher's name and location, including official system-wide shortcuts.
foreach ($folder in $scanRoots) {
    if (Test-Path -LiteralPath $folder) {
        foreach ($file in Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -Recurse) {
            Assert-RegularPath $file.FullName
            $link = $shell.CreateShortcut($file.FullName)
            if ($link.TargetPath -in @($official, $installed) -and !$link.Arguments) {
                $record = @($records | Where-Object Path -eq $file.FullName)
                Update-Shortcut $file.FullName Install ($record | Select-Object -First 1)
            }
        }
    }
}
Save-ShortcutJournal
if ($UserOnly) {
    if (!$records.Count -and !@($machineJournal.Shortcuts | Where-Object Path).Count) { throw 'No Hytale launcher shortcut was found. Reinstall the official launcher first.' }
    return
}
New-Item -Path $RegistryPath -Force | Out-Null
$properties = @{
    DisplayName = 'Hytale Accounts addon'; DisplayVersion = '0.1.2'; InstallLocation = $destination
    UninstallString = '"' + $installed + '" --uninstall'; DisplayIcon = "$official,0"
}
foreach ($name in $properties.Keys) { New-ItemProperty -LiteralPath $RegistryPath -Name $name -Value $properties[$name] -PropertyType String -Force | Out-Null }
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
