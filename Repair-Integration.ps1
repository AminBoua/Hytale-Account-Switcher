param([string]$Destination = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'InstallerSafety.ps1')
Assert-RegularPath $Destination
$installed = Join-Path $Destination 'HytaleAccounts.exe'
$official = Join-Path (Split-Path $Destination) 'hytale-launcher.exe'
$manifest = Join-Path $Destination 'shortcuts.json'
foreach ($path in @($installed, $official, $manifest)) { Assert-RegularPath $path }
if (!(Test-Path -LiteralPath $installed) -or !(Test-Path -LiteralPath $official)) { return }
$journal = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
$shell = New-Object -ComObject WScript.Shell
foreach ($record in $journal.Shortcuts) {
    $path = [IO.Path]::GetFullPath($record.Path)
    $allowed = @($journal.Roots | Where-Object { $path.StartsWith([IO.Path]::GetFullPath($_).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if (!$allowed -or [IO.Path]::GetExtension($path) -ne '.lnk' -or $record.Arguments -ne '' -or $record.Backup -notmatch '^shortcut-[a-f0-9]{32}\.lnk$') { throw 'Invalid shortcut repair journal; files preserved.' }
    Assert-RegularPath $path
    $backup = Join-Path $Destination $record.Backup
    Assert-RegularPath $backup
    if (!(Test-Path -LiteralPath $backup)) { throw 'Shortcut restoration backup is missing; files preserved.' }
    if (!(Test-Path -LiteralPath $path)) { continue }
    $link = $shell.CreateShortcut($path)
    # Repair only official launcher shortcuts rewritten by its updater; leave user changes alone.
    if ($link.TargetPath -ne $official -or $link.Arguments) { continue }
    Copy-Item -LiteralPath $path -Destination $backup -Force
    $link.TargetPath = $installed
    $link.WorkingDirectory = $Destination
    $link.IconLocation = "$official,0"
    $link.Description = 'Hytale launcher with account switching'
    $link.Save()
}
