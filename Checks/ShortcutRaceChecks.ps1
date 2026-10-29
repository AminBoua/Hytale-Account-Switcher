$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
# Instrument only the fixture copy at the final handle-relative open; production has no race hooks.
$source = Get-Content -LiteralPath (Join-Path $repository 'ShortcutFile.cs') -Raw
$source = $source.Replace('var file = OpenRelative(parent, parts[parts.Length - 1], false, create);', 'if (ShortcutRaceProbe.BeforeLeaf != null) ShortcutRaceProbe.BeforeLeaf(path); var file = OpenRelative(parent, parts[parts.Length - 1], false, create);')
$source += @'
public static class ShortcutRaceProbe
{
    public static System.Action<string> BeforeLeaf;
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=System.Runtime.InteropServices.CharSet.Unicode, SetLastError=true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path, uint access, uint share, System.IntPtr security, uint mode, uint flags, System.IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError=true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, byte[] input, int length, System.IntPtr output, int outputLength, out int returned, System.IntPtr overlapped);
    public static void Junction(string path, string target)
    {
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = System.Text.Encoding.Unicode.GetBytes(target);
        var data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        System.BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
        System.BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
        System.BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 10);
        System.BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(data, 12);
        System.BitConverter.GetBytes((ushort)print.Length).CopyTo(data, 14);
        substitute.CopyTo(data, 16); print.CopyTo(data, 18 + substitute.Length);
        using (var handle = Open(path, 0x40000000, 7, System.IntPtr.Zero, 3, 0x02200000, System.IntPtr.Zero))
        {
            if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            int returned;
            if (!DeviceIoControl(handle, 0x900A4, data, data.Length, System.IntPtr.Zero, 0, out returned, System.IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }
}
'@
Add-Type -TypeDefinition $source
. (Join-Path $repository 'InstallerSafety.ps1')
. (Join-Path $repository 'ShortcutIntegration.ps1')
function Check($Condition, [string]$Message) { if (!$Condition) { throw "FAIL $Message" }; Write-Host "PASS $Message" }
function Reject([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Check $rejected $Message
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('shortcut-races-' + [Guid]::NewGuid().ToString('N'))
function Cleanup([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $root -and !$full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup escaped fixture.' }
    $item = Get-Item -LiteralPath $full -Force
    if ($item.PSIsContainer) {
        if (!($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { foreach ($child in Get-ChildItem -LiteralPath $full -Force) { Cleanup $child.FullName } }
        [IO.Directory]::Delete($full)
    } else { [IO.File]::Delete($full) }
}
try {
    $state = Join-Path $root 'state'; $folder = Join-Path $root 'shortcuts'; $outside = Join-Path $root 'victim'
    foreach ($directory in @($state,$folder,$outside)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    $destination = $state; $official = Join-Path $root 'official.exe'; $installed = Join-Path $state 'addon.exe'
    $roots = @($folder); $repairRoots = $roots; $ownerSid = ''; $manifestPath = Join-Path $state 'shortcuts.json'; $script:records = @(); $UserOnly = $false
    $shell = New-Object -ComObject WScript.Shell
    $path = Join-Path $folder 'Launcher.lnk'; $fixtureShortcut = $path; $victim = Join-Path $outside 'Launcher.lnk'
    [IO.File]::WriteAllText($victim, 'sentinel: never overwritten')
    $sentinel = (Get-FileHash -LiteralPath $victim).Hash
    $journalLock = Open-ShortcutJournal $state
    function OfficialLink { $link = $shell.CreateShortcut($path); $link.TargetPath = $official; $link.Arguments = ''; $link.Description = 'Official fixture'; $link.Save() }
    function Open-ShortcutFile([string]$Path, [switch]$Create) {
        if ($script:beforeOpen) { & $script:beforeOpen $Path $Create }
        $lease = [ShortcutFile]::new($Path, $Create.IsPresent)
        try { if ($script:afterOpen) { & $script:afterOpen $Path $Create $lease }; return $lease } catch { $lease.Dispose(); throw }
    }
    foreach ($operation in @('Install','Repair','Remove')) {
        OfficialLink
        if ($operation -eq 'Remove') { Update-Shortcut $path Install ($records | Select-Object -First 1) }
        $record = $records | Select-Object -First 1
        $script:attacked = $false
        $script:afterOpen = {
            param($opened,$create,$lease)
            if ($opened -ne $fixtureShortcut -or $script:attacked) { return }
            $script:attacked = $true
            Reject { [IO.Directory]::Move($folder, $folder + '-moved') } "$operation blocks directory replacement after validation"
            Reject { [ShortcutRaceProbe]::Junction($folder, $outside) } "$operation refuses in-place junction conversion with a held child"
            Reject { [IO.File]::Move($path, $path + '.moved') } "$operation blocks leaf replacement after inspection"
            Reject { [IO.File]::WriteAllText($path, 'racing user edit') } "$operation holds the inspected leaf through the write"
        }
        Update-Shortcut $path $operation $record
        Check $script:attacked "$operation deterministic race boundary exercised"
        Check ((Get-FileHash -LiteralPath $victim).Hash -eq $sentinel) "$operation leaves outside victim untouched"
        $script:afterOpen = $null
        if ($operation -ne 'Remove') { Check ($shell.CreateShortcut($path).TargetPath -eq $installed) "$operation completes through held file handle" }
        else { Check ($shell.CreateShortcut($path).TargetPath -eq $official) 'removal restores the original bytes' }
    }
    foreach ($operation in @('Install','Repair','Remove')) {
        OfficialLink
        if ($operation -eq 'Remove') { Update-Shortcut $path Install ($records | Select-Object -First 1) }
        $script:beforeOpen = {
            param($opened,$create)
            if ($opened -ne $fixtureShortcut) { return }
            $script:beforeOpen = $null
            [IO.Directory]::Move($folder, $folder + '-moved')
            New-Item -ItemType Junction -Path $folder -Target $outside | Out-Null
        }
        Reject { Update-Shortcut $path $operation ($records | Select-Object -First 1) } "$operation rejects junction planted between discovery and acquisition"
        Check ((Get-FileHash -LiteralPath $victim).Hash -eq $sentinel) "$operation cannot write through pre-acquisition junction"
        [IO.Directory]::Delete($folder); [IO.Directory]::Move($folder + '-moved', $folder)
    }
    OfficialLink; Update-Shortcut $path Install ($records | Select-Object -First 1)
    $record = $records | Select-Object -First 1
    [IO.File]::Delete($path)
    Update-Shortcut $path Remove $record
    Check (!(Test-Path -LiteralPath $path)) 'removal leaves a deleted shortcut missing'
    $script:beforeOpen = {
        param($opened,$create)
        if ($opened -eq $fixtureShortcut -and $create) { $script:beforeOpen = $null; [IO.File]::WriteAllText($fixtureShortcut, 'new user file') }
    }
    Reject { Write-ShortcutBytes $path ([Text.Encoding]::UTF8.GetBytes('fixture')) } 'shortcut creation refuses a concurrently created leaf'
    Check ([IO.File]::ReadAllText($path) -eq 'new user file') 'concurrent user file preserved'
    [IO.File]::Delete($path)
    # Convert the held, empty parent into a junction immediately before NtCreateFile.
    $script:reparseInjected = $false
    [ShortcutRaceProbe]::BeforeLeaf = {
        param($opened)
        if ($opened -ne $fixtureShortcut) { return }
        [ShortcutRaceProbe]::BeforeLeaf = $null
        [ShortcutRaceProbe]::Junction($folder, $outside)
        $script:reparseInjected = $true
    }
    $lease = $null
    try { $lease = [ShortcutFile]::new($path, $true); $lease.Write([Text.Encoding]::UTF8.GetBytes('bound to original directory')) } catch { Write-Host ('Protected open refused in-place reparse: ' + $_.Exception.Message) }
    finally { if ($lease) { $lease.Dispose() } }
    Check $script:reparseInjected 'in-place junction attack reached the already-held directory'
    Check ((Get-FileHash -LiteralPath $victim).Hash -eq $sentinel) 'handle-relative creation cannot follow an in-place parent junction'
    [IO.Directory]::Delete($folder)
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    New-Item -ItemType HardLink -Path $path -Target $victim | Out-Null
    Reject { Update-Shortcut $path Install $null } 'hard-linked shortcut rejected before any write'
    Check ((Get-FileHash -LiteralPath $victim).Hash -eq $sentinel) 'hard-link victim untouched'
    [IO.File]::Delete($path)
    OfficialLink; Update-Shortcut $path Install $record
    $legacyRecord = [pscustomobject]@{ Path = $path; Backup = ''; Arguments = ''; AppliedHash = $record.AppliedHash }
    Update-Shortcut $path Remove $legacyRecord
    Check (!(Test-Path -LiteralPath $path)) 'legacy addon-created shortcut is deleted through its held handle'
    OfficialLink; Update-Shortcut $path Install $record
    $link = $shell.CreateShortcut($path); $link.Description = 'User changed description only'; $link.Save()
    $changed = (Get-FileHash -LiteralPath $path).Hash
    Update-Shortcut $path Repair $record
    Check ((Get-FileHash -LiteralPath $path).Hash -eq $changed) 'repair preserves metadata-only user changes'
    Update-Shortcut $path Remove $record
    $restored = $shell.CreateShortcut($path)
    Check ($restored.TargetPath -eq $official -and $restored.Description -eq 'User changed description only') 'removal preserves edited metadata while restoring a working official target'
    OfficialLink; Update-Shortcut $path Install $record
    $applied = [IO.File]::ReadAllBytes($path)
    $copy = Join-Path $folder 'Replacement.lnk'
    [IO.File]::Copy($path, $copy)
    Update-Shortcut $copy Remove $null
    Check ((Get-ShortcutHash (Read-ShortcutBytes $copy)) -eq $record.AppliedHash) 'unique unchanged bytes cannot override a different recorded file identity'
    $identity = $record.FileId; $record.FileId = ''
    $link = $shell.CreateShortcut($copy); $link.Description = 'Unsupported legacy edit'; $link.Save()
    $legacyHash = (Get-FileHash -LiteralPath $copy).Hash
    Update-Shortcut $copy Remove $null
    Check ((Get-FileHash -LiteralPath $copy).Hash -eq $legacyHash) 'renamed legacy metadata edits remain untouched without a recorded file identity'
    [IO.File]::WriteAllBytes($copy, $applied)
    Update-Shortcut $copy Remove $null
    Check ($shell.CreateShortcut($copy).TargetPath -eq $official) 'unique unchanged legacy shortcut still restores without a recorded file identity'
    $record.FileId = $identity
    [IO.File]::Delete($copy)
    $twin = Join-Path $folder 'Twin.lnk'
    $link = $shell.CreateShortcut($twin); $link.TargetPath = $official; $link.Description = 'Different original metadata'; $link.Save()
    Update-Shortcut $twin Install $null
    $twinRecord = $records | Where-Object Path -eq $twin
    # Force identical applied bytes regardless of the host's shell link-tracking metadata.
    [IO.File]::WriteAllBytes($twin, $applied)
    $twinRecord.AppliedHash = $record.AppliedHash
    Save-ShortcutJournal
    Check ($twinRecord.FileId -ne $record.FileId) 'identical addon shortcuts retain distinct native file identities'
    $renamed = Join-Path $folder 'Renamed.lnk'
    [IO.File]::Move($path, $renamed)
    $script:beforeOpen = {
        param($opened,$create)
        if ($opened -ne $renamed) { return }
        $script:beforeOpen = $null
        $link = $shell.CreateShortcut($renamed); $link.Description = 'Changed after discovery'; $link.Save()
    }
    Update-Shortcut $renamed Remove $null
    $restored = $shell.CreateShortcut($renamed)
    Check ($restored.TargetPath -eq $official -and !$restored.Arguments -and $restored.Description -eq 'Changed after discovery') 'renamed metadata edit before acquisition restores the validated target and retains the edit'
    foreach ($foreign in @('target','arguments')) {
        [IO.File]::WriteAllBytes($renamed, $applied)
        $link = $shell.CreateShortcut($renamed)
        if ($foreign -eq 'target') { $link.TargetPath = Join-Path $root 'foreign.exe' } else { $link.Arguments = '--foreign' }
        $link.Save()
        $foreignHash = (Get-FileHash -LiteralPath $renamed).Hash
        Update-Shortcut $renamed Remove $null
        Check ((Get-FileHash -LiteralPath $renamed).Hash -eq $foreignHash) "renamed identity match with foreign $foreign remains untouched"
    }
    [IO.File]::WriteAllBytes($renamed, $applied)
    $twinIdentity = $twinRecord.FileId; $twinRecord.FileId = $record.FileId
    Update-Shortcut $renamed Remove $null
    Check ((Get-ShortcutHash (Read-ShortcutBytes $renamed)) -eq $record.AppliedHash) 'ambiguous recorded file identities remain untouched'
    $twinRecord.FileId = $twinIdentity
    [IO.File]::WriteAllBytes($renamed, $applied)
    $identity = $record.FileId; $record.FileId = ''
    Update-Shortcut $renamed Remove $null
    Check ((Get-ShortcutHash (Read-ShortcutBytes $renamed)) -eq $record.AppliedHash) 'ambiguous legacy matches remain untouched without a recorded file identity'
    $record.FileId = $identity
    $script:afterOpen = {
        param($opened,$create,$lease)
        if ($opened -ne $renamed) { return }
        Reject { [IO.File]::WriteAllText($renamed, 'racing user edit') } 'renamed shortcut identity, inspection and restoration hold the same file'
        Reject { [IO.Directory]::Move($folder, $folder + '-moved') } 'renamed shortcut restoration pins its directory'
    }
    Update-Shortcut $renamed Remove $null
    $script:afterOpen = $null
    Check ((Get-ShortcutHash (Read-ShortcutBytes $renamed)) -eq (Get-ShortcutHash (Read-ShortcutBytes (Join-Path $state $record.Backup)))) 'unchanged renamed shortcut restores its exact backup'
    Check ((Get-ShortcutHash (Read-ShortcutBytes $twin)) -eq $record.AppliedHash) 'renamed restoration leaves its byte-identical sibling untouched'
    Update-Shortcut $twin Remove $twinRecord
    Check ((Get-ShortcutHash (Read-ShortcutBytes $twin)) -eq (Get-ShortcutHash (Read-ShortcutBytes (Join-Path $state $twinRecord.Backup)))) 'identical sibling restores its own original metadata'
    [IO.File]::Delete($twin)
    Check (!(Test-Path -LiteralPath $path) -and @(Get-ChildItem -LiteralPath $folder -Filter '*.lnk').Count -eq 1) 'renamed restoration creates no duplicate launcher entry'
    [IO.File]::WriteAllBytes($path, [byte[]]@(1,2,3))
    $brokenHash = (Get-FileHash -LiteralPath $path).Hash
    Update-Shortcut $path Install $null
    Check ((Get-FileHash -LiteralPath $path).Hash -eq $brokenHash) 'unowned broken shortcut is preserved'
    foreach ($operation in @('Repair','Remove')) {
        Reject { Update-Shortcut $path $operation $legacyRecord } "$operation refuses a broken owned shortcut without a backup"
        Check ((Get-FileHash -LiteralPath $path).Hash -eq $brokenHash) "$operation leaves unrecoverable bytes available for recovery"
    }
    [IO.File]::Delete($path)
    $empty = $shell.CreateShortcut($path); $empty.Description = 'User chose a valid targetless link'; $empty.Save()
    Check ([ShortcutFile]::IsReadableShortcut($path) -and !$shell.CreateShortcut($path).TargetPath) 'native loader accepts a valid shortcut with an empty target'
    $emptyHash = (Get-FileHash -LiteralPath $path).Hash
    Update-Shortcut $path Repair $record
    Update-Shortcut $path Remove $record
    Check ((Get-FileHash -LiteralPath $path).Hash -eq $emptyHash) 'repair and removal preserve valid targetless user replacements'
    $backupPath = Join-Path $state $record.Backup
    $backupBytes = [IO.File]::ReadAllBytes($backupPath)
    [IO.File]::WriteAllBytes($backupPath, [byte[]]@(1,2,3))
    [IO.File]::Delete($path)
    Reject { Update-Shortcut $path Remove $record } 'missing shortcut restoration rejects a broken backup before creating a leaf'
    Check (!(Test-Path -LiteralPath $path)) 'invalid backup cannot be installed as a restored shortcut'
    [IO.File]::WriteAllBytes($backupPath, $backupBytes)
    foreach ($operation in @('Remove','Repair','Install')) {
        $path = Join-Path $folder ("Recovery-$operation.lnk")
        OfficialLink
        $originalBytes = [IO.File]::ReadAllBytes($path)
        Update-Shortcut $path Install $null
        $record = $records | Where-Object Path -eq $path
        $appliedBytes = [IO.File]::ReadAllBytes($path)
        $backupPath = Join-Path $state $record.Backup
        $backupHash = (Get-FileHash -LiteralPath $backupPath).Hash
        $backupWritten = (Get-Item -LiteralPath $backupPath).LastWriteTimeUtc
        $journalHash = (Get-FileHash -LiteralPath $manifestPath).Hash
        $recordJson = $record | ConvertTo-Json -Compress
        # Keep the original alive so the replacement cannot reuse its native file identity.
        $moved = $path + '.original'
        [IO.File]::Move($path, $moved)
        [IO.File]::WriteAllBytes($path, [byte[]]@(1,2,3))
        $lease = Open-ShortcutFile $path
        try { Check ($lease.Identity -ne $record.FileId) "$operation unreadable replacement has a different file identity" } finally { $lease.Dispose() }
        Check (![ShortcutFile]::IsReadableShortcut($path)) "$operation replacement is unreadable by the native shortcut loader"
        $replacementHash = (Get-FileHash -LiteralPath $path).Hash
        $failure = $null
        try { Update-Shortcut $path $operation $record } catch { $failure = $_ }
        Check ((Get-FileHash -LiteralPath $path).Hash -eq $replacementHash) "$operation preserves different-identity unreadable replacement bytes"
        Check ($failure -and $failure.Exception.Message -like '*ownership*') "$operation refuses recovery when replacement ownership cannot be validated"
        Check ((Get-FileHash -LiteralPath $manifestPath).Hash -eq $journalHash -and ($record | ConvertTo-Json -Compress) -ceq $recordJson) "$operation refusal retains the journal and in-memory recovery record"
        Check ((Get-FileHash -LiteralPath $backupPath).Hash -eq $backupHash -and (Get-Item -LiteralPath $backupPath).LastWriteTimeUtc -eq $backupWritten) "$operation refusal retains the original backup without rewriting it"
        [IO.File]::Delete($path)
        [IO.File]::Move($moved, $path)
        Check ($appliedBytes.Length -gt $originalBytes.Length) "$operation partial-write fixture has an original tail"
        # Tear a real in-place shortcut write at a native structure boundary.
        for ($prefixLength = 76; $prefixLength -lt $originalBytes.Length; $prefixLength += 8) {
            [IO.File]::WriteAllBytes($path, $originalBytes)
            $stream = [IO.File]::Open($path, 'Open', 'Write', 'None')
            try { $stream.Write($appliedBytes, 0, $prefixLength); $stream.Flush($true) } finally { $stream.Dispose() }
            if (![ShortcutFile]::IsReadableShortcut($path)) { break }
        }
        $lease = Open-ShortcutFile $path
        try { Check ($lease.Identity -eq $record.FileId) "$operation partial write retains the recorded file identity" } finally { $lease.Dispose() }
        Check ((Get-Item -LiteralPath $path).Length -eq $originalBytes.Length -and ![ShortcutFile]::IsReadableShortcut($path)) "$operation partial in-place write is unreadable"
        $partialHash = (Get-FileHash -LiteralPath $path).Hash
        $legacyRecord = [pscustomobject]@{ Path = $path; Backup = $record.Backup; Arguments = ''; AppliedHash = $record.AppliedHash }
        Reject { Update-Shortcut $path $operation $legacyRecord } "$operation refuses unreadable legacy recovery without a recorded identity"
        Check ((Get-FileHash -LiteralPath $path).Hash -eq $partialHash -and (Get-FileHash -LiteralPath $manifestPath).Hash -eq $journalHash -and (Get-FileHash -LiteralPath $backupPath).Hash -eq $backupHash) "$operation missing-identity refusal preserves shortcut and recovery data"
        Update-Shortcut $path $operation $record
        if ($operation -eq 'Remove') {
            Check ((Get-FileHash -LiteralPath $path).Hash -eq $backupHash) 'removal recovers a same-identity partial write byte-for-byte'
        } else {
            Check ([ShortcutFile]::IsReadableShortcut($path) -and $shell.CreateShortcut($path).TargetPath -eq $installed) "$operation recovers a same-identity partial write to the addon target"
        }
        Check ((Get-FileHash -LiteralPath $backupPath).Hash -eq $backupHash -and (Get-Item -LiteralPath $backupPath).LastWriteTimeUtc -eq $backupWritten) "$operation partial-write recovery preserves the original backup"
    }
    # Exercise the production cleanup after real shortcut writes have produced several recovery records.
    $backups = @($records | Where-Object Backup | ForEach-Object Backup)
    Check ($backups.Count -gt 1) 'cleanup fixture has multiple recorded backups'
    [IO.File]::WriteAllText((Join-Path $state 'shortcuts.new'), 'unfinished journal fixture')
    $cleanupNames = @('shortcuts.new') + $backups + @('shortcuts.json')
    $cleanupHashes = @{}
    foreach ($name in $cleanupNames) { $cleanupHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $state $name)).Hash }
    foreach ($lockedName in @('shortcuts.new', $backups[-1])) {
        $locked = [IO.File]::Open((Join-Path $state $lockedName), 'Open', 'Read', 'Read')
        $failure = $null
        try { Remove-ShortcutStateFiles } catch { $failure = $_ }
        finally { $locked.Dispose() }
        Check ($failure -and $failure.Exception.GetBaseException() -is [ComponentModel.Win32Exception] -and $failure.Exception.GetBaseException().NativeErrorCode -eq 32) "cleanup rejects locked $lockedName with a sharing violation"
        foreach ($name in $cleanupNames) {
            $recoveryPath = Join-Path $state $name
            Check ((Test-Path -LiteralPath $recoveryPath) -and (Get-FileHash -LiteralPath $recoveryPath).Hash -eq $cleanupHashes[$name]) "locked $lockedName preserves recovery file $name"
            Check ((Get-ShortcutHash (Read-ShortcutBytes $recoveryPath)).Replace('-','') -eq $cleanupHashes[$name]) "failed cleanup releases its handle for $name"
        }
    }
    $lastBackup = Join-Path $state $backups[-1]
    $backupBytes = [IO.File]::ReadAllBytes($lastBackup)
    foreach ($linkType in @('HardLink','Junction')) {
        [IO.File]::Delete($lastBackup)
        $target = if ($linkType -eq 'HardLink') { $victim } else { $outside }
        New-Item -ItemType $linkType -Path $lastBackup -Target $target | Out-Null
        $failure = $null
        try { Remove-ShortcutStateFiles } catch { $failure = $_ }
        finally {
            if ($linkType -eq 'Junction') { [IO.Directory]::Delete($lastBackup) } else { [IO.File]::Delete($lastBackup) }
            [IO.File]::WriteAllBytes($lastBackup, $backupBytes)
        }
        if ($linkType -eq 'Junction') {
            Check ($failure -and $failure.Exception.GetBaseException() -is [ComponentModel.Win32Exception] -and $failure.Exception.GetBaseException().NativeErrorCode -eq 5) 'cleanup refuses to acquire a directory junction as a backup file'
        } else { Check ($failure -and $failure.ToString().Contains('link')) 'cleanup rejects a later hard-linked backup' }
        foreach ($name in $cleanupNames) { Check ((Get-FileHash -LiteralPath (Join-Path $state $name)).Hash -eq $cleanupHashes[$name]) "$linkType refusal preserves recovery file $name" }
        Check ((Get-FileHash -LiteralPath $victim).Hash -eq $sentinel) "$linkType cleanup refusal preserves the outside victim"
    }
    $unknown = Join-Path $state ('shortcut-' + [Guid]::NewGuid().ToString('N') + '.lnk')
    $lookalike = Join-Path $state 'shortcuts.json.bak'
    foreach ($unowned in @($unknown,$lookalike)) { [IO.File]::WriteAllText($unowned, 'unowned fixture') }
    $script:afterOpen = {
        param($opened,$create,$lease)
        if ($opened -ne $manifestPath) { return }
        foreach ($name in $cleanupNames) {
            Check (Test-Path -LiteralPath (Join-Path $state $name)) "cleanup acquires the journal before deleting $name"
            Reject { [IO.File]::WriteAllText((Join-Path $state $name), 'racing edit') } "cleanup holds $name until the entire set is acquired"
        }
    }
    Remove-ShortcutStateFiles
    $script:afterOpen = $null
    foreach ($name in $cleanupNames) { Check (!(Test-Path -LiteralPath (Join-Path $state $name))) "cleanup retry removes $name after releasing locks" }
    foreach ($unowned in @($unknown,$lookalike)) { Check ([IO.File]::ReadAllText($unowned) -eq 'unowned fixture') 'cleanup preserves unrecorded files and lookalikes' }
    Reject { $otherLock = Open-ShortcutJournal $state; $otherLock.Dispose() } 'cleanup retains the caller journal lock'
} finally {
    [ShortcutRaceProbe]::BeforeLeaf = $null
    if ($journalLock) { $journalLock.Dispose() }
    if (Test-Path -LiteralPath $root) { Cleanup $root }
}
