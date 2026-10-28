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
function Interrupt-ShortcutWrite($Fixture) {
    Check ($Fixture.Replacement.Length -gt $Fixture.Original.Length) 'partial shortcut overwrite has a longer replacement and an original tail'
    # Find a torn native structure in these COM-generated links; offsets depend on the temp path.
    for ($prefixLength = 76; $prefixLength -lt $Fixture.Original.Length; $prefixLength += 8) {
        [IO.File]::WriteAllBytes($Fixture.Path, $Fixture.Original)
        $stream = [IO.File]::Open($Fixture.Path, 'Open', 'Write', 'None')
        try { $stream.Write($Fixture.Replacement, 0, $prefixLength); $stream.Flush($true) } finally { $stream.Dispose() }
        if (!$shell.CreateShortcut($Fixture.Path).TargetPath) { break }
    }
    Check ((Get-Item -LiteralPath $Fixture.Path).Length -eq $Fixture.Original.Length -and !$shell.CreateShortcut($Fixture.Path).TargetPath) 'partial in-place overwrite leaves an unreadable shortcut at the original length'
}
function Get-InstallFixtureSnapshot {
    [pscustomobject]@{
        # Setup's diagnostic is expected on rejection; installation state must not change.
        Files = (@(Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object FullName -ne (Join-Path $package 'install-error.txt') | Sort-Object FullName | ForEach-Object {
            [pscustomobject]@{ Path = $_.FullName; Hash = if (!$_.PSIsContainer) { (Get-FileHash -LiteralPath $_.FullName).Hash }; Permissions = (Get-Acl -LiteralPath $_.FullName).Sddl }
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
function Interrupt-AddonUpgrade([string]$Point) {
    $configPath = Join-Path $root 'crash-config.xml'
    $signal = Join-Path $root 'crash-ready'
    $driver = Join-Path $root 'crash-child.ps1'
    @{ Local = $env:LOCALAPPDATA; Programs = $env:ProgramFiles; Package = $package; Options = $options; Point = $Point; Signal = $signal
        Tasks = @($global:HytaleFixtureTasks.Values | ForEach-Object { [pscustomobject]@{ TaskName = $_.TaskName; Actions = @($_.Actions | Select-Object Execute,Arguments); State = $_.State } }) } | Export-Clixml -LiteralPath $configPath
    $childSource = @'
param($ConfigPath)
$ErrorActionPreference = 'Stop'
$config = Import-Clixml -LiteralPath $ConfigPath
$env:LOCALAPPDATA = $config.Local
$env:ProgramFiles = $config.Programs
Import-Module ScheduledTasks
$global:HytaleFixtureTasks = @{}
foreach ($task in $config.Tasks) { $global:HytaleFixtureTasks[$task.TaskName] = $task }
'@
    foreach ($name in @('Check','Register-ScheduledTask','Get-ScheduledTask','Disable-ScheduledTask','Stop-ScheduledTask','Unregister-ScheduledTask','Get-CimInstance')) {
        $childSource += "`r`nfunction $name {" + (Get-Item -LiteralPath "function:$name").Definition + "`r`n}`r`n"
    }
    $childSource += @'
$source = Get-Content -LiteralPath (Join-Path $config.Package 'ShortcutFile.cs') -Raw
$write = 'stream.Position = 0; stream.Write(bytes, 0, bytes.Length);'
Check ($source.Contains($write)) 'crash staging boundary found'
$source = $source.Replace($write, ('stream.Position = 0; if (InstallerCrashProbe.Point == "stage" && System.IO.Path.GetFileName(path).EndsWith("-Repair-Integration.ps1.new")) { stream.Write(bytes, 0, bytes.Length / 2); stream.Flush(true); InstallerCrashProbe.Pause(); } ' + $write))
$publish = 'else File.Move(staged.path, path);'
Check ($source.Contains($publish)) 'crash publication boundary found'
$source = $source.Replace($publish, ($publish + ' if (InstallerCrashProbe.Point == System.IO.Path.GetFileName(path)) InstallerCrashProbe.Pause();'))
$source += 'public static class InstallerCrashProbe {
    public static string Point, Signal;
    public static void Pause() {
        using (var file = new System.IO.FileStream(Signal, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write)) { file.WriteByte(1); file.Flush(true); }
        System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
    }
}'
Add-Type -TypeDefinition $source
[InstallerCrashProbe]::Point = $config.Point
[InstallerCrashProbe]::Signal = $config.Signal
$options = $config.Options
& (Join-Path $config.Package 'Install.ps1') @options
throw 'Crash checkpoint was not reached.'
'@
    Set-Content -LiteralPath $driver -Value $childSource
    foreach ($path in @($signal,(Join-Path $package 'install-error.txt'))) { if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) } }
    $child = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"' + $driver + '"'),('"' + $configPath + '"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'crash-output.txt') -RedirectStandardError (Join-Path $root 'crash-error.txt')
    try {
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath $signal) -and !$child.HasExited -and $deadline.Elapsed.TotalSeconds -lt 30) { Start-Sleep -Milliseconds 50 }
        if (!(Test-Path -LiteralPath $signal)) { throw "Crash child did not reach $Point : $(Get-Content -LiteralPath (Join-Path $root 'crash-error.txt') -Raw)" }
        $child.Kill()
        Check ($child.WaitForExit(5000)) "$Point subprocess forcibly terminated"
        Check (!(Test-Path -LiteralPath (Join-Path $package 'install-error.txt'))) "$Point bypasses installer catch and finally rollback"
    } finally {
        if (!$child.HasExited) { $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
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
    $scripts = @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs','Repair-Integration.ps1')
    foreach ($name in $scripts) {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $package
        $comment = if ($name -like '*.cs') { '//' } else { '#' }
        Add-Content -LiteralPath (Join-Path $package $name) -Value "`r`n$comment OLD addon payload: $name"
    }
    Set-Content -LiteralPath (Join-Path $package 'HytaleAccounts.exe') -Value 'OLD non-executed helper fixture'
    # Only the fixture's loaded class has fault hooks; shipped payloads have none.
    $source = Get-Content -LiteralPath (Join-Path $package 'ShortcutFile.cs') -Raw
    $write = 'stream.Position = 0; stream.Write(bytes, 0, bytes.Length);'
    Check ($source.Contains($write)) 'installer write-fault boundary found'
    $source = $source.Replace($write, ($write + ' if (InstallerWriteProbe.AfterWrite != null) InstallerWriteProbe.AfterWrite(bytes);'))
    $publish = 'else File.Move(staged.path, path);'
    Check ($source.Contains($publish)) 'installer publication-fault boundary found'
    $source = $source.Replace($publish, ($publish + ' if (InstallerWriteProbe.AfterPublish != null) InstallerWriteProbe.AfterPublish(path);'))
    $source += 'public static class InstallerWriteProbe { public static System.Action<byte[]> AfterWrite; public static System.Action<string> AfterPublish; }'
    Add-Type -TypeDefinition $source
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
    # Machine roots are scanned desktop first, then Programs; deny writes/deletion on each matching link.
    $firstShortcut = Join-Path $commonDesktop 'Hytale Launcher.lnk'
    $secondShortcut = Join-Path $commonPrograms 'Hytale Launcher\Hytale Launcher.lnk'
    foreach ($lockedShortcut in @($secondShortcut, $firstShortcut)) {
        $locked = [IO.File]::Open($lockedShortcut, 'Open', 'Read', 'Read')
        $failure = $null
        try { & (Join-Path $package 'Install.ps1') @options } catch { $failure = $_ }
        finally { $locked.Dispose() }
        Check ($failure -and $failure.Exception.GetBaseException() -is [ComponentModel.Win32Exception] -and $failure.Exception.GetBaseException().NativeErrorCode -eq 32) "installation fails with a sharing violation on $lockedShortcut"
        $installed = Join-Path $destination 'HytaleAccounts.exe'
        $changedCount = [int]($lockedShortcut -eq $secondShortcut)
        $firstTarget = if ($changedCount) { $installed } else { $official }
        Check ($shell.CreateShortcut($firstShortcut).TargetPath -eq $firstTarget -and (Get-FileHash -LiteralPath $secondShortcut).Hash -eq $originalLinks[$secondShortcut]) "failure occurs after $changedCount shortcut changes and preserves the locked shortcut"
        Check (Test-Path -LiteralPath $registry) 'partial installation retains a Windows Apps removal entry'
        $entry = Get-ItemProperty -LiteralPath $registry
        Check ($entry.DisplayName -eq 'Hytale Accounts addon' -and $entry.InstallLocation -eq $destination -and $entry.UninstallString -ceq ('"' + $installed + '" --uninstall')) 'partial installation registers the installed helper as the removal entry point'
        foreach ($name in @('HytaleAccounts.exe') + $scripts) {
            Check ((Get-FileHash -LiteralPath (Join-Path $destination $name)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $package $name)).Hash) "partial installation retains usable removal payload: $name"
        }
        $journal = Get-Content -LiteralPath (Join-Path $destination 'shortcuts.json') -Raw | ConvertFrom-Json
        Check ($journal.UserSid -eq $options.UserSid -and $journal.Shortcuts.Count -eq $changedCount) 'partial installation retains a journal even before the first shortcut can change'
        foreach ($record in $journal.Shortcuts) {
            Check ($record.Path -eq $firstShortcut -and (Get-FileHash -LiteralPath (Join-Path $destination $record.Backup)).Hash -eq $originalLinks[$firstShortcut]) 'partial installation journals the changed shortcut and its exact original bytes'
        }
        # Use only the installed removal script, without executing the synthetic helper or requesting elevation.
        & (Join-Path $destination 'Uninstall.ps1') @options
        foreach ($path in $originalLinks.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $originalLinks[$path]) 'removal after failed installation restores original shortcut bytes' }
        Check (!(Test-Path -LiteralPath $destination) -and !(Test-Path -LiteralPath $registry) -and !$global:HytaleFixtureTasks.Count) 'removal after failed installation clears addon recovery state and Windows Apps entry'
        Check ((Get-FileHash -LiteralPath $officialUninstall).Hash -eq $uninstallHash) 'failed installation and removal preserve the official uninstall shortcut'
        Check (@($protected | Where-Object { (Get-FileHash -LiteralPath $_).Hash -ne $hashes[$_] }).Count -eq 0) 'failed installation and removal preserve Hytale accounts and game data'
        Assert-LauncherSnapshot $official $before
    }
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
    $uninstallPath = Join-Path $destination 'Uninstall.ps1'
    $acl = Get-Acl -LiteralPath $uninstallPath
    $acl.SetAccessRuleProtection($true, $true)
    Set-Acl -LiteralPath $uninstallPath -AclObject $acl
    foreach ($name in $scripts) {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination (Join-Path $package $name) -Force
        $comment = if ($name -like '*.cs') { '//' } else { '#' }
        Add-Content -LiteralPath (Join-Path $package $name) -Value "`r`n$comment NEW addon payload: $name"
    }
    Set-Content -LiteralPath (Join-Path $package 'HytaleAccounts.exe') -Value 'NEW non-executed helper fixture with different length'
    Set-ItemProperty -LiteralPath $registry -Name DisplayVersion -Value 'previous fixture version'
    Check-InstallPreflight 'Existing installation'
    $replacementNames = @('HytaleAccounts.exe') + $scripts + @('launcher-baseline.json')
    $oldPayload = @{}
    foreach ($name in $replacementNames) {
        $path = Join-Path $destination $name
        $oldPayload[$name] = [pscustomobject]@{ Bytes = [IO.File]::ReadAllBytes($path); Hash = (Get-FileHash -LiteralPath $path).Hash; Permissions = (Get-Acl -LiteralPath $path).Sddl }
    }
    foreach ($name in @('HytaleAccounts.exe') + $scripts) {
        Check ($oldPayload[$name].Hash -ne (Get-FileHash -LiteralPath (Join-Path $package $name)).Hash) "upgrade fixture has distinct old/new $name bytes"
    }
    # This subprocess is killed externally, so no PowerShell exception rollback can run.
    foreach ($point in @('stage','Repair-Integration.ps1','HytaleAccounts.exe')) {
        foreach ($name in $replacementNames) { [IO.File]::WriteAllBytes((Join-Path $destination $name), $oldPayload[$name].Bytes) }
        $recovery = @{}
        foreach ($state in @($destination,(Join-Path $env:LOCALAPPDATA 'HytaleAccounts\ShortcutIntegration'))) {
            foreach ($file in Get-ChildItem -LiteralPath $state -File | Where-Object Name -match '^shortcuts\.json$|^shortcut-.*\.lnk$') { $recovery[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
        }
        $entry = Get-ItemProperty -LiteralPath $registry | ConvertTo-Json -Compress
        $tasks = @($global:HytaleFixtureTasks.Values | Sort-Object TaskName) | ConvertTo-Json -Depth 6 -Compress
        Interrupt-AddonUpgrade $point
        foreach ($name in $replacementNames) {
            $path = Join-Path $destination $name
            $newHash = if ($name -eq 'launcher-baseline.json') { $oldPayload[$name].Hash } else { (Get-FileHash -LiteralPath (Join-Path $package $name)).Hash }
            $hash = (Get-FileHash -LiteralPath $path).Hash
            Check ($hash -in @($oldPayload[$name].Hash,$newHash)) "$point leaves complete old or new $name bytes"
            $published = $point -eq 'HytaleAccounts.exe' -or ($point -eq 'Repair-Integration.ps1' -and $name -notin @('HytaleAccounts.exe','launcher-baseline.json'))
            $expected = if ($published) { $newHash } else { $oldPayload[$name].Hash }
            Check ($hash -eq $expected) "$point publishes $name in order with helper last"
            Check ((Get-Acl -LiteralPath $path).Sddl -ceq $oldPayload[$name].Permissions) "$point preserves $name permissions"
        }
        $backups = @(Get-ChildItem -LiteralPath $destination -Filter '.upgrade-*.bak' -Force)
        Check ($backups.Count -eq $(if ($point -eq 'stage') { 6 } else { $replacementNames.Count })) "$point retains flushed previous payload backups"
        foreach ($backup in $backups) {
            $name = $backup.Name.Substring(42); $name = $name.Substring(0, $name.Length - 4)
            Check ((Get-FileHash -LiteralPath $backup.FullName).Hash -eq $oldPayload[$name].Hash) "$point retains exact $name recovery bytes"
        }
        foreach ($path in $recovery.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $recovery[$path]) "$point retains original shortcut restoration information" }
        Check ((Get-ItemProperty -LiteralPath $registry | ConvertTo-Json -Compress) -ceq $entry -and (@($global:HytaleFixtureTasks.Values | Sort-Object TaskName) | ConvertTo-Json -Depth 6 -Compress) -ceq $tasks) "$point preserves Windows Apps removal and task ownership"
        if ($point -eq 'Repair-Integration.ps1') {
            Check-InstallPreflight 'Interrupted installation'
            $snapshot = Get-InstallFixtureSnapshot
            $locked = [IO.File]::Open($backups[0].FullName, 'Open', 'Read', 'Read')
            $failure = $null
            try { & (Join-Path $package 'Install.ps1') @options } catch { $failure = $_ }
            finally { $locked.Dispose() }
            Check ($failure -and (Get-InstallFixtureSnapshot).Files -ceq $snapshot.Files) 'retry preserves interrupted state when a retained backup is locked'
            $linkedBackup = Join-Path $destination ('.upgrade-' + [Guid]::NewGuid().ToString('N') + '-Install.ps1.bak')
            New-Item -ItemType HardLink -Path $linkedBackup -Target $protected[0] | Out-Null
            $failure = $null
            try { & (Join-Path $package 'Install.ps1') @options } catch { $failure = $_ }
            finally { [IO.File]::Delete($linkedBackup) }
            Check ($failure -and $failure.ToString().Contains('link or unexpected file type')) 'retry rejects a linked retained backup'
            Check ((Get-FileHash -LiteralPath $protected[0]).Hash -eq $hashes[$protected[0]]) 'linked recovery artifact leaves outside bytes intact'
        }
        & (Join-Path $package 'Install.ps1') @options
        foreach ($name in @('HytaleAccounts.exe') + $scripts) { Check ((Get-FileHash -LiteralPath (Join-Path $destination $name)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $package $name)).Hash) "$point retry publishes complete $name" }
        Check (!@(Get-ChildItem -LiteralPath $destination -Filter '.upgrade-*' -Force).Count) "$point successful retry clears interrupted upgrade files"
        foreach ($path in $recovery.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $recovery[$path]) "$point retry keeps shortcut restoration bytes" }
        Assert-LauncherSnapshot $official $before
        Check (@($protected | Where-Object { (Get-FileHash -LiteralPath $_).Hash -ne $hashes[$_] }).Count -eq 0) "$point crash and retry leave Hytale data intact"
    }
    # Include a missing destination in rollback, as when an upgrade introduces a script.
    foreach ($scenario in @('locked later destination','partial staged write','later publication','missing destination','rollback write failure')) {
        foreach ($name in $replacementNames) { [IO.File]::WriteAllBytes((Join-Path $destination $name), $oldPayload[$name].Bytes) }
        Set-ItemProperty -LiteralPath $registry -Name DisplayVersion -Value 'previous fixture version'
        $missingPath = Join-Path $destination 'InstallerSafety.ps1'
        if ($scenario -eq 'missing destination') { [IO.File]::Delete($missingPath) }
        $snapshot = Get-InstallFixtureSnapshot
        $locked = $null
        $probe = [pscustomobject]@{ WriteFaultReached = $false; Published = @() }
        try {
            if ($scenario -eq 'locked later destination') {
                $locked = [IO.File]::Open((Join-Path $destination 'Repair-Integration.ps1'), 'Open', 'Read', 'Read')
            } elseif ($scenario -eq 'partial staged write') {
                [InstallerWriteProbe]::AfterWrite = {
                    param($bytes)
                    $text = [Text.Encoding]::UTF8.GetString($bytes)
                    if ($text.Contains('# NEW addon payload: Repair-Integration.ps1')) {
                        [InstallerWriteProbe]::AfterWrite = $null
                        $probe.WriteFaultReached = $true
                        throw 'Controlled payload failure.'
                    }
                }
            } else {
                [InstallerWriteProbe]::AfterPublish = {
                    param($path)
                    $probe.Published += Split-Path $path -Leaf
                    if ($path -ne (Join-Path $destination 'Repair-Integration.ps1')) { return }
                    [InstallerWriteProbe]::AfterPublish = $null
                    Check ($probe.Published[0] -eq 'Install.ps1') 'earlier script publication completed before failure'
                    Check ('HytaleAccounts.exe' -notin $probe.Published) 'helper remains old until every support file is published'
                    $probe.WriteFaultReached = $true
                    throw 'Controlled payload failure.'
                }
                if ($scenario -eq 'rollback write failure') {
                    [InstallerWriteProbe]::AfterWrite = {
                        param($bytes)
                        if (!$probe.WriteFaultReached -or ![Text.Encoding]::UTF8.GetString($bytes).Contains('# OLD addon payload: Repair-Integration.ps1')) { return }
                        [InstallerWriteProbe]::AfterWrite = $null
                        throw 'Controlled rollback write failure.'
                    }
                }
            }
            $failure = $null
            try { & (Join-Path $package 'Install.ps1') @options } catch { $failure = $_ }
        } finally {
            if ($locked) { $locked.Dispose() }
            [InstallerWriteProbe]::AfterWrite = $null
            [InstallerWriteProbe]::AfterPublish = $null
        }
        if ($scenario -eq 'locked later destination') {
            Check ($null -ne $failure) 'upgrade rejects a locked later destination'
        } else {
            Check ($failure -and $probe.WriteFaultReached -and $failure.ToString().Contains('Controlled payload failure.')) "$scenario exercises controlled failure: $failure"
        }
        if ($scenario -eq 'rollback write failure') {
            $backups = @(Get-ChildItem -LiteralPath $destination -Filter '.upgrade-*.bak' -Force)
            Check ($backups.Count -eq $replacementNames.Count -and $failure.ToString().Contains('Controlled rollback write failure.') -and $failure.ToString().Contains('Previous payload backups retained')) 'rollback failure reports and retains the entire previous payload for recovery'
            foreach ($name in $replacementNames) {
                $backup = @($backups | Where-Object Name -like ('*-' + $name + '.bak'))
                Check ($backup.Count -eq 1 -and (Get-FileHash -LiteralPath $backup[0].FullName).Hash -eq $oldPayload[$name].Hash) "retained backup has the previous $name bytes"
            }
        }
        $after = Get-InstallFixtureSnapshot
        if ($scenario -ne 'rollback write failure') { Check ($after.Files -ceq $snapshot.Files) "$scenario preserves or restores all bytes, permissions, directories and shortcuts" }
        Check ($after.Registry -ceq $snapshot.Registry -and $after.Tasks -ceq $snapshot.Tasks) "$scenario preserves Windows Apps uninstall availability and task ownership"
        Assert-LauncherSnapshot $official $before
        Check (@($protected | Where-Object { (Get-FileHash -LiteralPath $_).Hash -ne $hashes[$_] }).Count -eq 0) "$scenario leaves Hytale data untouched"
        & (Join-Path $package 'Install.ps1') @options
        foreach ($name in @('HytaleAccounts.exe') + $scripts) {
            Check ((Get-FileHash -LiteralPath (Join-Path $destination $name)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $package $name)).Hash) "$scenario retry installs the new $name"
        }
        foreach ($name in $replacementNames) { Check ((Get-Acl -LiteralPath (Join-Path $destination $name)).Sddl -ceq $oldPayload[$name].Permissions) "$scenario retry preserves $name permissions" }
        Check (!@(Get-ChildItem -LiteralPath $destination -Filter '.upgrade-*' -Force).Count) "$scenario retry leaves no upgrade backups"
        Check ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq '0.1.3') "$scenario retry completes Windows Apps registration"
    }
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
    foreach ($recovery in @('Repair-Integration.ps1','Install.ps1')) {
        & (Join-Path $package 'Install.ps1') @options
        & (Join-Path $package 'Install.ps1') @options -UserOnly
        $fixtures = @()
        foreach ($user in @($true,$false)) {
            $fixtureState = if ($user) { Join-Path $env:LOCALAPPDATA 'HytaleAccounts\ShortcutIntegration' } else { $destination }
            $fixtureManifest = Join-Path $fixtureState 'shortcuts.json'
            $fixtureJournal = Get-Content -LiteralPath $fixtureManifest -Raw | ConvertFrom-Json
            $record = $fixtureJournal.Shortcuts[0]
            $backup = Join-Path $fixtureState $record.Backup
            $fixture = [pscustomobject]@{ User = $user; Path = $record.Path; Manifest = $fixtureManifest; Backup = $backup;
                Original = [IO.File]::ReadAllBytes($backup); Replacement = [IO.File]::ReadAllBytes($record.Path); BackupHash = (Get-FileHash -LiteralPath $backup).Hash; BackupWritten = (Get-Item -LiteralPath $backup).LastWriteTimeUtc }
            $fixtures += $fixture
            Interrupt-ShortcutWrite $fixture
        }
        if ($recovery -eq 'Install.ps1') {
            & (Join-Path $package $recovery) @options
            & (Join-Path $package $recovery) @options -UserOnly
        } else {
            & (Join-Path $destination $recovery)
            & (Join-Path $destination $recovery) -UserOnly
        }
        foreach ($fixture in $fixtures) {
            Check ($shell.CreateShortcut($fixture.Path).TargetPath -eq (Join-Path $destination 'HytaleAccounts.exe')) "$recovery recovers a journal-owned partially overwritten shortcut"
            Check ((Get-FileHash -LiteralPath $fixture.Backup).Hash -eq $fixture.BackupHash) "$recovery retains the exact original backup"
            Check ((Get-Item -LiteralPath $fixture.Backup).LastWriteTimeUtc -eq $fixture.BackupWritten) "$recovery does not rewrite its recovery backup"
            Interrupt-ShortcutWrite $fixture
            $held = [IO.File]::Open($fixture.Path, 'Open', 'Read', 'Read')
            $failure = $null
            try { & (Join-Path $package 'Uninstall.ps1') @options -UserOnly:$fixture.User } catch { $failure = $_ } finally { $held.Dispose() }
            Check ($failure -and (Test-Path -LiteralPath $fixture.Manifest) -and (Get-FileHash -LiteralPath $fixture.Backup).Hash -eq $fixture.BackupHash) 'blocked restoration retains its journal and usable original backup'
            # A corrupt backup must stop removal before either journal or addon files are discarded.
            [IO.File]::WriteAllBytes($fixture.Backup, [byte[]]@(1,2,3))
            $failure = $null
            try { & (Join-Path $package 'Uninstall.ps1') @options -UserOnly:$fixture.User } catch { $failure = $_ }
            Check ($failure -and $failure.Exception.Message -like '*restoration backup is unreadable*') 'uninstall rejects an unusable restoration backup'
            Check ((Test-Path -LiteralPath $fixture.Manifest) -and (Test-Path -LiteralPath $fixture.Backup) -and (Test-Path -LiteralPath (Join-Path $destination 'HytaleAccounts.exe'))) 'failed shortcut recovery retains the journal, backup and addon for retry'
            [IO.File]::WriteAllBytes($fixture.Backup, $fixture.Original)
            & (Join-Path $package 'Uninstall.ps1') @options -UserOnly:$fixture.User
            Check ((Get-FileHash -LiteralPath $fixture.Path).Hash -eq $fixture.BackupHash) 'uninstall restores a partially overwritten shortcut byte-for-byte'
            Check (!(Test-Path -LiteralPath $fixture.Manifest) -and !(Test-Path -LiteralPath $fixture.Backup)) 'uninstall removes recovery data after successful restoration'
        }
        foreach ($path in $originalLinks.Keys) { Check ((Get-FileHash -LiteralPath $path).Hash -eq $originalLinks[$path]) 'partial-write recovery preserves every original shortcut' }
        Assert-LauncherSnapshot $official $before
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
    $restorationCases = @()
    foreach ($folder in @($programs,$commonPrograms)) {
        foreach ($scenario in @('renamed','deleted','unchanged','replaced')) {
            $path = Join-Path $folder ($scenario + '.lnk')
            $previous = $shell.CreateShortcut($path); $previous.TargetPath = $official
            $previous.Description = "$folder $scenario"; $previous.Save()
            $restorationCases += [pscustomobject]@{ Scenario = $scenario; Path = $path; Current = $path; Hash = (Get-FileHash -LiteralPath $path).Hash; ForeignHash = '' }
        }
    }
    & (Join-Path $package 'Install.ps1') @options
    & (Join-Path $package 'Install.ps1') @options -UserOnly
    foreach ($case in $restorationCases) {
        if ($case.Scenario -in @('renamed','replaced')) {
            $case.Current = Join-Path (Split-Path $case.Path) ('moved-' + (Split-Path $case.Path -Leaf))
            [IO.File]::Move($case.Path, $case.Current)
        }
        if ($case.Scenario -eq 'deleted') { [IO.File]::Delete($case.Path) }
        if ($case.Scenario -eq 'replaced') {
            $foreign = $shell.CreateShortcut($case.Path); $foreign.TargetPath = Join-Path $root 'other.exe'; $foreign.Save()
            $case.ForeignHash = (Get-FileHash -LiteralPath $case.Path).Hash
        }
    }
    $beforeRemoval = (@($programs,$desktop,$commonPrograms,$commonDesktop) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.lnk' -Recurse | ForEach-Object FullName } | Sort-Object) -join "`n"
    & (Join-Path $package 'Uninstall.ps1') @options -UserOnly
    & (Join-Path $package 'Uninstall.ps1') @options
    foreach ($case in $restorationCases) {
        if ($case.Scenario -eq 'deleted') {
            Check (!(Test-Path -LiteralPath $case.Path)) 'uninstall does not recreate a deleted shortcut'
        } else {
            Check ((Get-FileHash -LiteralPath $case.Current).Hash -eq $case.Hash) "uninstall restores $($case.Scenario) shortcut byte-for-byte at its current location"
        }
        if ($case.Scenario -eq 'renamed') { Check (!(Test-Path -LiteralPath $case.Path)) 'renamed shortcut does not reappear under its original name' }
        if ($case.Scenario -eq 'replaced') { Check ((Get-FileHash -LiteralPath $case.Path).Hash -eq $case.ForeignHash) 'unrelated replacement at the original path is preserved' }
    }
    $afterRemoval = (@($programs,$desktop,$commonPrograms,$commonDesktop) | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.lnk' -Recurse | ForEach-Object FullName } | Sort-Object) -join "`n"
    Check ($afterRemoval -ceq $beforeRemoval) 'restoration creates no duplicate launcher entries or deleted shortcuts'
    Check (!$global:HytaleFixtureTasks.Count -and !(Test-Path -LiteralPath $destination)) 'renamed shortcut restoration completes removal with mocked tasks'
    foreach ($case in $restorationCases) {
        foreach ($path in @($case.Path,$case.Current) | Select-Object -Unique) { if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) } }
    }
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
    if ('InstallerWriteProbe' -as [type]) { [InstallerWriteProbe]::AfterWrite = $null; [InstallerWriteProbe]::AfterPublish = $null }
    if (Test-Path -LiteralPath $registry) { Remove-Item -LiteralPath $registry }
    $env:LOCALAPPDATA = $oldLocal
    $env:ProgramFiles = $oldPrograms
    if (Test-Path -LiteralPath $root) { Remove-Fixture $root }
    Remove-Variable HytaleFixtureTasks -Scope Global -ErrorAction SilentlyContinue
}
