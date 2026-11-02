param(
    [string]$ProgramsDirectory = [Environment]::GetFolderPath('Programs'),
    [string]$DesktopDirectory = [Environment]::GetFolderPath('DesktopDirectory'),
    [string]$RegistryPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HytaleAccounts',
    [int]$WaitForPid = 0,
    [string]$RemovalAttempt = '',
    [switch]$UserOnly,
    [switch]$KeepJournal,
    [string]$UserSid = '',
    [switch]$Interactive
)
# Shell elevation cannot supply a child environment; restrict discovery before the first cmdlet.
$env:PSModulePath = [IO.Path]::Combine([Environment]::SystemDirectory, 'WindowsPowerShell\v1.0\Modules')
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'InstallerSafety.ps1')
    . (Join-Path $PSScriptRoot 'ShortcutIntegration.ps1')
    if ($UserOnly -and [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Per-user shortcut removal must run without elevation.' }
    if (!$UserOnly) { Wait-RemovalFinalization $WaitForPid $RemovalAttempt }
    $official = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
    $destination = Join-Path (Split-Path $official) '.hytale-accounts-addon'
    $installed = Join-Path $destination 'HytaleAccounts.exe'
    Assert-RegularPath $destination
    $machineManifest = Join-Path $destination 'shortcuts.json'
    if (Test-Path -LiteralPath $machineManifest) {
        Assert-RegularPath $machineManifest
        $machineJournal = Get-Content -LiteralPath $machineManifest -Raw -Encoding UTF8 | ConvertFrom-Json
        $callerSid = if ($UserOnly) { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value } else { $UserSid }
        if ($machineJournal.UserSid -and $machineJournal.UserSid -ne $callerSid) { throw 'Remove the addon from the Windows account that installed it so its shortcuts can be restored.' }
    }
    if (!$UserOnly) {
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $legacyArguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $destination 'Repair-Integration.ps1') + '"'
        $repairTasks = @(Get-ScheduledTask -TaskName 'HytaleAccounts-Repair' -ErrorAction SilentlyContinue) + @(Get-ScheduledTask -TaskName 'HytaleAccounts-Repair-S-1-*' -ErrorAction SilentlyContinue)
        # Validate every action before stopping either scope; never operate on a foreign task.
        foreach ($task in $repairTasks) {
            $userTask = $task.TaskName -ne 'HytaleAccounts-Repair'
            $legacy = $legacyArguments
            $arguments = '--repair-integration'
            if ($userTask) { $legacy += ' -UserOnly'; $arguments = '--repair-integration-user' }
            if (@($task.Actions).Count -ne 1 -or !(($task.Actions[0].Execute -ceq $powershell -and $task.Actions[0].Arguments -ceq $legacy) -or ($task.Actions[0].Execute -ceq $installed -and $task.Actions[0].Arguments -ceq $arguments))) { throw 'Shortcut repair task changed; files preserved.' }
        }
        foreach ($task in $repairTasks) {
            Disable-ScheduledTask -TaskName $task.TaskName | Out-Null
            Stop-ScheduledTask -TaskName $task.TaskName
            for ($attempt = 0; (Get-ScheduledTask -TaskName $task.TaskName).State -eq 'Running'; $attempt++) {
                if ($attempt -ge 50) { throw 'Shortcut repair is still stopping; retry removal. Files preserved.' }
                Start-Sleep -Milliseconds 100
            }
        }
        Stop-InstalledLauncher $installed
    }
    $before = if (Test-Path -LiteralPath $official) { Get-LauncherSnapshot $official } else { $null }
    $shell = New-Object -ComObject WScript.Shell
    $state = if ($UserOnly) { Join-Path $env:LOCALAPPDATA 'HytaleAccounts\ShortcutIntegration' } else { $destination }
    $manifestPath = Join-Path $state 'shortcuts.json'
    if ($UserOnly -and !(Test-Path -LiteralPath $manifestPath)) { return }
    $journalLock = Open-ShortcutJournal $state
    Assert-RegularPath $manifestPath
    if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Shortcut restoration journal is missing; files preserved.' }
    $journal = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $records = @($journal.Shortcuts)
    $roots = @($journal.Roots)
    if (!$roots.Count) { throw 'Shortcut restoration journal is empty; files preserved.' }
    foreach ($record in $records) { Assert-ShortcutRecord $record $roots }
    Assert-ShortcutIdentities $records
    if (!$UserOnly) {
        foreach ($task in $repairTasks) { Unregister-ScheduledTask -TaskName $task.TaskName -Confirm:$false }
    }
    foreach ($record in $records) { Update-Shortcut $record.Path Remove $record }
    foreach ($folder in $roots) {
        Assert-RegularPath $folder
        if (Test-Path -LiteralPath $folder) {
            foreach ($file in Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -File -Recurse) {
                if ($file.FullName -notin $records.Path) { Update-Shortcut $file.FullName Remove $null }
            }
        }
    }
    if ($UserOnly) { if (!$KeepJournal) { Remove-ShortcutStateFiles }; return }
    if ($before) { Assert-LauncherSnapshot $official $before }
    # Only exact addon-owned files; accounts, encryption keys, worlds and Hytale remain untouched.
    $files = @('D3DCompiler_47_cor3.dll','PenImc_cor3.dll','PresentationNative_cor3.dll','vcruntime140_cor3.dll','wpfgfx_cor3.dll','LICENSE','THIRD-PARTY-NOTICES.txt','Install.ps1','Repair-Integration.ps1','launcher-baseline.json','HytaleAccounts.exe','Uninstall.ps1','InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')
    $retained = @()
    foreach ($file in Get-ChildItem -LiteralPath $destination -Filter '.upgrade-*' -Force) {
        if ($file.Name -cmatch '^\.upgrade-[a-f0-9]{32}-(.+)\.(bak|new)$' -and $Matches[1] -cin $files) {
            $retained += $file.Name
        }
    }
    $files += $retained
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
