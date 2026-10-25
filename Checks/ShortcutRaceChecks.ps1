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
    $script:beforeOpen = {
        param($opened,$create)
        if ($opened -eq $fixtureShortcut -and $create) { $script:beforeOpen = $null; [IO.File]::WriteAllText($fixtureShortcut, 'new user file') }
    }
    Reject { Update-Shortcut $path Remove $record } 'missing shortcut restore refuses a concurrently created leaf'
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
} finally {
    [ShortcutRaceProbe]::BeforeLeaf = $null
    if ($journalLock) { $journalLock.Dispose() }
    if (Test-Path -LiteralPath $root) { Cleanup $root }
}
