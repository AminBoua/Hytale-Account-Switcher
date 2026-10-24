$ErrorActionPreference = 'Stop'
$id = [Guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) ('addon-lifecycle-' + $id)
$registry = 'HKCU:\Software\HytaleAccounts-Check-' + $id
$repository = Split-Path $PSScriptRoot
$oldLocal = $env:LOCALAPPDATA
$oldPrograms = $env:ProgramFiles
# Exercise the real task action/trigger/settings without registering a system task during fixtures.
Import-Module ScheduledTasks
$global:HytaleFixtureTasks = @{}
function Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Settings,$User,$RunLevel,$Principal,[switch]$Force)
    Check ($Settings.Hidden -and $Action.Arguments -like '*-WindowStyle Hidden*') 'repair runs invisibly'
    if ($Principal) { Check ($Principal.RunLevel -eq 'Limited' -and $Principal.LogonType -eq 'Interactive') 'user shortcut repair has a limited interactive token' }
    else { Check ($User -eq 'SYSTEM') 'common shortcut repair uses SYSTEM' }
    $global:HytaleFixtureTasks[$TaskName] = [pscustomobject]@{ TaskName = $TaskName; Actions = @($Action); State = 'Ready' }
}
function Get-ScheduledTask { param($TaskName,$ErrorAction) @($global:HytaleFixtureTasks.Values | Where-Object TaskName -like $TaskName) }
function Disable-ScheduledTask { param($TaskName) }
function Stop-ScheduledTask { param($TaskName) }
function Unregister-ScheduledTask { param($TaskName,$Confirm) $global:HytaleFixtureTasks.Remove($TaskName) }
function Get-CimInstance { param($ClassName,$Filter) } # Fixture payloads never have running processes.
function Check($Condition, [string]$Message) {
    if (!$Condition) { throw "FAIL $Message" }
    Write-Host "PASS $Message"
}
function Get-InstallFixtureSnapshot {
    [pscustomobject]@{
        # Setup's diagnostic is expected on rejection; installation state must not change.
        Files = (@(Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object FullName -ne (Join-Path $package 'install-error.txt') | Sort-Object FullName | ForEach-Object {
            [pscustomobject]@{ Path = $_.FullName; Hash = if (!$_.PSIsContainer) { (Get-FileHash -LiteralPath $_.FullName).Hash } }
        }) | ConvertTo-Json -Depth 4 -Compress)
        Registry = $(if (Test-Path -LiteralPath $registry) { Get-ItemProperty -LiteralPath $registry | ConvertTo-Json -Compress })
        Tasks = (@($global:HytaleFixtureTasks.Values | Sort-Object TaskName) | ConvertTo-Json -Depth 6 -Compress)
    }
}
function Check-InstallPreflight([string]$Phase) {
    foreach ($case in @(
        @{ Name = 'conflicting SYSTEM task'; TaskName = 'HytaleAccounts-Repair'; Error = 'Another task owns the shortcut repair entry; preserved.' },
        @{ Name = 'conflicting user task'; TaskName = ('HytaleAccounts-Repair-' + $options.UserSid); Error = 'Another task owns the user shortcut repair entry; preserved.' },
        @{ Name = 'invalid user SID'; UserSid = 'S-1-5-18'; Error = 'Invalid shortcut user SID.' }
    )) {
        # An existing journal already rejects a different SID before task preflight.
        if ($case.UserSid -and (Test-Path -LiteralPath (Join-Path $destination 'shortcuts.json'))) { continue }
        $savedTasks = $global:HytaleFixtureTasks.Clone()
        $testOptions = $options.Clone()
        try {
            if ($case.TaskName) {
                $global:HytaleFixtureTasks[$case.TaskName] = [pscustomobject]@{
                    TaskName = $case.TaskName; Actions = @([pscustomobject]@{ Execute = 'foreign.exe'; Arguments = 'preserve me' }); State = 'Ready'
                }
            }
            if ($case.UserSid) { $testOptions.UserSid = $case.UserSid }
            $snapshot = Get-InstallFixtureSnapshot
            $failure = $null
            try { & (Join-Path $package 'Install.ps1') @testOptions } catch { $failure = $_ }
            Check ($failure -and $failure.Exception.Message -eq $case.Error) "$Phase rejects $($case.Name)"
            $after = Get-InstallFixtureSnapshot
            Check ($after.Files -ceq $snapshot.Files) "$Phase rejection preserves files, directories and shortcuts"
            Check ($after.Registry -ceq $snapshot.Registry) "$Phase rejection preserves registry entries"
            Check ($after.Tasks -ceq $snapshot.Tasks) "$Phase rejection preserves all tasks"
        } finally { $global:HytaleFixtureTasks = $savedTasks }
    }
}
function Remove-Fixture([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $root -and !$full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped its root.' }
    $item = Get-Item -LiteralPath $full -Force
    if (($item.Attributes -band [IO.FileAttributes]::Directory) -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        foreach ($child in Get-ChildItem -LiteralPath $full -Force) { Remove-Fixture $child.FullName }
        [IO.Directory]::Delete($full)
    } elseif ($item.Attributes -band [IO.FileAttributes]::Directory) { [IO.Directory]::Delete($full) }
    else { Remove-Item -LiteralPath $full -Force }
}
try {
    $package = Join-Path $root 'package'
    $programs = Join-Path $root 'shortcuts'
    $desktop = Join-Path $root 'desktop'
    $commonPrograms = Join-Path $root 'common-programs'
    $commonDesktop = Join-Path $root 'common-desktop'
    $options = @{ ProgramsDirectory = $programs; DesktopDirectory = $desktop; CommonProgramsDirectory = $commonPrograms; CommonDesktopDirectory = $commonDesktop; RegistryPath = $registry; UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
    $env:LOCALAPPDATA = Join-Path $root 'local'
    $env:ProgramFiles = Join-Path $root 'program-files'
    $official = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
    foreach ($folder in @($package, $programs, (Split-Path $official))) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
    foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs','Repair-Integration.ps1')) { Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $package }
    Set-Content -LiteralPath (Join-Path $package 'HytaleAccounts.exe') -Value 'non-executed helper fixture'
    Set-Content -LiteralPath $official -Value 'official launcher fixture'
    . (Join-Path $package 'InstallerSafety.ps1')
    $before = Get-LauncherSnapshot $official
    $destination = Join-Path (Split-Path $official) '.hytale-accounts-addon'
    $protected = @('Accounts\one\account.dat','Accounts\one\.keys\key','install\game','UserData\world','credentials.dpapi') | ForEach-Object { Join-Path $root $_ }
    foreach ($file in $protected) { New-Item -ItemType Directory -Path (Split-Path $file) -Force | Out-Null; Set-Content -LiteralPath $file -Value 'opaque preserved bytes' }
    $hashes = @{}; foreach ($file in $protected) { $hashes[$file] = (Get-FileHash -LiteralPath $file).Hash }
    $shell = New-Object -ComObject WScript.Shell
    $shortcutPath = Join-Path $programs 'Hytale Launcher.lnk'
    $previous = $shell.CreateShortcut($shortcutPath); $previous.TargetPath = $official
    $previous.Hotkey = 'CTRL+ALT+H'; $previous.WindowStyle = 7; $previous.Description = 'Original Hytale shortcut'; $previous.Save()
    $previousHash = (Get-FileHash -LiteralPath $shortcutPath).Hash
    $previousHotkey = $shell.CreateShortcut($shortcutPath).Hotkey
    $originalLinks = @{$shortcutPath = $previousHash}
    foreach ($path in @((Join-Path $commonPrograms 'Hytale Launcher\Hytale Launcher.lnk'),(Join-Path $commonDesktop 'Hytale Launcher.lnk'))) {
        New-Item -ItemType Directory -Path (Split-Path $path) -Force | Out-Null
        $previous = $shell.CreateShortcut($path); $previous.TargetPath = $official; $previous.Save()
        $originalLinks[$path] = (Get-FileHash -LiteralPath $path).Hash
    }
    $officialUninstall = Join-Path $commonPrograms 'Hytale Launcher\Uninstall Hytale Launcher.lnk'
    $previous = $shell.CreateShortcut($officialUninstall); $previous.TargetPath = Join-Path (Split-Path $official) 'uninstall.exe'; $previous.Save()
    $uninstallHash = (Get-FileHash -LiteralPath $officialUninstall).Hash
    # SetupChecks exercises the production orchestration; these phases use real scripts on disposable links.
    foreach ($failureCode in @(1223, 2)) {
        & (Join-Path $package 'Install.ps1') @options
        & (Join-Path $package 'Install.ps1') @options -UserOnly
        $userState = Join-Path $env:LOCALAPPDATA 'HytaleAccounts\ShortcutIntegration'
        $userManifest = Join-Path $userState 'shortcuts.json'
        $userJournal = Get-Content -LiteralPath $userManifest -Raw | ConvertFrom-Json
        $recoveryHashes = @{}
        foreach ($name in @('shortcuts.json') + @($userJournal.Shortcuts | ForEach-Object Backup)) {
            $path = Join-Path $userState $name
            $recoveryHashes[$path] = (Get-FileHash -LiteralPath $path).Hash
        }
        $installedHash = (Get-FileHash -LiteralPath (Join-Path $destination 'HytaleAccounts.exe')).Hash
        $entry = Get-ItemProperty -LiteralPath $registry | ConvertTo-Json -Compress
        $tasks = @($global:HytaleFixtureTasks.Values | Sort-Object TaskName) | ConvertTo-Json -Depth 6 -Compress
        & (Join-Path $destination 'Uninstall.ps1') @options -UserOnly -KeepJournal
        Check ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $previousHash) 'pre-elevation phase restores the original shortcut'
        foreach ($path in $recoveryHashes.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $recoveryHashes[$path]) 'pre-elevation phase retains journal and original backup bytes' }
        $failure = $null
        try { & { throw [ComponentModel.Win32Exception]::new($failureCode) } }
        catch {
            $failure = $_
            & (Join-Path $destination 'Install.ps1') @options -UserOnly
        }
        Check ($failure.Exception.NativeErrorCode -eq $failureCode) 'mock elevation cancels or fails after the per-user phase'
        Check ((Get-FileHash -LiteralPath (Join-Path $destination 'HytaleAccounts.exe')).Hash -eq $installedHash) 'failed elevation leaves the installed addon intact'
        Check ((Get-ItemProperty -LiteralPath $registry | ConvertTo-Json -Compress) -ceq $entry) 'failed elevation retains the existing Windows Apps entry'
        Check ((@($global:HytaleFixtureTasks.Values | Sort-Object TaskName) | ConvertTo-Json -Depth 6 -Compress) -ceq $tasks) 'failed elevation retains both repair tasks'
        $link = $shell.CreateShortcut($shortcutPath)
        Check ($link.TargetPath -eq (Join-Path $destination 'HytaleAccounts.exe') -and !$link.Arguments -and $link.Hotkey -eq $previousHotkey -and $link.WindowStyle -eq 7) 'failed elevation restores addon launch through the same shortcut with its metadata'
        $journal = Get-Content -LiteralPath $userManifest -Raw | ConvertFrom-Json
        Check ($journal.Shortcuts.Count -eq 1 -and (Get-FileHash -LiteralPath (Join-Path $userState $journal.Shortcuts[0].Backup)).Hash -eq $previousHash) 'rollback retains a usable original restoration record'
        & (Join-Path $destination 'Uninstall.ps1') @options -UserOnly -KeepJournal
        & (Join-Path $destination 'Repair-Integration.ps1') -UserOnly
        Check ($shell.CreateShortcut($shortcutPath).TargetPath -eq (Join-Path $destination 'HytaleAccounts.exe')) 'retained journal lets background repair recover if immediate rollback is unavailable'
        # Simulate repair during UAC, then the successful-start finalization before machine removal.
        & (Join-Path $destination 'Uninstall.ps1') @options -UserOnly
        & (Join-Path $destination 'Repair-Integration.ps1') -UserOnly
        Check ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $previousHash -and !(Test-Path -LiteralPath $userManifest)) 'finalization restores original bytes and prevents repair from reapplying the addon'
        & (Join-Path $package 'Uninstall.ps1') @options
        foreach ($path in $originalLinks.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $originalLinks[$path]) 'later uninstall restores existing shortcuts byte-for-byte' }
        $allLinks = @($programs,$desktop,$commonPrograms,$commonDesktop) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.lnk' -Recurse }
        Check ($allLinks.Count -eq 4 -and (Get-FileHash -LiteralPath $officialUninstall).Hash -eq $uninstallHash) 'failed elevation and retry create no duplicate shortcuts'
        Check (!(Test-Path -LiteralPath $destination) -and !(Test-Path -LiteralPath $registry) -and !$global:HytaleFixtureTasks.Count) 'later uninstall removes only addon installation, entry and owned tasks'
        Check (@($protected | Where-Object { (Get-FileHash -LiteralPath $_).Hash -ne $hashes[$_] }).Count -eq 0) 'cancellation recovery and retry preserve Hytale accounts and game data'
        Assert-LauncherSnapshot $official $before
    }
    Check-InstallPreflight 'Fresh installation'
    & (Join-Path $package 'Install.ps1') @options
    Check ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $previousHash) 'machine installation leaves per-user shortcuts for the unelevated phase'
    & (Join-Path $package 'Install.ps1') @options -UserOnly
    Assert-LauncherSnapshot $official $before
    Check (Test-Path -LiteralPath (Join-Path $destination 'launcher-baseline.json')) 'installation retains launcher byte/permission baseline'
    foreach ($path in $originalLinks.Keys) {
        Check ($shell.CreateShortcut($path).TargetPath -eq (Join-Path $destination 'HytaleAccounts.exe')) 'existing Hytale shortcut uses addon with its original name and location'
    }
    $allLinks = @($programs,$desktop,$commonPrograms,$commonDesktop) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.lnk' -Recurse }
    Check ($allLinks.Count -eq 4 -and !@($allLinks | Where-Object Name -match 'with Accounts|Uninstall Hytale Accounts').Count) 'installation creates no duplicate launcher or addon uninstall shortcut'
    Check ((Get-FileHash -LiteralPath $officialUninstall).Hash -eq $uninstallHash) 'official Hytale uninstaller shortcut remains untouched'
    Check ((Get-ItemProperty -LiteralPath $registry).UninstallString -like '*--uninstall') 'Windows uninstall entry invokes the same executable'
    Set-Content -LiteralPath (Join-Path $destination 'HytaleAccounts.exe') -Value 'previous non-executed helper fixture'
    Set-ItemProperty -LiteralPath $registry -Name DisplayVersion -Value 'previous fixture version'
    Check-InstallPreflight 'Existing installation'
    $wrongUser = $options.Clone(); $wrongUser.UserSid = 'S-1-5-21-1-2-3-1001'
    foreach ($operation in @('Install.ps1','Uninstall.ps1')) {
        $ownedHash = (Get-FileHash -LiteralPath $shortcutPath).Hash
        $rejected = $false
        try { & (Join-Path $package $operation) @wrongUser } catch { $rejected = $true }
        Check ($rejected -and (Get-FileHash -LiteralPath $shortcutPath).Hash -eq $ownedHash) 'another Windows account cannot orphan the owner shortcut journal'
    }
    foreach ($lockedName in @('HytaleAccounts.exe','Repair-Integration.ps1','launcher-baseline.json','shortcuts.new','shortcuts.json')) {
        & (Join-Path $package 'Install.ps1') @options
        & (Join-Path $package 'Install.ps1') @options -UserOnly
        & (Join-Path $package 'Uninstall.ps1') @options -UserOnly
        Set-Content -LiteralPath (Join-Path $destination 'shortcuts.new') -Value 'unfinished journal fixture'
        $manifest = Join-Path $destination 'shortcuts.json'
        $journal = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $recovery = @{}
        foreach ($name in @('shortcuts.json','shortcuts.new','HytaleAccounts.exe','Install.ps1','Uninstall.ps1','InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs','Repair-Integration.ps1','launcher-baseline.json') + @($journal.Shortcuts | ForEach-Object Backup)) {
            $path = Join-Path $destination $name
            $recovery[$path] = (Get-FileHash -LiteralPath $path).Hash
        }
        $uninstallString = (Get-ItemProperty -LiteralPath $registry).UninstallString
        # Journal locks allow reads and writes but deny deletion; payload locks also deny writes.
        $sharing = if ($lockedName -eq 'shortcuts.json') { 'ReadWrite' } else { 'Read' }
        $locked = [IO.File]::Open((Join-Path $destination $lockedName), 'Open', 'Read', $sharing)
        $failure = $null
        try { & (Join-Path $destination 'Uninstall.ps1') @options } catch { $failure = $_ }
        finally { $locked.Dispose() }
        Check ($failure -and $failure.Exception.GetBaseException() -is [ComponentModel.Win32Exception] -and $failure.Exception.GetBaseException().NativeErrorCode -eq 32) ("locked $lockedName rejects removal with a sharing violation")
        foreach ($path in $originalLinks.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $originalLinks[$path]) 'failed removal leaves original user and system shortcuts restored' }
        $missing = @($recovery.Keys | Where-Object { !(Test-Path -LiteralPath $_) })
        Check (!$missing.Count -and (Test-Path -LiteralPath $registry)) ('failed removal retains recovery files and Windows Apps entry; missing: ' + ($missing -join ', ') + '; Apps entry: ' + (Test-Path -LiteralPath $registry))
        Check ((Get-ItemProperty -LiteralPath $registry).UninstallString -ceq $uninstallString -and $uninstallString -ceq ('"' + (Join-Path $destination 'HytaleAccounts.exe') + '" --uninstall')) 'failed removal retains the executable entry point for retry from Windows Apps'
        foreach ($path in $recovery.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $recovery[$path]) 'failed removal preserves recovery bytes' }
        $rejected = $false
        try { & (Join-Path $destination 'Uninstall.ps1') @wrongUser } catch { $rejected = $true }
        Check $rejected 'retry still enforces the shortcut journal owner'
        & (Join-Path $destination 'Uninstall.ps1') @options -UserOnly
        & (Join-Path $destination 'Uninstall.ps1') @options
        Check (!(Test-Path -LiteralPath $destination)) 'retry after releasing the lock removes addon files and machine recovery state'
        Check (@($protected | Where-Object { (Get-FileHash -LiteralPath $_).Hash -ne $hashes[$_] }).Count -eq 0) 'failed removal and retry preserve Hytale data'
        Assert-LauncherSnapshot $official $before
        Check ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $previousHash) 'uninstall restores the exact previous shortcut after repeated install'
        foreach ($path in $originalLinks.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $originalLinks[$path]) 'uninstall restores existing user and system shortcuts byte-for-byte' }
        Check (!(Test-Path -LiteralPath $registry)) 'Windows addon entry removed'
    }
    & (Join-Path $package 'Install.ps1') @options
    & (Join-Path $package 'Install.ps1') @options -UserOnly
    # Simulate the old machine journal's per-user records, then migrate them with real installer code.
    $userState = Join-Path $env:LOCALAPPDATA 'HytaleAccounts\ShortcutIntegration'
    $userManifest = Join-Path $userState 'shortcuts.json'
    $userJournal = Get-Content -LiteralPath $userManifest -Raw | ConvertFrom-Json
    $machineManifest = Join-Path $destination 'shortcuts.json'
    $machineJournal = Get-Content -LiteralPath $machineManifest -Raw | ConvertFrom-Json
    foreach ($record in $userJournal.Shortcuts) { Copy-Item -LiteralPath (Join-Path $userState $record.Backup) -Destination (Join-Path $destination $record.Backup) }
    $machineJournal.Roots = @($machineJournal.Roots) + @($userJournal.Roots)
    $machineJournal.Shortcuts = @($machineJournal.Shortcuts) + @($userJournal.Shortcuts)
    $machineJournal | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $machineManifest
    Remove-Fixture $userState
    & (Join-Path $package 'Install.ps1') @options
    & (Join-Path $package 'Install.ps1') @options -UserOnly
    $migrated = Get-Content -LiteralPath $userManifest -Raw | ConvertFrom-Json
    Check ($migrated.Shortcuts.Count -eq 1 -and (Get-FileHash -LiteralPath (Join-Path $userState $migrated.Shortcuts[0].Backup)).Hash -eq $previousHash) 'legacy user backup migrates without replacing it with the addon shortcut'
    Set-Content -LiteralPath $official -Value 'new official update fixture'
    $updated = Get-LauncherSnapshot $official
    $updater = $shell.CreateShortcut($shortcutPath); $updater.TargetPath = $official; $updater.Description = 'Updated official shortcut'; $updater.Save()
    $updatedLink = (Get-FileHash -LiteralPath $shortcutPath).Hash
    & (Join-Path $destination 'Repair-Integration.ps1')
    Check ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $updatedLink) 'SYSTEM repair leaves migrated user shortcuts to their limited task'
    & (Join-Path $destination 'Repair-Integration.ps1') -UserOnly
    Check ($shell.CreateShortcut($shortcutPath).TargetPath -eq (Join-Path $destination 'HytaleAccounts.exe')) 'official update shortcut automatically returns to addon'
    & (Join-Path $destination 'Repair-Integration.ps1')
    & (Join-Path $destination 'Repair-Integration.ps1') -UserOnly
    Assert-LauncherSnapshot $official $updated
    & (Join-Path $package 'Uninstall.ps1') @options -UserOnly
    & (Join-Path $package 'Uninstall.ps1') @options
    Check ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $updatedLink) 'uninstall restores latest official updater shortcut, not an obsolete backup'
    Check ($global:HytaleFixtureTasks.Count -eq 0) 'uninstall removes hidden update repair task'
    & (Join-Path $package 'Install.ps1') @options
    & (Join-Path $package 'Install.ps1') @options -UserOnly
    $foreign = $shell.CreateShortcut($shortcutPath); $foreign.TargetPath = Join-Path $root 'other.exe'; $foreign.Save()
    $foreignHash = (Get-FileHash -LiteralPath $shortcutPath).Hash
    & (Join-Path $destination 'Repair-Integration.ps1')
    & (Join-Path $destination 'Repair-Integration.ps1') -UserOnly
    Check ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $foreignHash) 'automatic repair leaves user-selected shortcut targets alone'
    & (Join-Path $package 'Uninstall.ps1') @options -UserOnly
    & (Join-Path $package 'Uninstall.ps1') @options
    Assert-LauncherSnapshot $official $updated
    Check ((Get-FileHash -LiteralPath $shortcutPath).Hash -eq $foreignHash) 'uninstall preserves a replaced foreign shortcut and updated launcher'
    Check (!(Test-Path -LiteralPath (Join-Path $destination 'HytaleAccounts.exe'))) 'addon executable removed'
    Check (@($protected | Where-Object { (Get-FileHash -LiteralPath $_).Hash -ne $hashes[$_] }).Count -eq 0) 'opaque stores, encryption keys, installation and UserData preserved'
    $link = Join-Path $root 'linked'
    New-Item -ItemType Junction -Path $link -Target $env:LOCALAPPDATA | Out-Null
    $rejected = $false
    try { Assert-RegularPath (Join-Path $link 'HytaleAccounts\NativeLauncher\missing.exe') } catch { $rejected = $true }
    Check $rejected 'linked ancestor rejected even with missing leaf'
    & (Join-Path $package 'Install.ps1') @options
    & (Join-Path $package 'Install.ps1') @options -UserOnly
    $manifest = Join-Path $destination 'shortcuts.json'
    $journal = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $valid = Get-Content -LiteralPath $manifest -Raw
    $journal.Shortcuts[0].Backup = '..\outside.lnk'
    ConvertTo-Json -InputObject $journal -Depth 4 | Set-Content -LiteralPath $manifest
    $rejected = $false
    try { & (Join-Path $package 'Uninstall.ps1') @options } catch { $rejected = $true }
    Check ($rejected -and (Test-Path -LiteralPath (Join-Path $destination 'HytaleAccounts.exe'))) 'invalid restoration journal rejected before removal'
    Set-Content -LiteralPath $manifest -Value $valid
    & (Join-Path $package 'Uninstall.ps1') @options -UserOnly
    & (Join-Path $package 'Uninstall.ps1') @options
    New-Item -Path $registry -Force | Out-Null
    New-ItemProperty -Path $registry -Name InstallLocation -Value 'foreign installation' -Force | Out-Null
    $rejected = $false
    try { & (Join-Path $package 'Install.ps1') @options } catch { $rejected = $true }
    Check ($rejected -and (Get-Content -LiteralPath (Join-Path $package 'install-error.txt') -Raw).Contains('Another installation owns')) 'setup preserves the actual failure for its error dialog'
    & {
        $fixtureHelper = [pscustomobject]@{ Closed = $false }
        $fixtureHelper | Add-Member ScriptMethod CloseMainWindow { $this.Closed = $true; return $true }
        $fixtureHelper | Add-Member ScriptMethod WaitForExit { param($Timeout) return $this.Closed }
        function Get-CimInstance { param($ClassName,$Filter)
            if ($Filter -like '*HytaleAccounts*') { [pscustomobject]@{ ExecutablePath = 'fixture-helper'; ProcessId = 123 } }
        }
        function Get-Process { param($Name,$Id,$ErrorAction) if ($Id -eq 123) { $fixtureHelper } }
        Stop-InstalledLauncher 'fixture-helper'
        Check $fixtureHelper.Closed 'setup closes a leftover helper error dialog before waiting for exit'
    }
} finally {
    if (Test-Path -LiteralPath $registry) { Remove-Item -LiteralPath $registry }
    $env:LOCALAPPDATA = $oldLocal
    $env:ProgramFiles = $oldPrograms
    if (Test-Path -LiteralPath $root) { Remove-Fixture $root }
    Remove-Variable HytaleFixtureTasks -Scope Global -ErrorAction SilentlyContinue
}
