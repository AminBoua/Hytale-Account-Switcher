using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HytaleAccounts;
using Microsoft.Win32.SafeHandles;

// Compiled only by AclMigrationRaceChecks.ps1 against its instrumented temporary source copy.
internal static class AclMigrationRaceChecks
{
    private sealed class ProbeComplete : Exception { }
    private static void Check(bool result, string message)
    { if (!result) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); }
    private static string Dacl(string path) => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
        : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    public static void Main(string[] args)
    {
        var root = SecurityFixture.Create("acl-races-");
        void Cleanup(string path)
        {
            var full = Path.GetFullPath(path);
            if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Cleanup escaped ACL race fixture.");
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.Directory) == 0) { File.Delete(full); return; }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(full)) Cleanup(child);
            Directory.Delete(full);
        }
        try
        {
            var tree = Path.Combine(root, "tree"); Directory.CreateDirectory(tree);
            var empty = Path.Combine(tree, "empty"); Directory.CreateDirectory(empty);
            var leaf = Path.Combine(tree, "leaf.bin"); File.WriteAllText(leaf, "inspected bytes");
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            var victim = Path.Combine(outside, "victim.bin"); File.WriteAllText(victim, "untouched sentinel");
            var victimAcl = Dacl(victim); var outsideAcl = Dacl(outside);
            void VictimUnchanged() => Check(Dacl(victim) == victimAcl && Dacl(outside) == outsideAcl &&
                File.ReadAllText(victim) == "untouched sentinel", "outside sentinel bytes and ACLs unchanged");
            void SharingDenied(Action action, string message)
            {
                try { action(); }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 32) { Check(true, message); return; }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 32) { Check(true, message); return; }
                throw new Exception("FAIL " + message + " (expected Windows sharing violation)");
            }
            // Controls establish that ordinary fixture replacement is possible without migration leases.
            Directory.Move(tree, tree + "-moved"); Directory.Move(tree + "-moved", tree);
            File.Move(leaf, leaf + ".moved"); File.Move(leaf + ".moved", leaf);
            var reached = false;
            PrivateFiles.MigrationCheckpoint = (phase, _) =>
            {
                if (phase != "inspected") return;
                reached = true;
                SharingDenied(() => Directory.Move(tree, tree + "-moved"), "migration pins root against directory replacement");
                SharingDenied(() => Directory.Move(empty, empty + "-moved"), "migration pins empty descendant against replacement");
                SharingDenied(() => File.Move(leaf, leaf + ".moved"), "migration pins inspected leaf against replacement");
                SharingDenied(() => File.WriteAllText(leaf, "attacker bytes"), "migration denies writing the inspected leaf");
                SharingDenied(() => Junction(empty, outside), "migration blocks in-place junction conversion of empty descendant");
                throw new ProbeComplete();
            };
            try { PrivateFiles.SecureTree(tree); } catch (ProbeComplete) { }
            finally { PrivateFiles.MigrationCheckpoint = null; }
            Check(reached && File.ReadAllText(leaf) == "inspected bytes", "deterministic post-inspection race boundary exercised");
            VictimUnchanged();
            if (args.Contains("--pins-only")) return;

            // A junction planted before the pin is acquired must be rejected, not followed.
            reached = false;
            PrivateFiles.MigrationCheckpoint = (phase, path) =>
            {
                if (phase != "before-pin" || path != empty) return;
                Junction(empty, outside); reached = true;
            };
            var rejected = false;
            try { PrivateFiles.SecureTree(tree); } catch (IOException) { rejected = true; }
            finally { PrivateFiles.MigrationCheckpoint = null; }
            Check(reached && rejected, "pre-acquisition junction conversion stops migration");
            VictimUnchanged(); Directory.Delete(empty); File.Delete(leaf);

            foreach (var phase in new[] { "inspected", "directory-acl" })
            foreach (var kind in new[] { "protected-file", "inherited-file", "nested-file", "hard-link", "junction" })
            {
                var migration = Path.Combine(root, phase + "-" + kind); Directory.CreateDirectory(migration);
                var permissive = new DirectoryInfo(migration).GetAccessControl();
                permissive.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(migration).SetAccessControl(permissive);
                var inserted = Path.Combine(migration, "inserted");
                string? insertedAcl = null;
                if (kind == "hard-link")
                {
                    if (!CreateHardLink(inserted, victim, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    File.Delete(inserted);
                }
                var blocked = false;
                reached = false; rejected = false;
                PrivateFiles.MigrationCheckpoint = (point, path) =>
                {
                    if (reached || point != phase || path != migration) return;
                    if (kind == "hard-link")
                    {
                        if (!CreateHardLink(inserted, victim, IntPtr.Zero))
                        {
                            var error = Marshal.GetLastWin32Error();
                            if (error != 32) throw new Win32Exception(error);
                            blocked = true; return;
                        }
                    }
                    else if (kind == "junction") { Directory.CreateDirectory(inserted); Junction(inserted, outside); }
                    else
                    {
                        var file = inserted;
                        if (kind == "nested-file") { Directory.CreateDirectory(inserted); file = Path.Combine(inserted, "late.bin"); }
                        File.WriteAllText(file, "late synthetic bytes");
                        if (kind != "inherited-file")
                        {
                            var broad = new FileInfo(file).GetAccessControl();
                            broad.SetAccessRuleProtection(true, true); new FileInfo(file).SetAccessControl(broad);
                        }
                    }
                    if (kind is not ("hard-link" or "junction")) insertedAcl = Dacl(inserted);
                    reached = true;
                };
                try { PrivateFiles.SecureTree(migration); } catch (IOException) { rejected = true; }
                finally { PrivateFiles.MigrationCheckpoint = null; }
                Check(blocked || reached && rejected, phase + " " + kind +
                    (blocked ? " insertion denied by Windows sharing rules" : " insertion stops initialization"));
                VictimUnchanged();
                if (kind is "protected-file" or "inherited-file" or "nested-file")
                {
                    var file = kind == "nested-file" ? Path.Combine(inserted, "late.bin") : inserted;
                    Check(File.ReadAllText(file) == "late synthetic bytes", "concurrent insertion bytes preserved");
                    Check(Dacl(inserted) == insertedAcl, "uninspected child ACL is never changed by parent propagation");
                }
            }
        }
        finally { PrivateFiles.MigrationCheckpoint = null; Cleanup(root); }
    }

    private static void Junction(string path, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target); var print = Encoding.Unicode.GetBytes(target);
        var data = new byte[20 + substitute.Length + print.Length];
        BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0); BitConverter.GetBytes((ushort)(data.Length - 8)).CopyTo(data, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(data, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(data, 12); BitConverter.GetBytes((ushort)print.Length).CopyTo(data, 14);
        substitute.CopyTo(data, 16); print.CopyTo(data, 18 + substitute.Length);
        using var handle = OpenDirectory(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int length, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr security);
}
