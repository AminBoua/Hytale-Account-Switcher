param(
    [Parameter(Mandatory)][ValidateSet('Before','Installed','Removed')][string]$Phase,
    [Parameter(Mandatory)][Guid]$GuestUuid,
    [Parameter(Mandatory)][Guid]$BuildHostUuid,
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ArchiveHash,
    [Parameter(Mandatory)][string]$Output,
    [string]$Baseline,
    [switch]$DesignatedDisposableVm
)

$ErrorActionPreference = 'Stop'
$system = Get-CimInstance Win32_ComputerSystem
$actualUuid = [Guid](Get-CimInstance Win32_ComputerSystemProduct).UUID
if (!$DesignatedDisposableVm -or $actualUuid -ne $GuestUuid -or $GuestUuid -eq [Guid]::Empty -or
    $BuildHostUuid -eq [Guid]::Empty -or $actualUuid -eq $BuildHostUuid -or $system.PartOfDomain -or
    ($system.Manufacturer + ' ' + $system.Model) -notmatch 'Virtual Machine|VMware|VirtualBox|KVM|QEMU|Xen|Parallels') {
    throw 'Use a designated disposable Windows VM with its own UUID.'
}
if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $ArchiveHash) { throw 'Package hash differs.' }
if (Test-Path -LiteralPath $Output) { throw 'Use a new evidence filename.' }

$official = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
$addon = Join-Path (Split-Path $official) '.hytale-accounts-addon\HytaleAccounts.exe'
$registry = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\HytaleAccounts'
$entry = Get-ItemProperty -LiteralPath $registry -ErrorAction SilentlyContinue
$owner = [Security.Principal.WindowsIdentity]::GetCurrent()
$sid = $owner.User.Value
$shell = New-Object -ComObject WScript.Shell
$shortcuts = @(
    foreach ($folder in @('Programs','DesktopDirectory','CommonPrograms','CommonDesktopDirectory')) {
        $root = [Environment]::GetFolderPath($folder)
        if (!$root -or !(Test-Path -LiteralPath $root)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $root -File -Filter '*.lnk' -Recurse) {
            $link = $shell.CreateShortcut($file.FullName)
            if ($link.Arguments -eq '' -and $link.TargetPath -in @($official,$addon)) {
                [ordered]@{ Path = $file.FullName; Target = $link.TargetPath; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
            }
        }
    }
)
$tasks = @(Get-ScheduledTask -TaskName 'HytaleAccounts-Repair','HytaleAccounts-Repair-S-1-*' -ErrorAction SilentlyContinue | ForEach-Object {
    @{ Name = $_.TaskName; Principal = $_.Principal.UserId; RunLevel = $_.Principal.RunLevel.ToString(); Action = @($_.Actions | ForEach-Object { $_.Execute + ' ' + $_.Arguments }) }
})
$result = [ordered]@{
    Kind = 'disposable-vm-install-observation'; Phase = $Phase; ArchiveSha256 = $ArchiveHash.ToUpperInvariant()
    GuestUuid = $actualUuid.ToString(); Utc = [DateTime]::UtcNow.ToString('o'); Sid = $sid
    Elevated = [Security.Principal.WindowsPrincipal]::new($owner).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    OfficialSha256 = (Get-FileHash -LiteralPath $official -Algorithm SHA256).Hash
    OfficialAcl = (Get-Acl -LiteralPath $official).Sddl
    AddonFile = (Test-Path -LiteralPath $addon); AppsEntry = $entry.UninstallString
    Shortcuts = $shortcuts; Tasks = $tasks
}
if (!$shortcuts.Count) { throw 'No eligible launcher shortcut found.' }
if ($Phase -ne 'Before') {
    if (!$Baseline) { throw 'Supply the evidence from the Before phase.' }
    $before = Get-Content -LiteralPath $Baseline -Raw | ConvertFrom-Json
    if ($before.Kind -ne $result.Kind -or $before.Phase -ne 'Before' -or $before.ArchiveSha256 -ne $result.ArchiveSha256 -or
        $before.GuestUuid -ne $result.GuestUuid -or $before.Sid -ne $sid -or $before.OfficialSha256 -ne $result.OfficialSha256 -or
        $before.OfficialAcl -cne $result.OfficialAcl) { throw 'Baseline identity, package, or official launcher differs.' }
    if (@(Compare-Object @($before.Shortcuts | ForEach-Object Path | Sort-Object -Unique) @($shortcuts | ForEach-Object { $_.Path } | Sort-Object -Unique)).Count) {
        throw 'The existing launcher entries changed.'
    }
    if ($Phase -eq 'Installed' -and (!$result.AddonFile -or $result.AppsEntry -ne ('"' + $addon + '" --uninstall') -or
        @($shortcuts | Where-Object { $_.Target -ne $addon }).Count -or $tasks.Count -ne 2)) { throw 'Installed integration differs.' }
    if ($Phase -eq 'Removed') {
        if ($result.AddonFile -or $result.AppsEntry -or $tasks.Count) { throw 'Installed addon state remains.' }
        foreach ($item in $shortcuts) {
            $prior = @($before.Shortcuts | Where-Object Path -EQ $item.Path)
            if ($prior.Count -ne 1 -or $item.Target -ne $official -or $prior[0].Sha256 -ne $item.Sha256) {
                throw 'An unedited launcher shortcut differs from its original bytes.'
            }
        }
    }
} elseif ($result.AddonFile -or $result.AppsEntry -or $tasks.Count -or @($shortcuts | Where-Object { $_.Target -ne $official }).Count) {
    throw 'Use the Before phase on a clean official installation.'
}
$result | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $Output -Encoding UTF8
