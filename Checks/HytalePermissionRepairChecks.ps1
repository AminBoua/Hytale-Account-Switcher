$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path (Split-Path $PSScriptRoot) 'Repair-HytalePermissions.ps1'
$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('hytale-repair-' + [Guid]::NewGuid().ToString('N'))
$target = Join-Path $root 'Hytale'
function Check($condition, $message) { if (!$condition) { throw $message }; Write-Host "PASS $message" }
function Reject([scriptblock]$action, $message) { $rejected=$false; try { & $action | Out-Null } catch { $rejected=$true }; Check $rejected $message }
[IO.Directory]::CreateDirectory($root) | Out-Null
try {
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetSecurityDescriptorSddlForm("O:$($user)G:SYD:P(A;OICI;FA;;;$user)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", [Security.AccessControl.AccessControlSections]14)
    [IO.DirectoryInfo]::new($root).SetAccessControl($acl)
    [IO.Directory]::CreateDirectory($target) | Out-Null
    $acl.SetSecurityDescriptorSddlForm("O:$($user)G:SYD:P(A;OICI;FA;;;BU)(A;OICI;FA;;;$user)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;FR;;;WD)", [Security.AccessControl.AccessControlSections]14)
    [IO.DirectoryInfo]::new($target).SetAccessControl($acl)
    $child = Join-Path $target 'native-session.bin'
    [IO.File]::WriteAllBytes($child, [byte[]](0,1,255,42))
    $sub = [IO.Directory]::CreateDirectory((Join-Path $target 'data'))
    # Load helper through a fixture-only preview.
    $preview = & $scriptPath -Path $target
    Check $preview.ChangeRequired 'reported explicit Full Control grant is repairable'
    Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class RepairFixtureNative {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern SafeFileHandle CreateFile(string p,uint a,uint s,IntPtr sa,uint c,uint f,IntPtr t);
}
"@
    function Descriptor($p) {
        $h=[RepairFixtureNative]::CreateFile($p,0x20000,7,[IntPtr]::Zero,3,0x02200000,[IntPtr]::Zero)
        try { [Convert]::ToBase64String([HytalePermissionRepair]::Read($h)) } finally { $h.Dispose() }
    }
    function RootDescriptor($p) { Descriptor $p }
    $before=RootDescriptor $target; $fileBefore=Descriptor $child; $subBefore=RootDescriptor $sub.FullName
    & $scriptPath -Path $target | Out-Null
    Check ((RootDescriptor $target) -ceq $before) 'preview preserves descriptor'
    Check (@(Get-ChildItem $root).Count -eq 1) 'preview creates no backup or data'
    Reject { & $scriptPath -Path $target -Apply } 'apply requires backup'
    Reject { & $scriptPath -Path $target -Apply -BackupPath (Join-Path $target 'backup.bin') } 'inside backup rejected'
    Check ((RootDescriptor $target) -ceq $before -and (Descriptor $child) -ceq $fileBefore) 'rejected applies preserve root and child descriptors'
    $backup=Join-Path $root 'original.bin'
    & $scriptPath -Path $target -Apply -BackupPath $backup | Out-Null
    Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($backup)) -ceq $before) 'original descriptor saved exactly'
    $raw=[Security.AccessControl.RawSecurityDescriptor]::new([IO.File]::ReadAllBytes($backup),0)
    foreach ($ace in $raw.DiscretionaryAcl) { if ($ace.SecurityIdentifier.Value -eq 'S-1-5-32-545') { $ace.AccessMask = 0x1200A9 } }
    $expected=New-Object byte[] $raw.BinaryLength; $raw.GetBinaryForm($expected,0)
    Check ([HytalePermissionRepair]::Comparable([Convert]::FromBase64String((RootDescriptor $target))) -ceq [HytalePermissionRepair]::Comparable($expected)) 'only mutation bits removed; owner and unrelated ACEs preserved'
    Check ((Descriptor $child) -ceq $fileBefore -and (RootDescriptor $sub.FullName) -ceq $subBefore) 'child file and directory stored descriptors unchanged'
    Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($child)) -ceq 'AAH/Kg==') 'native session bytes unchanged'
    $again=& $scriptPath -Path $target -Apply -BackupPath (Join-Path $root 'unused.bin')
    Check (!$again.ChangeRequired -and !(Test-Path (Join-Path $root 'unused.bin'))) 'idempotent apply creates no backup'
    Reject { & $scriptPath -Path $root } 'wrong target rejected'
    Reject { & $scriptPath -Path ($target + '\..\Hytale') } 'ambiguous target rejected'
    $hs=[HytalePermissionRepair]::Pin($target,$true)
    try { [HytalePermissionRepair]::Write($hs[$hs.Count-1], [IO.File]::ReadAllBytes($backup)) } finally { $hs | ForEach-Object { $_.Dispose() } }
    Check ([HytalePermissionRepair]::Comparable([Convert]::FromBase64String((RootDescriptor $target))) -ceq [HytalePermissionRepair]::Comparable([Convert]::FromBase64String($before)) -and (Descriptor $child) -ceq $fileBefore) 'documented kernel recovery restores root without propagation'
    Reject { & $scriptPath -Path $target -Apply -BackupPath $backup } 'existing backup never overwritten'
    foreach ($sddl in @(
        "O:$($user)G:SYD:P(A;OICI;FW;;;BU)(A;;FA;;;$user)",
        "O:$($user)G:SYD:P(A;OICIIO;FA;;;BU)(A;;FA;;;$user)",
        "O:$($user)G:SYD:P(A;OICIID;FA;;;BU)(A;;FA;;;$user)",
        "O:$($user)G:SYD:P(A;OICI;FA;;;BU)(A;;FA;;;WD)(A;;FA;;;$user)",
        "O:$($user)G:SYD:NO_ACCESS_CONTROL",
        "O:S-1-5-21-1-2-3-1234G:SYD:P(A;OICI;FA;;;BU)(A;;FA;;;$user)"
    )) {
        $bad=[Security.AccessControl.RawSecurityDescriptor]::new($sddl)
        $bytes=New-Object byte[] $bad.BinaryLength; $bad.GetBinaryForm($bytes,0)
        Reject { [HytalePermissionRepair]::Plan($bytes) } "unsupported ACL rejected: $sddl"
    }
    foreach ($flags in @('P','AI','')) {
        $reported=[Security.AccessControl.RawSecurityDescriptor]::new("O:BAG:SYD:$flags(A;OICI;FA;;;BU)(A;OICI;FA;;;BA)(A;OICI;FA;;;$user)")
        $bytes=New-Object byte[] $reported.BinaryLength; $reported.GetBinaryForm($bytes,0)
        $planned=[Security.AccessControl.RawSecurityDescriptor]::new([HytalePermissionRepair]::Plan($bytes),0)
        Check ($planned.Owner.Value -eq 'S-1-5-32-544' -and $planned.DiscretionaryAcl[0].AccessMask -eq 0x1200A9) "Administrators-owned reported shape supported ($flags)"
    }
    $link=Join-Path $root 'junction'
    cmd /c mklink /J $link $target | Out-Null
    try { Reject { [HytalePermissionRepair]::Pin($link,$true) } 'reparse target rejected'; Reject { & $scriptPath -Path (Join-Path $link 'Hytale') } 'reparse ancestor rejected' } finally { cmd /c rmdir $link }
    Write-Host 'All Hytale permission repair checks passed (Windows PowerShell).'
} finally {
    # Only this independently generated fixture is removed; junction is removed above.
    if (Test-Path $root) {
        $resolved = (Resolve-Path -LiteralPath $root).ProviderPath
        if ($resolved -cne [IO.Path]::GetFullPath($root) -or [IO.Path]::GetFileName($resolved) -notmatch '^hytale-repair-[0-9a-f]{32}$') { throw 'Unexpected fixture cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
