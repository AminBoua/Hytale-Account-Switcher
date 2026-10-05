param(
    [string]$ProgramsDirectory = [Environment]::GetFolderPath('Programs'),
    [string]$DesktopDirectory = [Environment]::GetFolderPath('DesktopDirectory'),
    [string]$RegistryPath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HytaleAccounts',
    [int]$WaitForPid = 0,
    [switch]$Interactive
)
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'InstallerSafety.ps1')
    if ($WaitForPid) { Wait-Process -Id $WaitForPid -Timeout 30 -ErrorAction SilentlyContinue }
    $official = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
    $destination = Join-Path (Split-Path $official) '.hytale-accounts-addon'
    $installed = Join-Path $destination 'HytaleAccounts.exe'
    Assert-RegularPath $destination
    Stop-InstalledLauncher $installed
    $before = if (Test-Path -LiteralPath $official) { Get-LauncherSnapshot $official } else { $null }
    $shell = New-Object -ComObject WScript.Shell
    $manifestPath = Join-Path $destination 'shortcuts.json'
    Assert-RegularPath $manifestPath
    if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Shortcut restoration journal is missing; files preserved.' }
    $journal = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $records = @($journal.Shortcuts)
    $roots = @($journal.Roots)
    if (!$records.Count -or !$roots.Count) { throw 'Shortcut restoration journal is empty; files preserved.' }
    # Validate the entire restoration journal before deleting any files.
    foreach ($record in $records) {
        $path = [IO.Path]::GetFullPath($record.Path)
        $allowed = $false
        foreach ($folder in $roots) {
            if ($path.StartsWith([IO.Path]::GetFullPath($folder).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $allowed = $true }
        }
        if (!$allowed -or [IO.Path]::GetExtension($path) -ne '.lnk' -or $record.Arguments -notin @('','--uninstall')) { throw 'Invalid shortcut restoration journal; files preserved.' }
        if ($record.Backup -and $record.Backup -notmatch '^shortcut-[a-f0-9]{32}\.lnk$') { throw 'Invalid shortcut backup; files preserved.' }
        Assert-RegularPath $path
        if ($record.Backup) {
            $backup = Join-Path $destination $record.Backup
            Assert-RegularPath $backup
            if (!(Test-Path -LiteralPath $backup)) { throw 'Shortcut backup is missing; files preserved.' }
        }
    }
    foreach ($record in $records) {
        if (Test-Path -LiteralPath $record.Path) {
            $link = $shell.CreateShortcut($record.Path)
            if ($link.TargetPath -ne $installed -or $link.Arguments -ne $record.Arguments) { continue }
            if ($record.Backup) { Copy-Item -LiteralPath (Join-Path $destination $record.Backup) -Destination $record.Path -Force }
            else { Remove-Item -LiteralPath $record.Path }
        } elseif ($record.Backup) { Copy-Item -LiteralPath (Join-Path $destination $record.Backup) -Destination $record.Path }
    }
    if (Test-Path -LiteralPath $RegistryPath) {
        if ((Get-ItemProperty -LiteralPath $RegistryPath).InstallLocation -eq $destination) { Remove-Item -LiteralPath $RegistryPath }
    }
    if ($before) { Assert-LauncherSnapshot $official $before }
    # Only exact addon-owned files; accounts, encryption keys, worlds and Hytale remain untouched.
    $files = @('HytaleAccounts.exe','Uninstall.ps1','InstallerSafety.ps1','launcher-baseline.json','shortcuts.json','shortcuts.new') + @($records | Where-Object Backup | ForEach-Object Backup)
    foreach ($name in $files) { Assert-RegularPath (Join-Path $destination $name) }
    foreach ($name in $files) { $path = Join-Path $destination $name; if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    if (!(Get-ChildItem -LiteralPath $destination -Force)) { [IO.Directory]::Delete($destination) }
    Write-Output 'Addon removed. Hytale, accounts and game data preserved.'
} catch {
    if ($Interactive) { Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show($_.Exception.Message, 'Hytale Accounts uninstall') | Out-Null }
    else { throw }
    exit 1
}
