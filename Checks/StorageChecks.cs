using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace HytaleAccounts.Checks;

internal static class StorageChecks
{
    private static readonly SecurityIdentifier Stranger = new("S-1-5-21-314159265-271828182-161803398-4242");

    internal static void Run()
    {
        var name = "hytale-storage-check-" + Guid.NewGuid().ToString("N");
        var fixture = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
        WindowsPermissions.CreatePrivateDirectory(fixture);
        try
        {
            CheckLegacyAndSwitching(Path.Combine(fixture, "legacy"));
            CheckRefusals(Path.Combine(fixture, "refusals"));
        }
        finally { Directory.Delete(fixture, recursive: true); }
        Console.WriteLine("PASS legacy account storage, session restoration and Windows access checks");
    }

    private static void CheckLegacyAndSwitching(string root)
    {
        var legacy = Path.Combine(root, "Accounts", "legacy", "AppData", "Hytale");
        var discovered = Path.Combine(root, "Accounts", "discovered", "AppData", "Hytale");
        var nativeFile = Path.Combine(root, "account.dat");
        var legacyFile = Path.Combine(legacy, "account.dat");
        WindowsPermissions.CreatePrivateDirectory(root);
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(discovered);
        File.WriteAllText(legacyFile, "legacy session");
        Grant(legacyFile, FileSystemRights.ReadAndExecute);
        File.WriteAllText(Path.Combine(discovered, "account.dat"), "discovered session");
        File.WriteAllText(Path.Combine(root, "Accounts", "native-stores.json"),
            """{"Stores":[{"Folder":"legacy","Label":"Original label"}],"Selected":"legacy","Active":"legacy"}""");
        File.WriteAllText(nativeFile, "refreshed session");
        Grant(nativeFile, FileSystemRights.ReadAndExecute);
        var originalAcl = Dacl(nativeFile);

        var storage = new AccountStorage(root);
        var first = storage.Catalog.Stores.Single(store => store.Folder == "legacy");
        Check(first.Label == "Original label" && first.Profile is null && !first.SignedOut, "Read the old catalog format.");
        Check(storage.Catalog.Stores.Any(store => store.Folder == "discovered"), "Discover the legacy directory.");
        Check(!Dacl(legacyFile).Contains(Stranger.Value), "Restrict legacy snapshot permissions.");
        storage.Activate(first);
        Check(File.ReadAllText(nativeFile) == "refreshed session", "Keep the current native session on restart.");
        storage.Activate(first, restoreSnapshot: true);
        Check(File.ReadAllText(nativeFile) == "legacy session" && Dacl(nativeFile) == originalAcl,
            "Restore opaque bytes while retaining the native DACL.");

        var firstProfile = Guid.NewGuid();
        storage.VerifyProfile(first, firstProfile, select: true);
        Refuse(() => storage.VerifyProfile(first, Guid.NewGuid(), select: true), "Reject a profile mismatch.");
        var pending = storage.CreatePending();
        var nativeKeys = Path.Combine(root, ".keys");
        Directory.CreateDirectory(nativeKeys);
        File.WriteAllText(Path.Combine(nativeKeys, "key-z"), "pending key");
        File.WriteAllText(nativeFile, "pending session");
        storage.Capture(pending);
        var pendingFile = Path.Combine(storage.Folder(pending.Folder), "AppData", "Hytale", "account.dat");
        var pendingKeys = Path.Combine(Path.GetDirectoryName(pendingFile)!, ".keys");
        File.WriteAllText(Path.Combine(pendingKeys, "key-a"), "other key");
        storage.Activate(first, restoreSnapshot: true);
        File.WriteAllText(Path.Combine(nativeKeys, "key-z"), "new native key");
        File.WriteAllText(nativeFile, "current session");

        Refuse(() => storage.Activate(pending, restoreSnapshot: true), "Reject a conflicting native key.");
        Check(File.ReadAllText(nativeFile) == "current session" && File.ReadAllText(pendingFile) == "pending session" &&
            File.ReadAllText(Path.Combine(nativeKeys, "key-z")) == "new native key" &&
            !File.Exists(Path.Combine(nativeKeys, "key-a")), "Keep the active session and pending recovery data on a conflict.");
        storage.DiscardPending(pending);
        Check(File.ReadAllText(pendingFile) == "pending session", "Keep discarded pending recovery data after a conflict.");

        var released = storage.CreatePending();
        storage.Capture(released);
        var releasedFile = Path.Combine(storage.Folder(released.Folder), "AppData", "Hytale", "account.dat");
        storage.ReleasePending(released);
        Check(File.ReadAllText(releasedFile) == "current session", "Keep released pending recovery data.");

        var reopened = new AccountStorage(root);
        Check(reopened.Catalog.Stores.Single(store => store.Folder == "legacy").Profile == firstProfile,
            "Retain the verified profile in the compatible catalog.");
    }

