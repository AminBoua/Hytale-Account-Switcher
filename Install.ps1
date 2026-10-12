param(
    [string]$ProgramsDirectory = [Environment]::GetFolderPath('Programs'),
    [string]$DesktopDirectory = [Environment]::GetFolderPath('DesktopDirectory'),
    [string]$CommonProgramsDirectory = [Environment]::GetFolderPath('CommonPrograms'),
    [string]$CommonDesktopDirectory = [Environment]::GetFolderPath('CommonDesktopDirectory'),
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
$roots = @($DesktopDirectory, $ProgramsDirectory, $CommonDesktopDirectory, $CommonProgramsDirectory)
foreach ($path in @($helper, $destination) + $roots) { Assert-RegularPath $path }
$before = Get-LauncherSnapshot $official
if (Test-Path -LiteralPath $RegistryPath) {
    if ((Get-ItemProperty -LiteralPath $RegistryPath).InstallLocation -ne $destination) { throw 'Another installation owns the uninstall entry; preserved.' }
}
Stop-InstalledLauncher $installed -IncludeOfficial
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$manifestPath = Join-Path $destination 'shortcuts.json'
Assert-RegularPath $manifestPath
$records = @()
if (Test-Path -LiteralPath $manifestPath) {
    $journal = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $records = @($journal.Shortcuts)
    $roots = @(@($journal.Roots) + $roots | Select-Object -Unique)
}
$shell = New-Object -ComObject WScript.Shell
function Install-Shortcut([string]$Path, [string]$Arguments = '') {
    Assert-RegularPath $Path
    $existing = @($records | Where-Object { $_.Path -eq $Path })
    if ($existing.Count -gt 0) {
        if (Test-Path -LiteralPath $Path) {
            $link = $shell.CreateShortcut($Path)
            if ($link.TargetPath -eq $official -and !$link.Arguments -and $existing[0].Backup) {
                if ($existing[0].Backup -notmatch '^shortcut-[a-f0-9]{32}\.lnk$') { throw 'Invalid shortcut restoration backup; files preserved.' }
                Assert-RegularPath (Join-Path $destination $existing[0].Backup)
                Copy-Item -LiteralPath $Path -Destination (Join-Path $destination $existing[0].Backup) -Force
            }
            elseif ($link.TargetPath -ne $installed -or $link.Arguments -ne $Arguments) { throw "Shortcut changed since installation; preserved: $Path" }
        }
    } else {
        $backup = ''
        if (Test-Path -LiteralPath $Path) {
            $previous = $shell.CreateShortcut($Path)
            if ($previous.TargetPath -ne $installed -or $previous.Arguments -notin @('','--uninstall')) {
                $backup = 'shortcut-' + [Guid]::NewGuid().ToString('N') + '.lnk'
                Copy-Item -LiteralPath $Path -Destination (Join-Path $destination $backup)
            }
        }
        $script:records += [pscustomobject]@{ Path = $Path; Backup = $backup; Arguments = $Arguments }
        # Record restoration before changing a shortcut, including on interrupted installs.
        $temporary = Join-Path $destination 'shortcuts.new'
        Assert-RegularPath $temporary
        [IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject @{ Roots = $roots; Shortcuts = @($script:records) } -Depth 4))
        if (Test-Path -LiteralPath $manifestPath) { [IO.File]::Replace($temporary, $manifestPath, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $manifestPath) }
    }
    New-Item -ItemType Directory -Path (Split-Path $Path) -Force | Out-Null
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $installed
    $link.Arguments = $Arguments
    $link.WorkingDirectory = $destination
    $link.IconLocation = "$official,0"
    $link.Description = if ($Arguments) { 'Remove the Hytale account addon; keep Hytale and your data' } else { 'Hytale launcher with account switching' }
    $link.Save()
}
foreach ($name in @('HytaleAccounts.exe','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','launcher-baseline.json')) { Assert-RegularPath (Join-Path $destination $name) }
Copy-Item -LiteralPath $helper -Destination $installed -Force
foreach ($name in @('Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $destination $name) -Force }
$before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'launcher-baseline.json') -Encoding UTF8
# Keep every existing launcher's name and location, including official system-wide shortcuts.
foreach ($folder in $roots) {
    if (Test-Path -LiteralPath $folder) {
        foreach ($file in Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -Recurse) {
            Assert-RegularPath $file.FullName
            $link = $shell.CreateShortcut($file.FullName)
            if ($link.TargetPath -eq $official -and !$link.Arguments) { Install-Shortcut $file.FullName }
        }
    }
}
if (!$records.Count) { throw 'No Hytale launcher shortcut was found. Reinstall the official launcher first.' }
New-Item -Path $RegistryPath -Force | Out-Null
$properties = @{
    DisplayName = 'Hytale Accounts addon'; DisplayVersion = '0.1.1'; InstallLocation = $destination
    UninstallString = '"' + $installed + '" --uninstall'; DisplayIcon = "$official,0"
}
foreach ($name in $properties.Keys) { New-ItemProperty -LiteralPath $RegistryPath -Name $name -Value $properties[$name] -PropertyType String -Force | Out-Null }
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action = New-ScheduledTaskAction -Execute $powershell -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $destination 'Repair-Integration.ps1') + '"')
$repairTask = Get-ScheduledTask -TaskName 'HytaleAccounts-Repair' -ErrorAction SilentlyContinue
if ($repairTask -and (@($repairTask.Actions).Count -ne 1 -or $repairTask.Actions[0].Execute -cne $powershell -or $repairTask.Actions[0].Arguments -cne $action.Arguments)) { throw 'Another task owns the shortcut repair entry; preserved.' }
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
$settings = New-ScheduledTaskSettingsSet -Hidden -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName 'HytaleAccounts-Repair' -Action $action -Trigger $trigger -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
Assert-LauncherSnapshot $official $before
Write-Output 'Installed. Official launcher bytes and permissions preserved.'
} catch {
    $errorPath = Join-Path $PSScriptRoot 'install-error.txt'
    Assert-RegularPath $errorPath
    [IO.File]::WriteAllText($errorPath, $_.ToString())
    throw
}
