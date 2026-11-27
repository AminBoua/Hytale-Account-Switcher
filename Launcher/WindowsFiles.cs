using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HytaleAccounts;

internal static class WindowsFiles
{
    internal static void CheckPath(string path) => _ = PathAttributes(path);

    internal static bool DirectoryExists(string path) =>
        (PathAttributes(path) & FileAttributes.Directory) == FileAttributes.Directory;

    internal static bool FileExists(string path) =>
        PathAttributes(path) is { } attributes && (attributes & FileAttributes.Directory) == 0;

    private static FileAttributes? PathAttributes(string path)
    {
        var components = new Stack<string>();
        for (string? current = LocalPath(path); current is not null; current = Path.GetDirectoryName(current))
            components.Push(current);
        FileAttributes? attributes = null;
        foreach (var component in components)
        {
            attributes = Attributes(component);
            if (attributes is null) return null;
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                throw new IOException($"Path '{component}' contains a link. Files were preserved.");
        }
        return attributes;
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Error(path, ex); }
    }

    private static string LocalPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length < 3 || full[1] != ':' || full.IndexOf(':', 2) >= 0)
            throw new IOException("Only local Windows paths are supported.");
        if (full[3..].Split('\\').Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new IOException("A path component ends in a dot or space. Files were preserved.");
        return full;
    }

    internal sealed class DirectoryPin : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        internal string PathName { get; }
        internal SafeFileHandle Leaf => handles[^1];
        internal bool Machine { get; }

        internal DirectoryPin(string path, bool machine = false, bool forMutation = false)
        {
            PathName = LocalPath(path);
            Machine = machine;
            var ancestors = new Stack<string>();
            for (string? current = PathName; current is not null; current = Path.GetDirectoryName(current))
                ancestors.Push(current);

            try
            {
                foreach (var current in ancestors)
                {
                    var share = FileShare.Read;
                    SafeFileHandle OpenDirectory(FileShare mode) => handles.Count == 0
                        ? OpenRoot(current, mode)
                        : OpenRelative(Leaf, current, directory: true, 0x120081, mode)!;
                    using var validated = OpenDirectory(share);
                    RequireRegular(validated, current, directory: true);
                    WindowsPermissions.Require(WindowsPermissions.Read(validated, directory: true), current,
                        administratorsOnly: machine, ancestor: !current.Equals(PathName, StringComparison.OrdinalIgnoreCase));
                    // Deny directory writes during inspection. Rename requires write sharing afterward.
                    var held = OpenDirectory(forMutation ? FileShare.ReadWrite : share);
                    handles.Add(held);
                }
            }
            catch { Dispose(); throw; }
        }

        internal void Rename(SafeFileHandle file, string path, bool replace)
        {
            if (!string.Equals(Path.GetDirectoryName(path), PathName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Replacement needs a staged sibling.");
            var name = Encoding.Unicode.GetBytes(Path.GetFileName(path));
            var offset = IntPtr.Size == 8 ? 20 : 12;
            var info = new byte[offset + name.Length + 2];
            info[0] = replace ? (byte)3 : (byte)0;
            if (IntPtr.Size == 8) BitConverter.GetBytes(Leaf.DangerousGetHandle().ToInt64()).CopyTo(info, 8);
            else BitConverter.GetBytes(Leaf.DangerousGetHandle().ToInt32()).CopyTo(info, 4);
            BitConverter.GetBytes(name.Length).CopyTo(info, offset - 4);
            name.CopyTo(info, offset);
            var status = NtSetInformationFile(file, out _, info, (uint)info.Length, 65);
            if (status < 0) throw Error(path, new Win32Exception((int)RtlNtStatusToDosError(status)));
        }

        public void Dispose()
        {
            foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose();
        }
    }

    internal static FileStream Open(string path, bool allowDelete = false)
    {
        path = LocalPath(path);
        using var parent = new DirectoryPin(Path.GetDirectoryName(path)!);
        return OpenStream(parent, path, share: FileShare.Read | (allowDelete ? FileShare.Delete : 0))
            ?? throw new FileNotFoundException("File does not exist.", path);
    }

    internal static byte[] Read(string path)
    {
        using var file = Open(path);
        return Read(file);
    }

    private static byte[] Read(FileStream file)
    {
        file.Position = 0;
        using var bytes = new MemoryStream();
        file.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static FileStream? OpenStream(DirectoryPin parent, string path, bool create = false, bool write = false,
        bool delete = false, FileShare share = FileShare.None, FileSecurity? security = null, bool allowExisting = false,
        bool allowInheritedShortcutDelete = false, bool administratorsOnly = false)
    {
        var access = 0x80100000u;
        if (write) access |= 0x40000000;
        if (delete) access |= 0x10000;
        if (security is not null) access |= 0x40000;
        var handle = OpenRelative(parent.Leaf, path, directory: false, access, share, create, security, allowExisting);
        if (handle is null) return null;
        try
        {
            RequireRegular(handle, path, directory: false);
            WindowsPermissions.Require(WindowsPermissions.Read(handle, directory: false), path,
                administratorsOnly: administratorsOnly, allowInheritedShortcutDelete: allowInheritedShortcutDelete);
            return new FileStream(handle, write ? FileAccess.ReadWrite : FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void Write(string path, byte[] bytes, bool privateFile = false) =>
        Write(path, bytes, privateFile, restorePermissions: null);

    internal static void WriteMachineShortcutBackup(string path, byte[] bytes) =>
        Write(path, bytes, privateFile: false, restorePermissions: null, machineShortcutBackup: true);

    internal static void Write(string path, byte[] bytes, bool privateFile, FileSecurity? restorePermissions) =>
        Write(path, bytes, privateFile, restorePermissions, machineShortcutBackup: false);

    private static void Write(string path, byte[] bytes, bool privateFile, FileSecurity? restorePermissions, bool machineShortcutBackup)
    {
        path = LocalPath(path);
        using var parent = new DirectoryPin(Path.GetDirectoryName(path)!, machine: machineShortcutBackup, forMutation: true);
        using var destination = OpenStream(parent, path, share: FileShare.Read | FileShare.Delete);
        if (machineShortcutBackup && destination is not null)
            throw new IOException($"The machine shortcut backup '{path}' already exists.");
        var staged = Path.Combine(parent.PathName, Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            var privateAcl = machineShortcutBackup ? WindowsPermissions.MachineShortcutBackupAcl() : WindowsPermissions.PrivateAcl<FileSecurity>();
            using (var output = OpenStream(parent, staged, create: true, write: true, delete: true, security: privateAcl)!)
            {
                created = true;
                var actual = WindowsPermissions.Read(output.SafeFileHandle, directory: false);
                WindowsPermissions.RequirePrivate(actual, privateAcl);
                if (machineShortcutBackup && !Equals(actual.GetOwner(typeof(SecurityIdentifier)), privateAcl.GetOwner(typeof(SecurityIdentifier))))
                    throw new IOException("Windows did not set the machine shortcut backup owner.");
                output.Write(bytes);
                output.Flush(true);
                if (privateFile || destination is null)
                {
                    if (!privateFile && restorePermissions is not null)
                    {
                        var acl = new FileSecurity();
                        acl.SetSecurityDescriptorBinaryForm(restorePermissions.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                        output.SetAccessControl(acl);
                    }
                    parent.Rename(output.SafeFileHandle, path, replace: destination is not null);
                    created = false;
                    return;
                }
            }

            // ReplaceFile preserves the native session's DACL.
            try { File.Replace(staged, path, null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Error(path, ex); }
            created = false;
        }
        finally
        {
            if (created)
            {
                try { Delete(staged); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.TraceWarning(ex.Message); }
            }
        }
    }

    internal static void Delete(string path)
    {
        path = LocalPath(path);
        if (!DirectoryExists(Path.GetDirectoryName(path)!)) return;
        using var parent = new DirectoryPin(Path.GetDirectoryName(path)!);
        using var file = OpenStream(parent, path, delete: true, share: FileShare.Read | FileShare.Delete);
        if (file is not null) MarkDeleted(file.SafeFileHandle, path);
    }

    internal static void DeleteEmptyDirectory(string path)
    {
        path = LocalPath(path);
        if (!DirectoryExists(path)) return;
        using var parent = new DirectoryPin(Path.GetDirectoryName(path)!);
        using var directory = OpenRelative(parent.Leaf, path, directory: true, 0x130081, FileShare.Read)!;
        RequireRegular(directory, path, directory: true);
        WindowsPermissions.Require(WindowsPermissions.Read(directory, directory: true), path);
        if (!Directory.EnumerateFileSystemEntries(path).Any()) MarkDeleted(directory, path);
    }

    private static void MarkDeleted(SafeFileHandle file, string path, bool marked = true)
    {
        int delete = marked ? 1 : 0;
        if (!SetFileInformationByHandle(file, 4, ref delete, 4)) throw Error(path, new Win32Exception(Marshal.GetLastWin32Error()));
    }

    internal static OwnedFile OpenOwned(string path, bool create = false, bool readOnly = false) => new(path, create, readOnly);

    // Hold the file and its checked parent while setup edits the shortcut.
    internal static OwnedFile OpenMachineShortcut(string path) => new(path, create: false, readOnly: false, machineShortcut: true);

    internal static FileStream? OpenPayloadFile(DirectoryPin parent, string name) =>
        OpenStream(parent, Sibling(parent, name), share: FileShare.Read, administratorsOnly: true);

    internal static SafeFileHandle? OpenUpgradeArtifact(DirectoryPin parent, string name)
    {
        var path = Sibling(parent, name);
        const uint DeleteReadControlAndAttributes = 0x30080;
        // The old setup can leave files with no access rules. Omit SYNCHRONIZE.
        var handle = OpenRelative(parent.Leaf, path, directory: false, DeleteReadControlAndAttributes,
            FileShare.None, synchronous: false);
        if (handle is null) return null;
        try
        {
            RequireRegular(handle, path, directory: false);
            WindowsPermissions.RequireMachine(WindowsPermissions.Read(handle, directory: false), path);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void DeleteUpgradeArtifact(DirectoryPin parent, string name, SafeFileHandle handle) =>
        MarkDeleted(handle, Sibling(parent, name));

    private static string Sibling(DirectoryPin parent, string name)
    {
        if (!parent.Machine || name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny([':', '/', '\\']) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' ')) throw new IOException("An addon file requires a protected parent and a valid name.");
        return Path.Combine(parent.PathName, name);
    }

    internal sealed class OwnedFile : IDisposable
    {
        private readonly string path;
        private readonly DirectoryPin parent;
        private readonly bool readOnly;
        private FileStream? file;
        internal bool Exists => file is not null;
        internal string Identity
        {
            get
            {
                var info = Information(Stream.SafeFileHandle, path);
                return $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
            }
        }
        private FileStream Stream => file ?? throw new FileNotFoundException("File does not exist.", path);

        internal OwnedFile(string path, bool create, bool readOnly, bool machineShortcut = false)
        {
            if (create && readOnly) throw new ArgumentException("Read-only opens cannot create files.");
            this.path = LocalPath(path);
            this.readOnly = readOnly;
            parent = new DirectoryPin(Path.GetDirectoryName(this.path)!, forMutation: !readOnly);
            try
            {
                if (machineShortcut)
                    file = OpenStream(parent, this.path, write: true, delete: true, allowInheritedShortcutDelete: true);
                else Reopen(create);
            }
            catch { parent.Dispose(); throw; }
        }

        private void Reopen(bool create = false) => file = OpenStream(parent, path, create, !readOnly,
            delete: !readOnly, share: readOnly ? FileShare.Read : FileShare.None, allowExisting: true);

        internal byte[] Read() => WindowsFiles.Read(Stream);

        internal void Write(byte[] bytes)
        {
            Stream.Position = 0;
            Stream.Write(bytes);
            Stream.SetLength(bytes.Length);
            Stream.Flush(true);
        }

        internal void Delete()
        {
            SetDelete(marked: true);
            Close();
        }

        internal void SetDelete(bool marked) => MarkDeleted(Stream.SafeFileHandle, path, marked);

        internal void ReplaceFrom(OwnedFile staged)
        {
            if (readOnly || staged.readOnly || !staged.Exists || path.Equals(staged.path, StringComparison.OrdinalIgnoreCase) ||
                !parent.PathName.Equals(staged.parent.PathName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Replacement needs a writable staged sibling.");

            bool existed = Exists;
            staged.Stream.Flush(true);
            if (!existed)
            {
                parent.Rename(staged.Stream.SafeFileHandle, path, replace: false);
                staged.Close();
                Reopen();
                return;
            }

            // Hold the trusted parent while ReplaceFile applies the existing destination DACL.
            Close();
            staged.Close();
            try { File.Replace(staged.path, path, null); }
            finally
            {
                try { Reopen(); }
                finally { staged.Reopen(); }
            }
        }

        private void Close()
        {
            file?.Dispose();
            file = null;
        }

        public void Dispose()
        {
            Close();
            parent.Dispose();
        }
    }

    internal static SafeFileHandle OpenForMigration(DirectoryPin parent, string path, bool directory)
    {
        var handle = OpenRelative(parent.Leaf, path, directory, 0x160081, FileShare.Read)
            ?? throw new FileNotFoundException("File does not exist.", path);
        try { RequireRegular(handle, path, directory); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenRoot(string path, FileShare share)
    {
        var handle = CreateFile(path, 0x20081, share, IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw Error(path, new Win32Exception(error));
    }

    private static SafeFileHandle? OpenRelative(SafeFileHandle parent, string path, bool directory, uint access, FileShare share,
        bool create = false, FileSystemSecurity? security = null, bool allowExisting = false, bool synchronous = true)
    {
        var name = Path.GetFileName(path);
        var buffer = Marshal.StringToHGlobalUni(name);
        var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), Buffer = buffer };
        unicode.MaximumLength = unicode.Length;
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var descriptor = security is null ? default : GCHandle.Alloc(security.GetSecurityDescriptorBinaryForm(), GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(unicode, pointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = pointer, Attributes = 0x40,
                Security = descriptor.IsAllocated ? descriptor.AddrOfPinnedObject() : IntPtr.Zero
            };
            var result = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero, 0x80, (uint)share,
                create ? (allowExisting ? 3u : 2u) : 1u, 0x00200000u | (synchronous ? 0x20u : 0) | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
            if (result >= 0) return handle;
            handle.Dispose();
            var error = (int)RtlNtStatusToDosError(result);
            if (!directory && !create && error == 2) return null;
            throw Error(path, new Win32Exception(error));
        }
        finally
        {
            if (descriptor.IsAllocated) descriptor.Free();
            Marshal.FreeHGlobal(pointer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static FileInformation Information(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Error(path, new Win32Exception(Marshal.GetLastWin32Error()));
        return info;
    }

    private static void RequireRegular(SafeFileHandle handle, string path, bool directory)
    {
        var info = Information(handle, path);
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1))
            throw new IOException($"Path '{path}' contains a link or unexpected file type. Files were preserved.");
    }

    internal static IOException Error(string path, Exception exception)
    {
        if (exception is PathException previous) return previous;
        var code = exception is Win32Exception native ? native.NativeErrorCode : exception.HResult & 0xffff;
        return new PathException(code is 32 or 33
            ? $"Cannot lock '{path}' because another program has a conflicting handle. Close the program using this path, then retry. Files were preserved."
            : $"Cannot safely access '{path}' (Windows error {code}). Operation stopped.", exception);
    }

    private sealed class PathException(string message, Exception inner) : IOException(message, inner);
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, ref int info, uint size);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status,
        IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatus status, byte[] information, uint size, int kind);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