    private static void CheckRefusals(string root)
    {
        WindowsPermissions.CreatePrivateDirectory(root);
        var file = Path.Combine(root, "session");
        File.WriteAllBytes(file, [1, 2, 3]);
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Refuse(() => WindowsFiles.Read(file), "Refuse a conflicting file handle.");
        Grant(file, FileSystemRights.WriteData);
        Refuse(() => WindowsFiles.Read(file), "Refuse a file another Windows user can change.");
        Refuse(() => WindowsFiles.Write(file, [9], privateFile: true), "Refuse replacement of an unsafe file.");
        Check(File.ReadAllBytes(file).SequenceEqual(new byte[] { 1, 2, 3 }), "Preserve a refused file.");

        var legacy = Path.Combine(root, "migration");
        Directory.CreateDirectory(legacy);
        var untouched = Path.Combine(legacy, "readable");
        var unsafeFile = Path.Combine(legacy, "writable");
        File.WriteAllBytes(untouched, [8]);
        File.WriteAllBytes(unsafeFile, [9]);
        Grant(untouched, FileSystemRights.ReadAndExecute);
        Grant(unsafeFile, FileSystemRights.ChangePermissions);
        var untouchedAcl = Dacl(untouched);
        Refuse(() => WindowsPermissions.SecureTree(legacy), "Refuse migration of a foreign-mutable child.");
        Check(Dacl(untouched) == untouchedAcl, "Leave unrelated permissions unchanged on failed inspection.");

        var metadata = Path.Combine(root, "journal");
        var staged = Path.Combine(root, "journal-staged");
        WindowsFiles.Write(metadata, Encoding.UTF8.GetBytes("old"));
        WindowsFiles.Write(staged, Encoding.UTF8.GetBytes("updated"));
        using (var target = WindowsFiles.OpenOwned(metadata))
        using (var source = WindowsFiles.OpenOwned(staged))
        {
            Check(target.Identity != source.Identity, "Distinguish held file identities.");
            target.ReplaceFrom(source);
            Check(target.Read().AsSpan().SequenceEqual("updated"u8) && !source.Exists, "Replace a held journal with a staged sibling.");
        }
        var linked = Path.Combine(root, "journal-link");
        if (!CreateHardLink(linked, metadata, IntPtr.Zero))
            throw new IOException("Could not create the hard-link fixture.");
        Refuse(() => WindowsFiles.Write(linked, [1], privateFile: true), "Refuse a hard-link destination.");
        Check(File.ReadAllText(metadata) == "updated", "Preserve the hard-linked target.");
        File.Delete(linked);

        var empty = Path.Combine(root, "empty");
        WindowsPermissions.CreatePrivateDirectory(empty);
        WindowsFiles.DeleteEmptyDirectory(empty);
        Check(!Directory.Exists(empty), "Remove an empty pinned directory.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr security);

    private static void Grant(string path, FileSystemRights rights)
    {
        var file = new FileInfo(path);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(Stranger, rights, AccessControlType.Allow));
        file.SetAccessControl(acl);
    }

    private static string Dacl(string path) =>
        new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    private static void Refuse(Action action, string message)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new IOException(message);
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new IOException(message);
    }
}
