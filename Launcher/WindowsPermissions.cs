using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HytaleAccounts;

internal static class WindowsPermissions
{
    private static readonly string User = WindowsIdentity.GetCurrent().User!.Value;
    private static bool Trusted(string sid, bool administratorsOnly) =>
        sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" ||
        (!administratorsOnly && sid == User);

    internal static void RequireMachine(FileSystemSecurity security, string path, bool ancestor = false) =>
        Require(security, path, administratorsOnly: true, ancestor: ancestor);

    internal static void Require(FileSystemSecurity security, string path, bool administratorsOnly = false, bool ancestor = false,
        bool allowInheritedShortcutDelete = false)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.Owner is null || !Trusted(descriptor.Owner.Value, administratorsOnly))
            throw new IOException($"Unsafe owner of '{path}'; operation stopped and files preserved.");
        if (descriptor.DiscretionaryAcl is null)
            throw new IOException($"Unrestricted permissions on '{path}'; operation stopped and files preserved.");
        var mutation = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        // These rights let users create siblings and change the ancestor's own attributes.
        if (ancestor && security is DirectorySecurity)
            mutation &= ~(FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories |
                FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes);
        var denied = new Dictionary<string, uint>();
        for (var index = 0; index < descriptor.DiscretionaryAcl.Count; index++)
        {
            var entry = descriptor.DiscretionaryAcl[index];
            if ((entry.AceFlags & AceFlags.InheritOnly) != 0) continue;
            if (entry is not CommonAce ace || ace.IsCallback || ace.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                throw new IOException($"Unsupported access rule on '{path}'; operation stopped and files preserved.");
            var sid = ace.SecurityIdentifier.Value;
            var rights = MapGeneric((uint)ace.AccessMask);
            if (ace.AceQualifier == AceQualifier.AccessDenied)
            { denied[sid] = denied.GetValueOrDefault(sid) | rights; continue; }
            // Package/capability access also needs the normal user/group side of an AppContainer token.
            if (Trusted(sid, administratorsOnly) || sid == "S-1-3-4" || sid.StartsWith("S-1-15-2-", StringComparison.Ordinal) ||
                sid.StartsWith("S-1-15-3-", StringComparison.Ordinal)) continue;
            // Evaluate each trustee separately: a deny for one group cannot protect an allow for another.
            var effective = rights & ~(denied.GetValueOrDefault(sid) | denied.GetValueOrDefault("S-1-1-0"));
            var effectiveMutation = effective & (uint)mutation;
            // The checked parent prevents replacement. The delete-child right applies only to directories.
            if (allowInheritedShortcutDelete && security is FileSecurity && (ace.AceFlags & AceFlags.Inherited) != 0)
                effectiveMutation &= ~(uint)(FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles);
            if (effectiveMutation != 0)
                throw new IOException($"Unsafe mutation permissions on '{path}'; operation stopped and files preserved. " +
                    $"ACE {index} ({ace.AceFlags}), mask 0x{(uint)ace.AccessMask:X8}, effective mutation 0x{effectiveMutation:X8}.");
        }
    }

    private static uint MapGeneric(uint mask)
    {
        if ((mask & 0x10000000) != 0) mask |= 0x1f01ff;
        if ((mask & 0x40000000) != 0) mask |= 0x120116;
        if ((mask & 0x80000000) != 0) mask |= 0x120089;
        if ((mask & 0x20000000) != 0) mask |= 0x1200a0;
        return mask & 0x0fffffff;
    }


    internal static T PrivateAcl<T>() where T : FileSystemSecurity, new()
    {
        var acl = new T();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = acl is DirectorySecurity ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in new[] { User, "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    internal static FileSecurity MachineShortcutBackupAcl()
    {
        var acl = new FileSecurity();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        acl.SetAccessRuleProtection(true, false);
        foreach (var (sid, rights) in new[] { (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl), (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute) })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow));
        return acl;
    }

    private static byte[] Dacl(FileSystemSecurity acl)
    {
        var dacl = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl;
        if (dacl is null) return [];
        var bytes = new byte[dacl.BinaryLength];
        dacl.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static bool IsPrivate(FileSystemSecurity actual, FileSystemSecurity expected) =>
        actual.AreAccessRulesProtected && Dacl(actual).AsSpan().SequenceEqual(Dacl(expected));

    internal static void RequirePrivate(FileSystemSecurity actual, FileSystemSecurity expected)
    {
        if (!IsPrivate(actual, expected)) throw new IOException("Windows could not apply private permissions.");
    }

    internal static FileSystemSecurity Read(SafeFileHandle handle, bool directory)
    {
        _ = GetKernelObjectSecurity(handle, 7, null, 0, out var length);
        if (length == 0) throw new IOException("Could not read Windows permissions.", new Win32Exception(Marshal.GetLastWin32Error()));
        var descriptor = new byte[length];
        if (!GetKernelObjectSecurity(handle, 7, descriptor, length, out _))
            throw new IOException("Could not read Windows permissions.", new Win32Exception(Marshal.GetLastWin32Error()));
        FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity();
        security.SetSecurityDescriptorBinaryForm(descriptor);
        return security;
    }

    internal static void CreatePrivateDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (WindowsFiles.DirectoryExists(path))
        {
            using var existing = new WindowsFiles.DirectoryPin(path);
            return;
        }

        var parent = Path.GetDirectoryName(path)!;
        CreatePrivateDirectory(parent);
        using var directories = new WindowsFiles.DirectoryPin(parent);
        var acl = PrivateAcl<DirectorySecurity>();
        var descriptor = GCHandle.Alloc(acl.GetSecurityDescriptorBinaryForm(), GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor.AddrOfPinnedObject() };
            if (!CreateDirectory(path, ref attributes))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 183) throw WindowsFiles.Error(path, new Win32Exception(error));
            }
        }
        finally { descriptor.Free(); }

        using var created = new WindowsFiles.DirectoryPin(path);
        RequirePrivate(Read(created.Leaf, directory: true), acl);
    }

    internal static void SecureTree(string root)
    {
        CreatePrivateDirectory(root);
        using var rootParent = new WindowsFiles.DirectoryPin(Path.GetDirectoryName(Path.GetFullPath(root))!);
        var paths = new List<(string Path, bool Directory, SafeFileHandle Handle)>();
        var children = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Inspect(root);
            RequireUnchanged();
            foreach (var (path, directory, handle) in paths.AsEnumerable().Reverse())
            {
                FileSystemSecurity expected = directory ? PrivateAcl<DirectorySecurity>() : PrivateAcl<FileSecurity>();
                if (IsPrivate(Read(handle, directory), expected)) continue;
                // SetKernelObjectSecurity changes only the pinned object, without propagating to uninspected children.
                if (!SetKernelObjectSecurity(handle, 0x80000004, expected.GetSecurityDescriptorBinaryForm()))
                    throw WindowsFiles.Error(path, new Win32Exception(Marshal.GetLastWin32Error()));
                RequirePrivate(Read(handle, directory), expected);
            }
            RequireUnchanged();
        }
        finally
        {
            foreach (var (_, _, handle) in paths.AsEnumerable().Reverse()) handle.Dispose();
        }

        void Inspect(string path)
        {
            var directory = WindowsFiles.DirectoryExists(path);
            using var parent = new WindowsFiles.DirectoryPin(Path.GetDirectoryName(path)!);
            var handle = WindowsFiles.OpenForMigration(parent, path, directory);
            paths.Add((path, directory, handle));
            var acl = Read(handle, directory);
            Require(acl, path);
            if (acl.GetOwner(typeof(SecurityIdentifier))?.Value is not { } owner || (owner != User && owner is not ("S-1-5-18" or "S-1-5-32-544")))
                throw new IOException($"Addon path '{path}' belongs to another Windows user. Files were preserved.");
            if (!directory) return;
            children[path] = Directory.GetFileSystemEntries(path);
            foreach (var child in children[path]) Inspect(child);
        }

        void RequireUnchanged()
        {
            foreach (var (directory, inspected) in children)
                if (!new HashSet<string>(inspected, StringComparer.OrdinalIgnoreCase).SetEquals(Directory.EnumerateFileSystemEntries(directory)))
                    throw new IOException("Addon directory changed during permission migration.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);
}
