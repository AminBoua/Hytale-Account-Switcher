# Dot-source after InstallerSafety.ps1. The journal lock also pins its directory for COM staging.
if (!('ShortcutFile' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'ShortcutFile.cs') }
function Open-ShortcutFile([string]$Path, [switch]$Create) { [ShortcutFile]::new($Path, $Create.IsPresent) }
function Read-ShortcutBytes([string]$Path) {
    $file = Open-ShortcutFile $Path
    try { if (!$file.Exists) { throw "Shortcut backup is missing; files preserved: $Path" }; return ,$file.Read() } finally { $file.Dispose() }
}
function Write-ShortcutBytes([string]$Path, [byte[]]$Bytes) {
    $file = Open-ShortcutFile $Path -Create:(!(Test-Path -LiteralPath $Path))
    try { $file.Write($Bytes) } finally { $file.Dispose() }
}
function Get-ShortcutHash([byte[]]$Bytes) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($hash.ComputeHash($Bytes)) } finally { $hash.Dispose() }
}
function Open-ShortcutJournal([string]$State) {
    Assert-RegularPath $State
    New-Item -ItemType Directory -Path $State -Force | Out-Null
    $lock = Open-ShortcutFile (Join-Path $State 'shortcuts.lock') -Create:(!(Test-Path -LiteralPath (Join-Path $State 'shortcuts.lock')))
    try {
        if ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            $acl = Get-Acl -LiteralPath $State
            $trusted = @('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
            if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw 'Privileged shortcut state has an untrusted owner.' }
            $raw = [Security.AccessControl.RawSecurityDescriptor]::new($acl.Sddl)
            if ($null -eq $raw.DiscretionaryAcl) { throw 'Privileged shortcut state has no access restrictions.' }
            foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
                if ($rule.AccessControlType -eq 'Allow' -and !($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $rule.IdentityReference.Value -notin $trusted -and ([long]$rule.FileSystemRights -band 0x500D0156)) { throw 'Privileged shortcut state is writable by a non-administrator.' }
            }
        }
        return $lock
    } catch { $lock.Dispose(); throw }
}
function Save-ShortcutJournal {
    $temporary = Join-Path $state 'shortcuts.new'
    Write-ShortcutBytes $temporary ([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @{ Roots = $roots; RepairRoots = $repairRoots; UserSid = $ownerSid; Shortcuts = @($script:records) } -Depth 4)))
    # State is pinned for the entire operation; privileged state is administrator-owned and not user-writable.
    if (Test-Path -LiteralPath $manifestPath) { Assert-RegularPath $manifestPath; [IO.File]::Replace($temporary, $manifestPath, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $manifestPath) }
}
function Assert-ShortcutRecord($Record, $Roots) {
    $path = [IO.Path]::GetFullPath($Record.Path)
    $allowed = @($Roots | Where-Object { $path.StartsWith([IO.Path]::GetFullPath($_).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if (!$allowed -or [IO.Path]::GetExtension($path) -ne '.lnk' -or $Record.Arguments -notin @('','--uninstall') -or ($Record.Backup -and $Record.Backup -notmatch '^shortcut-[a-f0-9]{32}\.lnk$')) { throw 'Invalid shortcut restoration journal; files preserved.' }
    if ($Record.Backup) { $null = Read-ShortcutBytes (Join-Path $state $Record.Backup) }
}
function Update-Shortcut([string]$Path, [ValidateSet('Install','Repair','Remove')] [string]$Operation, $Record) {
    # Missing directories mean the user removed the shortcut location; do not recreate them.
    if (!(Test-Path -LiteralPath (Split-Path $Path))) { return }
    $file = Open-ShortcutFile $Path
    $scratch = Join-Path $state ([Guid]::NewGuid().ToString('N') + '.lnk')
    try {
        if (!$file.Exists) { return }
        $bytes = $file.Read()
        if ($Operation -eq 'Remove' -and !$Record) {
            # Identify renamed links only by unchanged bytes held through restoration.
            $hash = Get-ShortcutHash $bytes
            $matches = @($records | Where-Object { $_.AppliedHash -and $_.AppliedHash -eq $hash })
            if ($matches.Count -ne 1) { return }
            $Record = $matches[0]
        }
        Write-ShortcutBytes $scratch $bytes
        $link = $shell.CreateShortcut($scratch)
        if ($Operation -eq 'Remove') {
            if ($link.TargetPath -ne $installed -or $link.Arguments -ne $Record.Arguments) { return }
            $edited = $Record.AppliedHash -and $Record.AppliedHash -ne (Get-ShortcutHash $bytes)
            if ($Record.Backup) {
                $originalBytes = Read-ShortcutBytes (Join-Path $state $Record.Backup)
                if ($edited) {
                    # Restore our launch target while retaining user-edited metadata from the held shortcut.
                    $original = $shell.CreateShortcut((Join-Path $state $Record.Backup))
                    $link.TargetPath = $original.TargetPath; $link.Arguments = $original.Arguments
                    if ($link.WorkingDirectory -eq $destination) { $link.WorkingDirectory = $original.WorkingDirectory }
                    if ($link.IconLocation -eq "$official,0") { $link.IconLocation = $original.IconLocation }
                    if ($link.Description -eq 'Hytale launcher with account switching') { $link.Description = $original.Description }
                    $link.Save()
                    $file.Write((Read-ShortcutBytes $scratch))
                } else { $file.Write($originalBytes) }
            } elseif (!$edited) { $file.Delete() }
            return
        }
        if ($link.TargetPath -ne $official -or $link.Arguments) {
            # Adopt a legacy machine journal into the user's unelevated journal without losing its original backup.
            if ($Operation -eq 'Install' -and !$Record -and $link.TargetPath -eq $installed -and !$link.Arguments -and $UserOnly -and (Test-Path -LiteralPath (Join-Path $destination 'shortcuts.json'))) {
                $legacy = Get-Content -LiteralPath (Join-Path $destination 'shortcuts.json') -Raw | ConvertFrom-Json
                $old = @($legacy.Shortcuts | Where-Object Path -eq $Path)
                if ($old.Count -eq 1 -and $old[0].Backup -match '^shortcut-[a-f0-9]{32}\.lnk$') {
                    $backup = 'shortcut-' + [Guid]::NewGuid().ToString('N') + '.lnk'
                    Write-ShortcutBytes (Join-Path $state $backup) ([IO.File]::ReadAllBytes((Join-Path $destination $old[0].Backup)))
                    $script:records += [pscustomobject]@{ Path = $Path; Backup = $backup; Arguments = ''; AppliedHash = (Get-ShortcutHash $bytes) }
                    Save-ShortcutJournal
                }
            }
            return
        }
        if (!$Record) {
            $Record = [pscustomobject]@{ Path = $Path; Backup = ('shortcut-' + [Guid]::NewGuid().ToString('N') + '.lnk'); Arguments = '' }
            $script:records += $Record
        }
        if (!$Record.Backup) { $Record.Backup = 'shortcut-' + [Guid]::NewGuid().ToString('N') + '.lnk' }
        Write-ShortcutBytes (Join-Path $state $Record.Backup) $bytes
        $link.TargetPath = $installed; $link.Arguments = ''; $link.WorkingDirectory = $destination
        $link.IconLocation = "$official,0"; $link.Description = 'Hytale launcher with account switching'
        $link.Save()
        $replacement = Read-ShortcutBytes $scratch
        $Record | Add-Member NoteProperty AppliedHash (Get-ShortcutHash $replacement) -Force
        Save-ShortcutJournal # Backup and intended bytes are journaled before the handle-bound write.
        $file.Write($replacement)
    } finally {
        $file.Dispose()
        if (Test-Path -LiteralPath $scratch) { $temp = Open-ShortcutFile $scratch; try { $temp.Delete() } finally { $temp.Dispose() } }
    }
}
function Remove-ShortcutStateFiles {
    foreach ($name in @('shortcuts.json','shortcuts.new') + @($records | Where-Object Backup | ForEach-Object Backup)) {
        $file = Open-ShortcutFile (Join-Path $state $name)
        try { if ($file.Exists) { $file.Delete() } } finally { $file.Dispose() }
    }
}
