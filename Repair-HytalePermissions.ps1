<#
.SYNOPSIS
Preview the narrowly supported v0.1.5 Hytale root permission repair.
.DESCRIPTION
Close Hytale before applying. Run with Windows PowerShell 5.1. No elevation is
started automatically. -Apply requires a new -BackupPath outside Hytale.
Only the explicit BUILTIN\Users OI/CI Full Control ACE is reduced by 0xD0156
(write, delete, permissions and ownership); read/execute and all other ACEs stay.
Children are neither inspected nor repaired: a root repair does not prove them safe.

Recovery (restores the original root DACL, including its unsafe Users access):
In the same PowerShell session, dot-source a preview to load the helper:
  . .\Repair-HytalePermissions.ps1
Then, after reviewing the backup and closing Hytale:
  $p = [Environment]::GetFolderPath('ApplicationData') + '\Hytale'
  $h = [HytalePermissionRepair]::Pin($p, $true)
  try { [HytalePermissionRepair]::Write($h[$h.Count-1], [IO.File]::ReadAllBytes('C:\path\original-acl.bin')) } finally { $h | ForEach-Object { $_.Dispose() } }
Write uses the non-propagating kernel setter; never use Set-Acl for recovery.
The backup contains owner/group/DACL, not session bytes; ownership is never set.
#>
[CmdletBinding()]
param(
    [string]$Path = ([Environment]::GetFolderPath('ApplicationData') + '\Hytale'),
    [switch]$Apply,
    [string]$BackupPath
)
$ErrorActionPreference = 'Stop'
if (-not ('HytalePermissionRepair' -as [type])) {
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
public static class HytalePermissionRepair {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string p, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int c, out Tag t, uint n);
    [StructLayout(LayoutKind.Sequential)] struct Tag { public uint Attributes, ReparseTag; }
    [DllImport("advapi32.dll", SetLastError=true)]
    static extern bool GetKernelObjectSecurity(SafeFileHandle h, uint i, byte[] b, uint n, out uint needed);
    [DllImport("advapi32.dll", SetLastError=true)]
    static extern bool SetKernelObjectSecurity(SafeFileHandle h, uint i, byte[] b);
    static void Error() { throw new Win32Exception(Marshal.GetLastWin32Error()); }
    static bool Trusted(string s) { return s == WindowsIdentity.GetCurrent().User.Value || s == "S-1-5-18" || s == "S-1-5-32-544" || s == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"; }
    public static byte[] Read(SafeFileHandle h) {
        uint n; GetKernelObjectSecurity(h, 7, null, 0, out n);
        if (n == 0) Error();
        byte[] b = new byte[n]; if (!GetKernelObjectSecurity(h, 7, b, n, out n)) Error(); return b;
    }
    public static void Write(SafeFileHandle h, byte[] b) {
        // Kernel setter does not propagate inheritable ACEs to existing children.
        if (!SetKernelObjectSecurity(h, 4, b)) Error();
        if (Comparable(Read(h)) != Comparable(b))
            throw new IOException("Root descriptor verification failed; retain the backup for recovery.");
    }
    public static string Comparable(byte[] b) {
        var d = new RawSecurityDescriptor(b,0);
        // The kernel clears auto-inheritance bookkeeping without propagating to children.
        d.SetFlags(d.ControlFlags & ~(ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.DiscretionaryAclAutoInheritRequired));
        return d.GetSddlForm(AccessControlSections.All);
    }
    static RawSecurityDescriptor Validate(byte[] b, bool ancestor) {
        var d = new RawSecurityDescriptor(b,0);
        if (d.Owner == null || !Trusted(d.Owner.Value) || d.DiscretionaryAcl == null) throw new IOException("Unsafe owner or null DACL.");
        if (!new CommonSecurityDescriptor(true, false, d).IsDiscretionaryAclCanonical) throw new IOException("Noncanonical DACL unsupported.");
        foreach (GenericAce e in d.DiscretionaryAcl) {
            var a = e as CommonAce;
            if (a == null || a.IsCallback || (a.AceQualifier != AceQualifier.AccessAllowed && a.AceQualifier != AceQualifier.AccessDenied)) throw new IOException("Unsupported ACE.");
            if (a.AceQualifier != AceQualifier.AccessAllowed || (a.AceFlags & AceFlags.InheritOnly) != 0 || Trusted(a.SecurityIdentifier.Value) || a.SecurityIdentifier.Value == "S-1-3-4" || a.SecurityIdentifier.Value.StartsWith("S-1-15-2-") || a.SecurityIdentifier.Value.StartsWith("S-1-15-3-")) continue;
            if (((uint)a.AccessMask & 0xF0000000) != 0) throw new IOException("Generic rights unsupported.");
            if (!ancestor && a.SecurityIdentifier.Value == "S-1-5-32-545") continue;
            if (((uint)a.AccessMask & (ancestor ? 0xD0150u : 0xD0156u)) != 0) throw new IOException("Unrelated unsafe mutation grant.");
        }
        return d;
    }
    public static SafeFileHandle[] Pin(string path, bool write) {
        string full = Path.GetFullPath(path);
        if (!System.Text.RegularExpressions.Regex.IsMatch(full, @"^[A-Za-z]:\\") || full.IndexOf(':',2) >= 0 || full.IndexOfAny(new char[]{'*','?'}) >= 0) throw new IOException("Local filesystem path required.");
        var names = new List<string>();
        for (var p = new DirectoryInfo(full); p != null; p = p.Parent) names.Add(p.FullName);
        names.Reverse(); var handles = new List<SafeFileHandle>();
        try {
            for (int i=0;i<names.Count;i++) {
                var h = CreateFile(names[i], 0x20080u | (write && i==names.Count-1 ? 0x40000u : 0u), 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                handles.Add(h); if (h.IsInvalid) Error();
                Tag tag; if (!GetFileInformationByHandleEx(h,9,out tag,8)) Error();
                if ((tag.Attributes & 0x410) != 0x10) throw new IOException("Reparse point or non-directory target.");
                Validate(Read(h), i != names.Count-1 || !String.Equals(Path.GetFileName(full), "Hytale", StringComparison.Ordinal));
            }
            return handles.ToArray();
        } catch { foreach(var h in handles) h.Dispose(); throw; }
    }
    public static byte[] Plan(byte[] b) {
        var d = Validate(b,false); int found=0;
        if (!new CommonSecurityDescriptor(true, false, d).IsDiscretionaryAclCanonical) throw new IOException("Noncanonical DACL unsupported.");
        foreach (GenericAce e in d.DiscretionaryAcl) {
            var a = (CommonAce)e;
            if (a.SecurityIdentifier.Value != "S-1-5-32-545") continue;
            if (a.AceQualifier != AceQualifier.AccessAllowed || a.AceFlags != (AceFlags.ObjectInherit | AceFlags.ContainerInherit) || (a.AccessMask != 0x1F01FF && a.AccessMask != 0x1200A9)) throw new IOException("Unsupported Users grant shape.");
            found++; a.AccessMask &= ~0xD0156;
        }
        if (found != 1) throw new IOException("Exactly one explicit Users OI/CI grant required.");
        byte[] result = new byte[d.BinaryLength]; d.GetBinaryForm(result,0); return result;
    }
}
"@
}
$full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
if ([IO.Path]::GetFileName($full) -cne 'Hytale' -or $Path -match '(^|[\\/])\.\.?([\\/]|$)' -or $Path -match '[. ]([\\/]|$)') { throw 'Target must be an unambiguous local Hytale directory.' }
$handles = @()
$backupHandles = @()
try {
    $handles = [HytalePermissionRepair]::Pin($full, $Apply.IsPresent)
    $handle = $handles[$handles.Count-1]
    $before = [HytalePermissionRepair]::Read($handle)
    $after = [HytalePermissionRepair]::Plan($before)
    $changed = [Convert]::ToBase64String($before) -cne [Convert]::ToBase64String($after)
    if (!$Apply -or !$changed) {
        [pscustomobject]@{ Path=$full; Mode='Preview'; ChangeRequired=$changed; ChildrenValidated=$false }
        return
    }
    if (!$BackupPath) { throw '-Apply requires a new -BackupPath outside Hytale.' }
    $backup = [IO.Path]::GetFullPath($BackupPath)
    if ($backup.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase) -or ($backup -ieq $full) -or $backup.Substring(2).Contains(':')) { throw 'Backup must be outside Hytale, without alternate streams.' }
    $backupHandles = [HytalePermissionRepair]::Pin([IO.Path]::GetDirectoryName($backup), $false)
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'Allow'))
    }
    $stream = [IO.FileStream]::new($backup, [IO.FileMode]::CreateNew, [Security.AccessControl.FileSystemRights]::FullControl, [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough, $acl)
    try {
        if ($stream.GetAccessControl().GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access) -cne $acl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)) { throw 'Backup ACL verification failed; root unchanged.' }
        $stream.Write($before, 0, $before.Length); $stream.Flush($true)
        $stream.Position = 0
        $saved = New-Object byte[] $before.Length
        if ($stream.Read($saved, 0, $saved.Length) -ne $saved.Length -or [Convert]::ToBase64String($saved) -cne [Convert]::ToBase64String($before)) { throw 'Backup byte verification failed; root unchanged.' }
        if ([Convert]::ToBase64String([HytalePermissionRepair]::Read($handle)) -cne [Convert]::ToBase64String($before)) { throw 'Root descriptor changed before apply.' }
        [HytalePermissionRepair]::Write($handle, $after)
    } finally { $stream.Dispose() }
    [pscustomobject]@{ Path=$full; Mode='Applied'; Backup=$backup; ChildrenValidated=$false }
} finally {
    foreach ($h in $backupHandles) { $h.Dispose() }
    foreach ($h in $handles) { $h.Dispose() }
}
