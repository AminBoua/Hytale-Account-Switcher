using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using HytaleAccounts;
using Microsoft.Win32.SafeHandles;

internal static class StorageSafetyChecks
{
    private static readonly SecurityIdentifier User = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier Stranger = new("S-1-5-21-314159265-271828182-161803398-4242");

    internal static void Run(Action<bool, string> check)
    {
        IOException Reject(Action action, string message)
        {
            try { action(); } catch (IOException ex) { check(true, message); return ex; }
            throw new Exception("FAIL " + message);
        }
        void Policy(string rules, bool accepted, string message, bool directory = true, bool ancestor = false,
            string? owner = null, bool administratorsOnly = false)
        {
            FileSystemSecurity acl = directory ? new DirectorySecurity() : new FileSecurity();
            acl.SetSecurityDescriptorSddlForm($"O:{owner ?? User.Value}G:SYD:" + rules);
            if (!accepted) Reject(() => StorageSafety.Require(acl, "synthetic descriptor", administratorsOnly, ancestor), message);
            else { StorageSafety.Require(acl, "synthetic descriptor", administratorsOnly, ancestor); check(true, message); }
        }
        var trusted = $"(A;OICIID;FA;;;{User.Value})(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)";
        Policy(trusted + "(A;OICIID;FRFX;;;BU)", true, "normal inherited profile ACL accepts read-only Users");
        foreach (var sid in new[] { "S-1-15-2-1", "S-1-15-2-2", "S-1-15-3-1024-1-2-3-4-5-6-7-8" })
            Policy(trusted + $"(A;OICI;GA;;;{sid})", true, "application package/capability ACE does not grant an unrelated normal user: " + sid);
        Policy(trusted + "(A;OICIIO;GA;;;CO)", true, "inherit-only Creator Owner does not grant mutation on the parent");
        Policy(trusted + "(A;;GA;;;OW)", true, "Owner Rights applies only to the validated owner");
        Policy(trusted, true, "TrustedInstaller-owned system ancestors are compatible", owner: "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
        Policy(trusted + "(A;;0x6;;;AU)", true, "ancestor sibling creation is compatible", ancestor: true);
        Policy(trusted + "(A;;0x6;;;AU)", false, "storage directory rejects foreign child insertion");
        foreach (var right in new[] { "0x2", "0x4", "0x10", "0x100", "0x40", "SD", "WD", "WO", "GW", "GA" })
            Policy(trusted + $"(A;;{right};;;{Stranger.Value})", false, "foreign mutation rejected: " + right);
        Policy("(D;;GW;;;WD)" + trusted + "(A;;GW;;;BU)", true, "Everyone deny removes effective group write access");
        Policy("(D;;GW;;;BU)" + trusted + "(A;;GW;;;BU)", true, "same-group deny removes effective write access");
        Policy("(D;;GW;;;BU)" + trusted + $"(A;;GW;;;{Stranger.Value})", false, "unrelated group deny cannot mask a foreign user's grant");
        Policy(trusted + "(A;;GW;;;BU)(D;;GW;;;BU)", false, "later deny cannot erase an earlier effective allow");
        Policy(trusted + "(A;;GA;;;S-1-15-3-1)(A;;GW;;;BU)", false, "capability grant does not exempt an unsafe normal-group grant");
        Policy(trusted, false, "foreign ownership rejected even without a mutation ACE", owner: Stranger.Value);
        Policy("NO_ACCESS_CONTROL", false, "null DACL rejected");
        Policy("(A;;GA;;;BA)", true, "administrator-owned staging policy accepted", owner: "BA", administratorsOnly: true);
        Policy(trusted, false, "user-owned staging policy rejected", administratorsOnly: true);
        Policy(trusted + "(A;;0x4;;;BU)", false, "file append is mutation, not ancestor sibling creation", directory: false, ancestor: true);
        Policy(trusted + "(A;;0x40;;;BU)", false, "ancestor delete-child is never exempted", ancestor: true);
        Policy(trusted + "(A;;0x100;;;BU)", false, "ancestor reparse mutation is never exempted", ancestor: true);

        var root = SecurityFixture.Create("storage-safety-");
        void Broad(string path, FileSystemRights rights)
        {
            if (Directory.Exists(path))
            {
                var acl = new DirectoryInfo(path).GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(Stranger, rights, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(acl);
            }
            else
            {
                var acl = new FileInfo(path).GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(Stranger, rights, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(acl);
            }
        }
        string Dacl(string path) => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        const string secret = "synthetic-sensitive-bytes-not-for-errors";
        try
        {
            var native = Path.Combine(root, "native"); Directory.CreateDirectory(native);
            var stores = new NativeStores(native);
            var pending = stores.CreatePending();
            var active = Path.Combine(native, "account.dat"); File.WriteAllText(active, secret);
            Broad(active, FileSystemRights.ReadAndExecute);
            var nativeAcl = Dacl(native); var activeAcl = Dacl(active);
            stores.Capture(pending);
            var snapshot = Path.Combine(stores.Folder(pending.Folder), "AppData", "Hytale", "account.dat");
            Broad(snapshot, FileSystemRights.ReadAndExecute);
            check(Dacl(snapshot).Contains(Stranger.Value), "snapshot fixture starts with a permissive existing ACL");
            stores.Capture(pending);
            check(!Dacl(snapshot).Contains(Stranger.Value) && File.ReadAllText(snapshot) == secret,
                "capture replaces existing permissive snapshot with a private file");
            check(Dacl(native) == nativeAcl && Dacl(active) == activeAcl, "capture preserves native parent and active-session ACLs");
            var keys = Path.Combine(native, ".keys"); Directory.CreateDirectory(keys);
            var key = Path.Combine(keys, "key.bin"); File.WriteAllText(key, secret);
            stores.Capture(pending);
            var copiedKey = Path.Combine(Path.GetDirectoryName(snapshot)!, ".keys", "key.bin");
            Broad(copiedKey, FileSystemRights.ReadAndExecute);
            stores.Capture(pending);
            check(!Dacl(copiedKey).Contains(Stranger.Value), "unchanged copied keys cannot retain permissive snapshot ACLs");
            foreach (var name in new[] { "hytale-launcher.exe", "native-stores.json", "launcher-update.exe" })
            {
                var target = Path.Combine(stores.Root, name); if (!File.Exists(target)) File.WriteAllText(target, "old");
                Broad(target, FileSystemRights.ReadAndExecute);
                NativeStores.AtomicWrite(target, [1, 2, 3], privateFile: true);
                check(!Dacl(target).Contains(Stranger.Value), "private replacement restricts existing " + name);
            }
            stores.Save();
            var hostile = Path.Combine(root, "hostile"); Directory.CreateDirectory(hostile);
            Broad(hostile, FileSystemRights.CreateFiles);
            var hostileAcl = Dacl(hostile);
            Reject(() => _ = new NativeStores(hostile), "untrusted insertion permission rejects storage before initialization");
            Reject(() => PrivateFiles.CreateDirectory(Path.Combine(hostile, "snapshot")), "private creation rejects an unsafe parent");
            Reject(() => NativeStores.AtomicWrite(Path.Combine(hostile, "secret.bin"), [1]), "atomic write rejects an unsafe parent before writing bytes");
            check(Dacl(hostile) == hostileAcl && !Directory.EnumerateFileSystemEntries(hostile).Any(), "unsafe parent is not repaired or populated");
            var writable = Path.Combine(root, "writable.bin"); File.WriteAllText(writable, secret);
            Broad(writable, FileSystemRights.WriteData);
            var writableAcl = Dacl(writable);
            Reject(() => NativeStores.ReadFile(writable), "reading a foreign-writable source fails closed");
            Reject(() => NativeStores.NewerBuild(writable, active), "version inspection rejects a foreign-writable cached executable");
            check(!NativeStores.NewerBuild(active, active), "versionless synthetic files compare safely under held handles");
            Reject(() => NativeStores.AtomicWrite(writable, [9]), "replacing a foreign-writable destination fails closed");
            check(File.ReadAllText(writable) == secret && Dacl(writable) == writableAcl, "rejected file bytes and ACL preserved");
            var migration = Path.Combine(root, "migration"); Directory.CreateDirectory(migration);
            var poisoned = Path.Combine(migration, "inserted.bin"); File.WriteAllText(poisoned, secret);
            Broad(poisoned, FileSystemRights.ChangePermissions);
            var migrationAcl = Dacl(migration); var poisonedAcl = Dacl(poisoned);
            Reject(() => PrivateFiles.SecureTree(migration), "migration rejects foreign-mutable children before ACL repair");
            check(Dacl(migration) == migrationAcl && Dacl(poisoned) == poisonedAcl, "failed inspection changes no migration ACLs");
            using (var writer = OpenDirectory(native, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (writer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var error = Reject(() => stores.Capture(pending), "capture refuses a conflicting directory writer");
                check(error.Message.Contains(native) && error.Message.Contains("Close the program") && error.Message.Contains("retry") &&
                    !error.Message.Contains(secret), "directory conflict identifies blocked path and safe retry without contents");
            }
            stores.Capture(pending);
            check(File.ReadAllText(snapshot) == secret, "capture succeeds after directory writer closes");
            using (var writer = File.Open(active, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                var error = Reject(() => NativeStores.ReadFile(active), "source read refuses a conflicting leaf writer");
                check(error.Message.Contains(active) && error.Message.Contains("retry") && !error.Message.Contains(secret),
                    "leaf conflict identifies blocked path without content");
            }
            using (var reader = File.Open(active, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Reject(() => NativeStores.AtomicWrite(active, [9]), "atomic replace refuses a conflicting delete share");
                check(error.Message.Contains(active) && error.Message.Contains("retry"), "atomic replacement reports its blocked destination");
            }
            NativeStores.AtomicWrite(active, System.Text.Encoding.UTF8.GetBytes(secret));
            check(Dacl(active) == activeAcl && File.ReadAllText(active) == secret, "retry preserves Hytale ACL and bytes");
            var link = Path.Combine(root, "hard-link.bin");
            if (!CreateHardLink(link, active, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Reject(() => NativeStores.AtomicWrite(link, [9], privateFile: true), "atomic private replacement rejects hard-link destinations");
            check(File.ReadAllText(active) == secret && Dacl(active) == activeAcl, "hard-link victim preserved");
            File.Delete(link);
        }
        finally
        {
            // The test creates no reparse points and deletes only its independently generated fixture root.
            if (!Path.GetFileName(root).StartsWith("storage-safety-", StringComparison.Ordinal)) throw new IOException("Invalid fixture cleanup root.");
            Directory.Delete(root, recursive: true);
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr security);
}
