using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Shared with Windows PowerShell 5.1; keep this source compatible with its C# compiler.
public sealed class ShortcutFile : IDisposable
{
    private readonly List<SafeFileHandle> directories = new List<SafeFileHandle>();
    private FileStream stream;
    public bool Exists { get { return stream != null; } }
    public string Identity
    {
        get
        {
            FileInformation info;
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info.Volume.ToString("X8") + ":" + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8");
        }
    }
    public ShortcutFile(string path, bool create)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (path.StartsWith(@"\\") || path.IndexOf(':', 2) >= 0) throw new IOException("Only local shortcut paths are supported.");
            var root = Path.GetPathRoot(path);
            var parent = Open(root, 0x81, FileShare.Read | FileShare.Write, 3, 0x02200000);
            directories.Add(parent);
            RequireRegular(parent, true);
            var parts = path.Substring(root.Length).Split('\\');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                // Open each component relative to the held parent, never traverse it again by pathname.
                parent = OpenRelative(parent, parts[i], true, false);
                directories.Add(parent);
                RequireRegular(parent, true);
            }
            var file = OpenRelative(parent, parts[parts.Length - 1], false, create);
            if (file == null) return;
            try { RequireRegular(file, false); stream = new FileStream(file, FileAccess.ReadWrite); }
            catch { file.Dispose(); throw; }
        }
        catch { Dispose(); throw; }
    }
    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory, bool create)
    {
        var buffer = Marshal.StringToHGlobalUni(name);
        var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2)), Buffer = buffer };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
        try
        {
            Marshal.StructureToPtr(unicode, pointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf(typeof(ObjectAttributes)), Root = parent.DangerousGetHandle(), Name = pointer, Attributes = 0x40 };
            SafeFileHandle handle; IoStatus status;
            // Relative opens remain bound to the actual parent even if it later acquires reparse data.
            int result = NtCreateFile(out handle, directory ? 0x100081u : 0xC0110000u, ref attributes, out status,
                IntPtr.Zero, 0x80, directory ? 3u : 0u, create ? 2u : 1u, 0x00200020u | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
            if (result >= 0) return handle;
            handle.Dispose();
            uint error = RtlNtStatusToDosError(result);
            if (!directory && !create && error == 2) return null;
            throw new Win32Exception((int)error);
        }
        finally { Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public IntPtr Status, Information; }
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatus status,
        IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    private static SafeFileHandle Open(string path, uint access, FileShare share, uint mode, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, mode, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error);
    }
    private static void RequireRegular(SafeFileHandle handle, bool directory)
    {
        FileInformation info;
        if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1))
            throw new IOException("Shortcut path contains a link or unexpected file type; files preserved.");
    }
    public byte[] Read()
    {
        stream.Position = 0;
        using (var copy = new MemoryStream()) { stream.CopyTo(copy); return copy.ToArray(); }
    }
    public static bool IsReadableShortcut(string path)
    {
        var link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
        try { ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0); return true; }
        catch (COMException error)
        {
            // WScript hides this invalid-format result behind an empty TargetPath.
            if (error.ErrorCode != unchecked((int)0x80004005)) throw;
            return false;
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }
    public void Write(byte[] bytes)
    {
        stream.Position = 0; stream.Write(bytes, 0, bytes.Length); stream.SetLength(bytes.Length); stream.Flush(true);
    }
    public void Delete()
    {
        int delete = 1;
        if (!SetFileInformationByHandle(stream.SafeFileHandle, 4, ref delete, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose()
    {
        if (stream != null) stream.Dispose();
        for (int i = directories.Count - 1; i >= 0; i--) directories[i].Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, ref int info, uint size);
}
