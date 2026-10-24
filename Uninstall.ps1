param(
    [string]$ProgramsDirectory = [Environment]::GetFolderPath('Programs'),
    [string]$DesktopDirectory = [Environment]::GetFolderPath('DesktopDirectory'),
    [string]$RegistryPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HytaleAccounts',
    [int]$WaitForPid = 0,
    [switch]$UserOnly,
    [switch]$KeepJournal,
    [string]$UserSid = '',
    [switch]$Interactive
)
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'InstallerSafety.ps1')
    . (Join-Path $PSScriptRoot 'ShortcutIntegration.ps1')
    if ($UserOnly -and [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Per-user shortcut removal must run without elevation.' }
    if ($WaitForPid) { Wait-Process -Id $WaitForPid -Timeout 30 -ErrorAction SilentlyContinue }
    $official = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
    $destination = Join-Path (Split-Path $official) '.hytale-accounts-addon'
    $installed = Join-Path $destination 'HytaleAccounts.exe'
    Assert-RegularPath $destination
    $machineManifest = Join-Path $destination 'shortcuts.json'
    if (Test-Path -LiteralPath $machineManifest) {
        Assert-RegularPath $machineManifest
        $machineJournal = Get-Content -LiteralPath $machineManifest -Raw | ConvertFrom-Json
        $callerSid = if ($UserOnly) { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value } else { $UserSid }
        if ($machineJournal.UserSid -and $machineJournal.UserSid -ne $callerSid) { throw 'Remove the addon from the Windows account that installed it so its shortcuts can be restored.' }
    }
    if (!$UserOnly) { Stop-InstalledLauncher $installed }
    $before = if (Test-Path -LiteralPath $official) { Get-LauncherSnapshot $official } else { $null }
    $shell = New-Object -ComObject WScript.Shell
    $state = if ($UserOnly) { Join-Path $env:LOCALAPPDATA 'HytaleAccounts\ShortcutIntegration' } else { $destination }
    $manifestPath = Join-Path $state 'shortcuts.json'
    if ($UserOnly -and !(Test-Path -LiteralPath $manifestPath)) { return }
    $journalLock = Open-ShortcutJournal $state
    Assert-RegularPath $manifestPath
    if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Shortcut restoration journal is missing; files preserved.' }
    $journal = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $records = @($journal.Shortcuts)
    $roots = @($journal.Roots)
    if (!$roots.Count) { throw 'Shortcut restoration journal is empty; files preserved.' }
    foreach ($record in $records) { Assert-ShortcutRecord $record $roots }
    if (!$UserOnly) {
    $repairTask = Get-ScheduledTask -TaskName 'HytaleAccounts-Repair' -ErrorAction SilentlyContinue
    if ($repairTask) {
        $expected = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $destination 'Repair-Integration.ps1') + '"'
        if (@($repairTask.Actions).Count -ne 1 -or $repairTask.Actions[0].Execute -cne (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -or $repairTask.Actions[0].Arguments -cne $expected) { throw 'Shortcut repair task changed; files preserved.' }
        Disable-ScheduledTask -TaskName 'HytaleAccounts-Repair' | Out-Null
        Stop-ScheduledTask -TaskName 'HytaleAccounts-Repair'
        for ($attempt = 0; (Get-ScheduledTask -TaskName 'HytaleAccounts-Repair').State -eq 'Running'; $attempt++) {
            if ($attempt -ge 50) { throw 'Shortcut repair is still stopping; retry removal. Files preserved.' }
            Start-Sleep -Milliseconds 100
        }
        Unregister-ScheduledTask -TaskName 'HytaleAccounts-Repair' -Confirm:$false
    }
    # Only remove tasks whose executable and arguments still match this installation.
    foreach ($task in @(Get-ScheduledTask -TaskName 'HytaleAccounts-Repair-S-1-*' -ErrorAction SilentlyContinue)) {
        $expectedUser = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $destination 'Repair-Integration.ps1') + '" -UserOnly'
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        if (@($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -cne $powershell -or $task.Actions[0].Arguments -cne $expectedUser) { throw 'User shortcut repair task changed; files preserved.' }
        Disable-ScheduledTask -TaskName $task.TaskName | Out-Null
        Stop-ScheduledTask -TaskName $task.TaskName
        Unregister-ScheduledTask -TaskName $task.TaskName -Confirm:$false
    }
    }
    foreach ($record in $records) { Update-Shortcut $record.Path Remove $record }
    if ($UserOnly) { if (!$KeepJournal) { Remove-ShortcutStateFiles }; return }
    if ($before) { Assert-LauncherSnapshot $official $before }
    # Only exact addon-owned files; accounts, encryption keys, worlds and Hytale remain untouched.
    $files = @('Install.ps1','Repair-Integration.ps1','launcher-baseline.json','HytaleAccounts.exe','Uninstall.ps1','InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')
    foreach ($name in $files) { Assert-RegularPath (Join-Path $destination $name) }
    $files += @('shortcuts.new') + @($records | Where-Object Backup | ForEach-Object Backup) + @('shortcuts.json')
    $removalFiles = @()
    try {
        # Hold exclusive delete-capable handles for the entire set before removing any recovery files.
        foreach ($name in $files) { $removalFiles += Open-ShortcutFile (Join-Path $destination $name) }
        foreach ($file in $removalFiles) { if ($file.Exists) { $file.Delete() } }
    } finally { foreach ($file in $removalFiles) { $file.Dispose() } }
    $journalLock.Delete(); $journalLock.Dispose(); $journalLock = $null
    if (!(Get-ChildItem -LiteralPath $destination -Force)) { [IO.Directory]::Delete($destination) }
    if (Test-Path -LiteralPath $RegistryPath) {
        if ((Get-ItemProperty -LiteralPath $RegistryPath).InstallLocation -eq $destination) { Remove-Item -LiteralPath $RegistryPath }
    }
    Write-Output 'Addon removed. Hytale, accounts and game data preserved.'
} catch {
    if ($Interactive) { Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show($_.Exception.Message, 'Hytale Accounts uninstall') | Out-Null }
    else { throw }
    exit 1
}

finally { if ($journalLock) { $journalLock.Dispose() } }
