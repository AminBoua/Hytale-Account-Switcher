param([string]$Destination = $PSScriptRoot, [switch]$UserOnly)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'InstallerSafety.ps1')
. (Join-Path $PSScriptRoot 'ShortcutIntegration.ps1')
if ($UserOnly -and [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Per-user shortcut repair must run without elevation.' }
$installed = Join-Path $Destination 'HytaleAccounts.exe'
$official = Join-Path (Split-Path $Destination) 'hytale-launcher.exe'
$state = if ($UserOnly) { Join-Path $env:LOCALAPPDATA 'HytaleAccounts\ShortcutIntegration' } else { $Destination }
$manifestPath = Join-Path $state 'shortcuts.json'
if (!(Test-Path -LiteralPath $manifestPath) -or !(Test-Path -LiteralPath $installed) -or !(Test-Path -LiteralPath $official)) { return }
$journalLock = Open-ShortcutJournal $state
try {
    $journal = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $script:records = @($journal.Shortcuts)
    $roots = @($journal.Roots)
    $repairRoots = if ($journal.RepairRoots) { @($journal.RepairRoots) } else { $roots }
    $ownerSid = $journal.UserSid
    $shell = New-Object -ComObject WScript.Shell
    foreach ($record in $records) {
        Assert-ShortcutRecord $record $roots
        # Legacy user records remain for restoration, but their new limited task owns ongoing repair.
        $path = [IO.Path]::GetFullPath($record.Path)
        if (@($repairRoots | Where-Object { $path.StartsWith([IO.Path]::GetFullPath($_).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) }).Count) {
            Update-Shortcut $record.Path Repair $record
        }
    }
} finally { $journalLock.Dispose() }
