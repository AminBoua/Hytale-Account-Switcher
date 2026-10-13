using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HytaleAccounts;

internal static class PrivateFiles
{
    private static readonly SecurityIdentifier[] Owners =
    [
        WindowsIdentity.GetCurrent().User ?? throw new IOException("Windows user SID is unavailable."),
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null)
    ];

    private static T Permissions<T>() where T : FileSystemSecurity, new()
    {
        var acl = new T();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = acl is DirectorySecurity ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        foreach (var sid in Owners)
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    internal static void CreateDirectory(string path)
    {
        NativeStores.RegularPath(path);
        if (StorageSafety.DirectoryExists(path))
        {
            using var existing = new SetupPayload.PinnedDirectories(path, requireTrusted: false);
            return;
        }
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        CreateDirectory(parent);
        using var directories = new SetupPayload.PinnedDirectories(parent, requireTrusted: false);
        var directory = new DirectoryInfo(path);
        var acl = Permissions<DirectorySecurity>();
        SetupPayload.CreateDirectory(path, acl);
        RequirePermissions(directory.GetAccessControl(), acl);
    }

    internal static FileStream CreateFile(string path)
    {
        NativeStores.RegularPath(path);
        using var directories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(Path.GetFullPath(path))!, requireTrusted: false);
        var acl = Permissions<FileSecurity>();
        FileStream file;
        try { file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions,
            FileShare.None, 4096, FileOptions.None, acl); }
        catch (IOException ex) { throw StorageSafety.Error(path, ex); }
        try { RequirePermissions(file.GetAccessControl(), acl); return file; }
        catch { file.Dispose(); throw; }
    }

    internal static void SecureTree(string root)
    {
        CreateDirectory(root);
        var entries = new List<string>();
        var children = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var leases = new List<IDisposable>();
        try
        {
            void Inspect(string path)
            {
                NativeStores.RegularPath(path);
                entries.Add(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                {
                    leases.Add(new SetupPayload.PinnedDirectories(path, requireTrusted: false));
                    RequireOwner(new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner));
                    children[path] = Directory.GetFileSystemEntries(path);
                    foreach (var child in children[path]) Inspect(child);
                }
                else
                {
                    var file = SetupPayload.OpenSource(path);
                    leases.Add(file);
                    RequireOwner(file.GetAccessControl());
                }
            }
            // Keep validated objects pinned until every ACL is applied; pathname checks alone can race.
            Inspect(root);
            void RequireUnchangedTree()
            {
                foreach (var (directory, inspected) in children)
                    if (!new HashSet<string>(inspected, StringComparer.OrdinalIgnoreCase).SetEquals(Directory.EnumerateFileSystemEntries(directory)))
                        throw new IOException("Addon directory changed during ACL migration; initialization stopped.");
            }
            RequireUnchangedTree();
            foreach (var path in entries.AsEnumerable().Reverse())
            {
                NativeStores.RegularPath(path);
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                {
                    var directory = new DirectoryInfo(path);
                    RequireOwner(directory.GetAccessControl(AccessControlSections.Owner));
                    // Do not propagate ACLs to children inserted after inspection (including hard links).
                    var acl = Permissions<DirectorySecurity>();
                    SetDirectoryPermissions(path, acl);
                    RequirePermissions(directory.GetAccessControl(), acl);
                }
                else
                {
                    using var file = new FileInfo(path).Create(FileMode.Open, FileSystemRights.Read | FileSystemRights.ChangePermissions,
                        FileShare.Read, 4096, FileOptions.None, null);
                    RequireSingleFile(file.SafeFileHandle);
                    RequireOwner(file.GetAccessControl());
                    var acl = Permissions<FileSecurity>();
                    file.SetAccessControl(acl);
                    RequirePermissions(file.GetAccessControl(), acl);
                }
            }
            RequireUnchangedTree();
        }
        catch (Win32Exception ex) { throw new IOException("Could not lock addon paths for ACL migration; files preserved.", ex); }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }

    internal static void SetDirectoryPermissions(string path, DirectorySecurity acl)
    {
        using var handle = OpenDirectory(path, 0x60080, FileShare.Read, IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !SetKernelObjectSecurity(handle, 0x80000004, acl.GetSecurityDescriptorBinaryForm()))
            throw StorageSafety.Error(path, new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static void RequirePermissions(FileSystemSecurity actual, FileSystemSecurity expected)
    {
        static byte[] Dacl(FileSystemSecurity acl)
        {
            var dacl = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl;
            if (dacl is null) return [];
            var bytes = new byte[dacl.BinaryLength]; dacl.GetBinaryForm(bytes, 0); return bytes;
        }
        // Windows may add auto-inheritance bookkeeping flags without changing any access rules.
        if (!actual.AreAccessRulesProtected || !Dacl(actual).AsSpan().SequenceEqual(Dacl(expected)))
            throw new IOException("Private Windows permissions could not be applied; operation stopped.");
    }

    private static void RequireOwner(FileSystemSecurity acl)
    {
        if (!Owners.Contains(acl.GetOwner(typeof(SecurityIdentifier))))
            throw new IOException("Addon file belongs to another Windows user; files preserved.");
    }

    private static void RequireSingleFile(SafeFileHandle file)
    {
        if (!GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (info.Links != 1 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new IOException("Addon file contains a link; files preserved.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);
}
