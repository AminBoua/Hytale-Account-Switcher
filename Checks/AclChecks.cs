using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HytaleAccounts;

internal static class AclChecks
{
    private static readonly SecurityIdentifier User = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static string Dacl(string path) => ((File.GetAttributes(path) & FileAttributes.Directory) != 0
        ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl())
        .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    private static bool IsPrivate(FileSystemSecurity acl)
    {
        var allowed = new[] { User.Value, "S-1-5-18", "S-1-5-32-544" };
        var inheritance = acl is DirectorySecurity ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        return acl.AreAccessRulesProtected && rules.Length == allowed.Length &&
            rules.All(r => !r.IsInherited && r.AccessControlType == AccessControlType.Allow &&
                r.FileSystemRights == FileSystemRights.FullControl && r.InheritanceFlags == inheritance &&
                r.PropagationFlags == PropagationFlags.None && allowed.Contains(r.IdentityReference.Value)) &&
            allowed.All(sid => rules.Any(r => r.IdentityReference.Value == sid));
    }

    private static bool IsPrivate(string path) => IsPrivate((File.GetAttributes(path) & FileAttributes.Directory) != 0
        ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl());

    private static void MakeBroad(string path)
    {
        var directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        FileSystemSecurity acl = directory ? new DirectorySecurity() : new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        var flags = directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        acl.AddAccessRule(new FileSystemAccessRule(User, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        foreach (var sid in new[] { Everyone, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, flags, PropagationFlags.None, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)acl);
        else new FileInfo(path).SetAccessControl((FileSecurity)acl);
    }

    public static void Run(Action<bool, string> check)
    {
        NativeStores.RequireIdle();
        var root = SecurityFixture.Create("hytale-acl-check-");
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { check(true, message); return; }
            throw new Exception(message);
        }
        void Cleanup(string path)
        {
            var full = Path.GetFullPath(path);
            if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("ACL fixture cleanup escaped its root.");
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.Directory) == 0) { File.Delete(full); return; }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(full)) Cleanup(child);
            Directory.Delete(full);
        }
        try
        {
            MakeBroad(root);
            var parentAcl = Dacl(root);
            var storage = Path.Combine(root, "Accounts", "old", "AppData", "Hytale");
            var runtime = Path.Combine(root, "Accounts", "pending-abandoned", "run-old");
            Directory.CreateDirectory(Path.Combine(storage, ".keys"));
            Directory.CreateDirectory(runtime);
            var oldFiles = new[] { Path.Combine(storage, "account.dat"), Path.Combine(storage, ".keys", "key"),
                Path.Combine(runtime, "hytale-launcher.exe"), Path.Combine(runtime, "interrupted.tmp"),
                Path.Combine(root, "Accounts", "launcher-update.exe") };
            foreach (var file in oldFiles) { File.WriteAllText(file, "synthetic opaque bytes"); MakeBroad(file); }
            MakeBroad(runtime); // An explicitly protected permissive descendant must also be repaired.
            var nativeKeys = Path.Combine(root, ".keys"); Directory.CreateDirectory(nativeKeys);
            var nativeKey = Path.Combine(nativeKeys, "native-key"); File.WriteAllText(nativeKey, "synthetic native key");
            var nativeKeyAcl = Dacl(nativeKey); var nativeDirectoryAcl = Dacl(nativeKeys);
            var unrelated = Path.Combine(root, "world.dat"); File.WriteAllText(unrelated, "unrelated fixture");
            var unrelatedAcl = Dacl(unrelated);
            EffectiveAccessChecks.Check(runtime, true, check);
            EffectiveAccessChecks.Check(oldFiles[0], true, check);
            var stores = new NativeStores(root);
            EffectiveAccessChecks.Check(runtime, false, check);
            EffectiveAccessChecks.Check(oldFiles[0], false, check);
            check(oldFiles.All(f => IsPrivate(f) && File.ReadAllText(f) == "synthetic opaque bytes") &&
                Directory.EnumerateDirectories(stores.Root, "*", SearchOption.AllDirectories).All(IsPrivate) && IsPrivate(stores.Root),
                "migration restricts existing snapshots, keys, pending files and runtime/update images without changing bytes");
            check(Dacl(root) == parentAcl && Dacl(nativeKey) == nativeKeyAcl && Dacl(nativeKeys) == nativeDirectoryAcl &&
                Dacl(unrelated) == unrelatedAcl && File.ReadAllText(unrelated) == "unrelated fixture",
                "migration leaves native keys, parent and unrelated data ACLs unchanged");
            var before = Dacl(oldFiles[0]); _ = new NativeStores(root);
            check(Dacl(oldFiles[0]) == before, "ACL migration is idempotent");

            var pending = stores.CreatePending();
            var active = Path.Combine(root, "account.dat"); File.WriteAllText(active, "synthetic active session");
            stores.Capture(pending);
            var captured = Path.Combine(stores.Folder(pending.Folder), "AppData", "Hytale");
            check(IsPrivate(stores.Folder(pending.Folder)) && IsPrivate(Path.Combine(captured, "account.dat")) &&
                IsPrivate(Path.Combine(captured, ".keys", "native-key")) && IsPrivate(Path.Combine(stores.Root, "native-stores.json")),
                "new pending accounts, snapshots, copied keys and metadata are private");
            check(File.ReadAllText(Path.Combine(captured, "account.dat")) == "synthetic active session" &&
                File.ReadAllText(Path.Combine(captured, ".keys", "native-key")) == "synthetic native key",
                "capture retains opaque native session and key bytes");
            var run = Path.Combine(stores.Folder(pending.Folder), "run-fixture"); PrivateFiles.CreateDirectory(run);
            var image = Path.Combine(run, "hytale-launcher.exe"); NativeStores.AtomicWrite(image, Encoding.UTF8.GetBytes("synthetic runtime capability"));
            check(IsPrivate(run) && IsPrivate(image), "generated runtime directory and executable are private");

            var temporary = Path.Combine(root, "creation.tmp");
            using (var file = PrivateFiles.CreateFile(temporary))
            {
                check(IsPrivate(file.GetAccessControl()), "temporary file has private ACL before any bytes are written under a permissive parent");
                file.Write(Encoding.UTF8.GetBytes("synthetic secret")); file.Flush(true);
            }
            File.Delete(temporary);
            var fresh = Path.Combine(root, "fresh.dat"); NativeStores.AtomicWrite(fresh, [1, 2, 3]);
            check(IsPrivate(fresh) && Dacl(root) == parentAcl, "first atomic write is private without restricting the native parent directory");

            var strict = new FileSecurity(); strict.SetAccessRuleProtection(true, false);
            strict.AddAccessRule(new FileSystemAccessRule(User, FileSystemRights.FullControl, AccessControlType.Allow));
            strict.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            new FileInfo(active).SetAccessControl(strict);
            var activeAcl = Dacl(active);
            NativeStores.AtomicWrite(active, Encoding.UTF8.GetBytes("replacement session"));
            check(Dacl(active) == activeAcl && File.ReadAllText(active) == "replacement session",
                "atomic replacement preserves a native session DACL stricter than the addon policy");
            using (var locked = File.Open(active, FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => NativeStores.AtomicWrite(active, [4, 5, 6]), "locked replacement fails without an overwrite fallback");
            check(Dacl(active) == activeAcl && File.ReadAllText(active) == "replacement session" &&
                !Directory.EnumerateFiles(root, "*.tmp").Any(), "failed atomic replacement preserves native bytes/ACL and removes its staging file");

            var old = stores.Catalog.Stores.Single();
            using (var locked = File.Open(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => stores.Activate(old), "failed activation rolls back when metadata cannot be committed");
            check(Dacl(active) == activeAcl && File.ReadAllText(active) == "replacement session" && stores.Catalog.Active == pending.Folder,
                "activation rollback preserves native session bytes, ACL and active selection");
            check(IsPrivate(Path.Combine(nativeKeys, "key")) && Dacl(nativeKey) == nativeKeyAcl && Dacl(nativeKeys) == nativeDirectoryAcl,
                "restored missing native keys are private while existing native key and directory ACLs are preserved");
            var empty = stores.CreatePending();
            using (var locked = File.Open(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => stores.Activate(empty), "failed sign-in transition rolls back after deleting the synthetic active session");
            check(Dacl(active) == activeAcl && File.ReadAllText(active) == "replacement session",
                "rollback of a deleted native session restores its original restrictive ACL");

            var inherited = new FileSecurity(); inherited.SetAccessRuleProtection(false, false);
            inherited.AddAccessRule(new FileSystemAccessRule(User, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(active).SetAccessControl(inherited);
            var inheritedAcl = Dacl(active);
            NativeStores.AtomicWrite(active, Encoding.UTF8.GetBytes("inherited session"));
            check(Dacl(active) == inheritedAcl, "replacement preserves inherited native session permissions");
            using (var locked = File.Open(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => stores.Activate(empty), "failed sign-in with inherited permissions rolls back");
            check(Dacl(active) == inheritedAcl && File.ReadAllText(active) == "inherited session",
                "deleted-session rollback preserves inherited DACL and protection flags");

            var linkedRoot = Path.Combine(root, "linked"); var accounts = Path.Combine(linkedRoot, "Accounts"); Directory.CreateDirectory(accounts);
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "sentinel.dat"); File.WriteAllText(sentinel, "external synthetic data");
            var outsideAcl = Dacl(outside); var sentinelAcl = Dacl(sentinel);
            var hardLink = Path.Combine(accounts, "hard-link.dat");
            if (!CreateHardLink(hardLink, sentinel, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Reject(() => _ = new NativeStores(linkedRoot), "migration rejects hard links before changing external ACLs");
            check(Dacl(sentinel) == sentinelAcl && File.ReadAllText(sentinel) == "external synthetic data", "hard-link target remains unchanged");
            File.Delete(hardLink);
            var junction = Path.Combine(accounts, "linked-directory");
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{junction.Replace("'", "''")}' -Value '{outside.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
            using (var process = Process.Start(start)!) { process.WaitForExit(); if (process.ExitCode != 0) throw new IOException("Could not create fixture junction."); }
            Reject(() => _ = new NativeStores(linkedRoot), "migration rejects junctions without traversing their targets");
            Reject(() => NativeStores.AtomicWrite(Path.Combine(junction, "sentinel.dat"), [9]), "atomic writer rejects a linked ancestor");
            check(Dacl(outside) == outsideAcl && Dacl(sentinel) == sentinelAcl && File.ReadAllText(sentinel) == "external synthetic data",
                "junction target bytes and ACLs remain unchanged");
            Directory.Delete(junction);
            var blocked = Path.Combine(accounts, "blocked.dat"); File.WriteAllText(blocked, "locked fixture");
            using (var locked = File.Open(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(() => _ = new NativeStores(linkedRoot), "inaccessible migration files stop initialization before metadata is written");
            check(!File.Exists(Path.Combine(accounts, "native-stores.json")) && File.ReadAllText(blocked) == "locked fixture",
                "migration failure preserves data and does not continue with unsecured storage");

            MakeBroad(accounts); MakeBroad(blocked);
            var beforeDirectory = Dacl(accounts); var beforeFile = Dacl(blocked);
            using (var writer = OpenDirectory(accounts, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (writer.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Reject(() => _ = new NativeStores(linkedRoot), "migration refuses a directory already open for reparse mutation");
                check(Dacl(accounts) == beforeDirectory && Dacl(blocked) == beforeFile &&
                    !File.Exists(Path.Combine(accounts, "native-stores.json")), "directory lock failure occurs before any ACL or metadata change");
            }
            _ = new NativeStores(linkedRoot);
            check(IsPrivate(accounts) && IsPrivate(blocked), "migration succeeds after conflicting directory handle closes");
        }
        finally { Cleanup(root); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path, uint access, uint share,
        IntPtr security, uint mode, uint flags, IntPtr template);
}
