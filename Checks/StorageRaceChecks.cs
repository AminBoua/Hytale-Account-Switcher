using System.IO;
using HytaleAccounts;

// Only compiled against disposable, instrumented source by AclMigrationRaceChecks.ps1 -Storage.
internal static class StorageRaceChecks
{
    private static void Check(bool result, string message)
    { if (!result) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (IOException) { Check(true, message); return; }
        throw new Exception("FAIL " + message);
    }
    public static void Main()
    {
        var root = SecurityFixture.Create("storage-races-");
        try
        {
            var directory = Path.Combine(root, "data"); Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "bytes.bin"); File.WriteAllText(path, "old synthetic bytes");
            var reached = false;
            StorageRaceProbe.Checkpoint = (phase, candidate) =>
            {
                if (phase != "read" || candidate != path) return;
                reached = true;
                Reject(() => Directory.Move(directory, directory + "-moved"), "source ancestors remain pinned while bytes are read");
                Reject(() => File.Move(path, path + "-moved"), "source leaf remains pinned while bytes are read");
                Reject(() => File.WriteAllText(path, "substitution"), "validated source cannot be overwritten before reading");
            };
            Check(System.Text.Encoding.UTF8.GetString(NativeStores.ReadFile(path)) == "old synthetic bytes" && reached,
                "guarded read uses the validated file object");
            reached = false;
            StorageRaceProbe.Checkpoint = (phase, candidate) =>
            {
                if (phase != "write" || candidate != path) return;
                reached = true;
                Reject(() => Directory.Move(directory, directory + "-moved"), "atomic write retains parent rename protection");
                Reject(() => File.WriteAllText(path, "substitution"), "atomic write retains destination writer exclusion");
            };
            NativeStores.AtomicWrite(path, System.Text.Encoding.UTF8.GetBytes("new private bytes"), privateFile: true);
            Check(reached && File.ReadAllText(path) == "new private bytes", "private atomic rename commits under retained handles");
            var fresh = Path.Combine(directory, "new.bin"); reached = false;
            StorageRaceProbe.Checkpoint = (phase, candidate) =>
            {
                if (phase != "write" || candidate != fresh) return;
                File.WriteAllText(fresh, "concurrently created file"); reached = true;
            };
            Reject(() => NativeStores.AtomicWrite(fresh, [9], privateFile: true), "atomic creation refuses an intervening destination");
            Check(reached && File.ReadAllText(fresh) == "concurrently created file" && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
                "concurrent file preserved and private temporary removed");
            var folder = Path.Combine(directory, "new-folder"); reached = false;
            StorageRaceProbe.Checkpoint = (phase, candidate) =>
            {
                if (phase != "create" || candidate != folder) return;
                Reject(() => Directory.Move(directory, directory + "-moved"), "private directory creation pins the parent");
                Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "sentinel"), "foreign fixture"); reached = true;
            };
            Reject(() => PrivateFiles.CreateDirectory(folder), "private directory creation never adopts an intervening directory");
            Check(reached && File.ReadAllText(Path.Combine(folder, "sentinel")) == "foreign fixture", "intervening directory is preserved");
        }
        finally
        {
            StorageRaceProbe.Checkpoint = null;
            if (!Path.GetFileName(root).StartsWith("storage-races-", StringComparison.Ordinal)) throw new IOException("Invalid fixture root.");
            Directory.Delete(root, recursive: true);
        }
    }
}
