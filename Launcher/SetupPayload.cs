using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HytaleAccounts;

internal sealed class SetupPayload : IDisposable
{
    internal static readonly string[] Scripts = ["Install.ps1", "Uninstall.ps1", "InstallerSafety.ps1", "Repair-Integration.ps1", "ShortcutIntegration.ps1", "ShortcutFile.cs"];
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private readonly List<FileStream> files = [];
    private PinnedDirectories? directories;
    internal string DirectoryPath { get; }

    private SetupPayload(string path) => DirectoryPath = path;

    internal static DirectorySecurity Permissions()
    {
        var acl = new DirectorySecurity();
        acl.SetOwner(Administrators);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Administrators, System })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    internal static void RequireTrusted(FileSystemSecurity acl)
    {
        StorageSafety.Require(acl, "installer payload", administratorsOnly: true);
    }

    internal sealed class PinnedDirectories : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        internal PinnedDirectories(string path, bool requireTrusted, bool forMutation = false)
        {
            try
            {
                var ancestors = new Stack<string>();
                for (var item = new DirectoryInfo(path); item != null; item = item.Parent) ancestors.Push(item.FullName);
                foreach (var ancestor in ancestors)
                {
                    var handle = CreateFile(ancestor, 0x20081, FileShare.Read, IntPtr.Zero, FileMode.Open,
                        0x02200000, IntPtr.Zero); // Deny write as well as deletion: an empty directory can become a junction in place.
                    if (handle.IsInvalid)
                    {
                        var error = new Win32Exception(Marshal.GetLastWin32Error()); handle.Dispose();
                        throw StorageSafety.Error(ancestor, error);
                    }
                    handles.Add(handle);
                    RequireRegular(handle, directory: true);
                    StorageSafety.Require(new DirectoryInfo(ancestor).GetAccessControl(), ancestor, requireTrusted,
                        ancestor: !ancestor.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                    if (forMutation)
                    {
                        // NTFS rename needs directory write sharing; exclude existing writers before trusting its DACL.
                        var mutable = CreateFile(ancestor, 0x20081, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
                        if (mutable.IsInvalid)
                        {
                            var error = new Win32Exception(Marshal.GetLastWin32Error()); mutable.Dispose(); throw StorageSafety.Error(ancestor, error);
                        }
                        handles[^1] = mutable; handle.Dispose();
                    }
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }

        internal void Rename(SafeFileHandle file, string path, bool replace)
        {
            var name = global::System.Text.Encoding.Unicode.GetBytes(Path.GetFileName(path));
            var offset = IntPtr.Size == 8 ? 20 : 12;
            var info = new byte[offset + name.Length + 2];
            info[0] = replace ? (byte)3 : (byte)0; // Replace + POSIX semantics retain existing readers that share deletion.
            if (IntPtr.Size == 8) BitConverter.GetBytes(handles[^1].DangerousGetHandle().ToInt64()).CopyTo(info, 8);
            else BitConverter.GetBytes(handles[^1].DangerousGetHandle().ToInt32()).CopyTo(info, 4);
            BitConverter.GetBytes(name.Length).CopyTo(info, offset - 4); name.CopyTo(info, offset);
            var status = NtSetInformationFile(file, out _, info, (uint)info.Length, 65);
            if (status < 0) throw StorageSafety.Error(path, new Win32Exception((int)RtlNtStatusToDosError(status)));
        }
    }

    internal static FileStream OpenSource(string path, bool allowDelete = false)
    {
        NativeStores.RegularPath(path);
        var handle = CreateFile(path, 0x80000000, FileShare.Read | (allowDelete ? FileShare.Delete : 0), IntPtr.Zero, FileMode.Open, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error()); handle.Dispose(); throw StorageSafety.Error(path, error);
        }
        try
        {
            RequireRegular(handle, directory: false);
            var stream = new FileStream(handle, FileAccess.Read);
            try { StorageSafety.Require(stream.GetAccessControl(), path); return stream; }
            catch { stream.Dispose(); throw; }
        }
        catch { handle.Dispose(); throw; }
    }

    internal static SetupPayload Extract(string path, Stream executable, Func<string, Stream> resource, DirectorySecurity acl)
    {
        using var parent = new PinnedDirectories(Path.GetDirectoryName(path)!, requireTrusted: false);
        CreateDirectory(path, acl);
        var payload = new SetupPayload(path);
        try
        {
            payload.directories = new PinnedDirectories(path, requireTrusted: false);
            var actual = new DirectoryInfo(path).GetAccessControl();
            static byte[] Dacl(FileSystemSecurity security)
            {
                var dacl = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl!;
                var bytes = new byte[dacl.BinaryLength]; dacl.GetBinaryForm(bytes, 0); return bytes;
            }
            if (!actual.AreAccessRulesProtected || !Equals(actual.GetOwner(typeof(SecurityIdentifier)), acl.GetOwner(typeof(SecurityIdentifier))) ||
                !Dacl(actual).AsSpan().SequenceEqual(Dacl(acl)))
                throw new IOException("Protected installer staging permissions were not applied.");
            void Write(string name, Stream input)
            {
                var target = Path.Combine(path, name);
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                payload.files.Add(OpenSource(target));
            }
            Write("HytaleAccounts.exe", executable);
            foreach (var name in Scripts) { using var input = resource(name); Write(name, input); }
            return payload;
        }
        catch { payload.Dispose(); throw; }
    }

    internal static void CreateDirectory(string path, DirectorySecurity acl)
    {
        // CreateDirectoryW fails on existing paths and installs the DACL at creation, before any bytes are written.
        var descriptor = acl.GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pin.AddrOfPinnedObject() };
            if (!CreateDirectory(path, ref attributes)) throw StorageSafety.Error(path, new Win32Exception(Marshal.GetLastWin32Error()));
        }
        finally { pin.Free(); }
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in files) file.Dispose();
            foreach (var name in Scripts.Concat(["HytaleAccounts.exe", "install-error.txt"]))
            {
                var path = Path.Combine(DirectoryPath, name);
                NativeStores.RegularPath(path);
                File.Delete(path);
            }
        }
        finally { directories?.Dispose(); directories = null; }
        Directory.Delete(DirectoryPath);
    }

    private static void RequireRegular(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || (!directory && info.Links != 1))
            throw new IOException("Installer path contains a link; files preserved.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public IntPtr Status, Information; }
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatus status, byte[] information, uint size, int kind);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
