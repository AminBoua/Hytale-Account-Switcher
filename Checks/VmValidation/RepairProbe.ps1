param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][ValidateSet('System','Limited')][string]$Role)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Guard.ps1')
$run = Get-Guard $Root
$identityJson = & (Join-Path $PSScriptRoot 'Checks.exe') identity $Root
if ($LASTEXITCODE) { throw 'Identity guard failed.' }
$identity = $identityJson | ConvertFrom-Json
$base = Join-Path $Root ($Role.ToLower() + '-repair')
$result = Join-Path $base 'result.json'
function Check($Value, [string]$Message) { if (!$Value) { throw $Message } }
Check (!(Test-Path -LiteralPath $result)) 'Each repair role is single-use.'
try {
    Check ($identity.Sid -eq $(if ($Role -eq 'System') { 'S-1-5-18' } else { $run.UserSid })) 'Wrong repair principal.'
    Check ($identity.Administrator -eq ($Role -eq 'System')) 'Wrong repair privilege level.'
    if ($Role -eq 'System') {
        & (Join-Path $PSScriptRoot 'Checks.exe') system-stage $Root
        Check ($LASTEXITCODE -eq 0) 'Production staging under the real SYSTEM token failed.'
    }
    if ($Role -eq 'Limited') { Check ($identity.ElevationType -eq 1) 'Limited repair must use the real standard-user token.' }
    # Only this fake repair process redirects its state directory; no launcher process is started.
    $env:LOCALAPPDATA = Join-Path $base 'local'
    $destination = Join-Path $base 'addon'
    $installed = Join-Path $destination 'HytaleAccounts.exe'
    $official = Join-Path $base 'hytale-launcher.exe'
    $state = if ($Role -eq 'Limited') { Join-Path $env:LOCALAPPDATA 'HytaleAccounts\ShortcutIntegration' } else { $destination }
    $links = Join-Path $base 'shortcuts'
    foreach ($path in @($destination,$state,$links)) { $null = New-Item -ItemType Directory -Path $path -Force }
    [IO.File]::WriteAllText($installed,'NONEXECUTABLE FAKE ADDON')
    [IO.File]::WriteAllText($official,'NONEXECUTABLE FAKE LAUNCHER')
    $shortcut = Join-Path $links 'Synthetic Launcher.lnk'
    $victim = Join-Path $base 'victim.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut); $link.TargetPath = $official; $link.Save()
    $outside = $shell.CreateShortcut($victim); $outside.TargetPath = $official; $outside.Save()
    $journal = @{ Roots=@($links); RepairRoots=@($links); UserSid=$run.UserSid; Shortcuts=@(@{Path=$shortcut;Arguments='';Backup=$null}) }
    $journal | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $state 'shortcuts.json') -Encoding UTF8
    & (Join-Path $PSScriptRoot 'Repair-Integration.ps1') -Destination $destination -UserOnly:($Role -eq 'Limited')
    Check ($shell.CreateShortcut($shortcut).TargetPath -eq $installed) 'Production repair did not update the synthetic shortcut.'
    # A malicious journal may not direct SYSTEM or limited repair outside its declared fixture shortcut root.
    $before = (Get-FileHash -LiteralPath $victim).Hash
    $journal.Shortcuts[0].Path = $victim
    $journal | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $state 'shortcuts.json') -Encoding UTF8
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Repair-Integration.ps1') -Destination $destination -UserOnly:($Role -eq 'Limited') } catch { $rejected = $true }
    Check ($rejected -and (Get-FileHash -LiteralPath $victim).Hash -eq $before) 'Unsafe repair journal was accepted or victim changed.'
    Check (!(Test-Path -LiteralPath (Join-Path $state 'shortcuts.new'))) 'Repair failure left a staging file.'
    @{ Success=$true; Identity=$identity; ProductionRepair=$true; UnsafeJournalRejected=$true; VictimPreserved=$true } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $result -Encoding UTF8
} catch {
    @{ Success=$false; Identity=$identity; Error=$_.Exception.Message } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $result -Encoding UTF8
    exit 1
}
