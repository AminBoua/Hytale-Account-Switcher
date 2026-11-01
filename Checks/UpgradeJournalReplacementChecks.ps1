$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
. (Join-Path $repository 'InstallerSafety.ps1')
. (Join-Path $repository 'ShortcutIntegration.ps1')
Add-Type -TypeDefinition @'
public static class UpgradeJournalProbe
{
    public static bool IsNull(string value) { return value == null; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle Pin(string path, uint access, uint share,
        System.IntPtr security, uint mode, uint flags, System.IntPtr template);
}
'@
function Check($Condition, [string]$Message) {
    if (!$Condition) { throw "FAIL $Message" }
    Write-Host "PASS $Message"
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('upgrade-journal-' + [Guid]::NewGuid().ToString('N'))
$pin = $null; $journalLock = $null
try {
    $null = New-Item -ItemType Directory -Path $root
    $state = $root; $manifestPath = Join-Path $state 'shortcuts.json'
    $roots = @($root); $repairRoots = $roots; $ownerSid = ''
    $script:records = @([pscustomobject]@{ Path = (Join-Path $root 'launcher.lnk'); Backup = 'original-backup.lnk' })
    [IO.File]::WriteAllText($manifestPath, '{"original":"journal"}')
    $acl = Get-Acl -LiteralPath $manifestPath
    $acl.SetAccessRuleProtection($true, $true)
    Set-Acl -LiteralPath $manifestPath -AclObject $acl
    $original = [IO.File]::ReadAllText($manifestPath)
    $permissions = (Get-Acl -LiteralPath $manifestPath).Sddl
    $journalLock = Open-ShortcutJournal $state
    Write-ShortcutBytes (Join-Path $state 'shortcuts.new') ([Text.Encoding]::UTF8.GetBytes('{}'))
    Check ((Get-Acl -LiteralPath (Join-Path $state 'shortcuts.new')).Sddl -cne $permissions) 'staged journal starts with a different inherited ACL'
    Check (![UpgradeJournalProbe]::IsNull($null)) 'PowerShell string binding converts ordinary null to an empty string'
    Check ([UpgradeJournalProbe]::IsNull([System.Management.Automation.Language.NullString]::Value)) 'journal backup argument binds to a true null string'

    # Match the released setup pin; no executable or destination file handle is open.
    $pin = [UpgradeJournalProbe]::Pin($root, 0x20081, 1, [IntPtr]::Zero, 3, 0x02200000, [IntPtr]::Zero)
    Check (!$pin.IsInvalid) 'released read-sharing directory pin acquired'
    $failure = $null
    try { Save-ShortcutJournal } catch { $failure = $_ }
    Check ($null -ne $failure -and $failure.Exception.InnerException.HResult -eq -2147023721 -and
        $failure.ToString().Contains('"Replace" with "3" argument(s)')) 'real journal publication reproduces three-argument Replace error 1175'
    Check ([IO.File]::ReadAllText($manifestPath) -ceq $original) 'failed replacement retains the original journal'
    Check (Test-Path -LiteralPath (Join-Path $state 'shortcuts.new')) 'failed replacement retains the staged journal'

    # Match forMutation: acquire the write-sharing pin before releasing the restrictive pin.
    $mutable = [UpgradeJournalProbe]::Pin($root, 0x20081, 3, [IntPtr]::Zero, 3, 0x02200000, [IntPtr]::Zero)
    if ($mutable.IsInvalid) { $mutable.Dispose(); throw 'Could not acquire mutation pin.' }
    $pin.Dispose(); $pin = $mutable
    Save-ShortcutJournal
    $journal = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($journal.Shortcuts[0].Backup -ceq $records[0].Backup) 'write-sharing pin permits atomic publication with restoration records intact'
    Check ((Get-Acl -LiteralPath $manifestPath).Sddl -ceq $permissions) 'replacement retains the original journal ACL'
    Check (!(Test-Path -LiteralPath (Join-Path $state 'shortcuts.new'))) 'successful replacement consumes its stage'
    $journalLock.Dispose(); $journalLock = $null
    $blocked = $false
    try { [IO.Directory]::Move($root, ($root + '-moved')) } catch { $blocked = $true }
    Check $blocked 'mutation pin still blocks directory replacement'
} finally {
    if ($pin) { $pin.Dispose() }
    if ($journalLock) { $journalLock.Dispose() }
    $resolved = [IO.Path]::GetFullPath($root)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('upgrade-journal-')) { throw 'Cleanup escaped journal fixture.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
