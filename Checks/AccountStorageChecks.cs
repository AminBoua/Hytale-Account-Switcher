using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using HytaleAccounts;

internal static class AccountStorageChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        check(!NativeStores.NewerVersion("2026.09.30-bbbbbbb", "2026.09.30-aaaaaaa") &&
            !NativeStores.NewerVersion("2026.09.30-aaaaaaa", "2026.09.30-bbbbbbb"),
            "same-day commit hashes establish no release ordering in either direction");
        check(NativeStores.NewerVersion("2026.10.01-aaaaaaa", "2026.09.30-bbbbbbb") &&
            !NativeStores.NewerVersion("2026.09.30-bbbbbbb", "2026.10.01-aaaaaaa") &&
            !NativeStores.NewerVersion(null, "2026.09.30-aaaaaaa") &&
            !NativeStores.NewerVersion("invalid", "2026.09.30-aaaaaaa"), "only valid later build dates establish release ordering");
        var root = SecurityFixture.Create("account-storage-");
        try
        {
            var stores = new NativeStores(root);
            var official = Path.Combine(root, "official.exe"); File.WriteAllText(official, "installed build A");
            var originalHash = PatchInstaller.Hash(official);
            var cached = Path.Combine(stores.Root, "launcher-update.exe");
            var receipt = Path.Combine(stores.Root, "launcher-update.json");
            var bytes = Encoding.UTF8.GetBytes("native update B");
            check(stores.SaveLauncherUpdate(bytes, originalHash) && stores.UseCachedUpdate(cached, official, originalHash),
                "native update receipt binds cached bytes to the exact official build superseded");
            check(!stores.SaveLauncherUpdate(bytes, originalHash), "duplicate native update with matching provenance is ignored");
            File.WriteAllText(official, "installed build C");
            check(!stores.UseCachedUpdate(cached, official, PatchInstaller.Hash(official)),
                "an unrelated installed build cannot be overridden by cached same-day or versionless bytes");
            File.WriteAllText(cached, "unrecorded cached bytes");
            check(!stores.UseCachedUpdate(cached, official, originalHash), "update provenance cannot authorize different cached bytes");
            File.Delete(receipt);
            check(!stores.UseCachedUpdate(cached, official, originalHash), "legacy cache without provenance cannot establish same-day ordering");
            File.WriteAllText(receipt, "invalid JSON");
            check(!stores.UseCachedUpdate(cached, official, originalHash), "invalid update receipt falls back to the installed build");
            using (var locked = File.Open(receipt, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { stores.SaveLauncherUpdate(bytes, originalHash); throw new Exception("Locked update receipt replaced"); }
                catch (IOException) { }
                check(!stores.UseCachedUpdate(cached, official, originalHash), "interrupted cache commit cannot authorize a same-day override");
            }
            check(stores.SaveLauncherUpdate(bytes, originalHash) && stores.UseCachedUpdate(cached, official, originalHash),
                "retry repairs update provenance after a failed receipt write even when cached bytes already match");

            var active = Path.Combine(root, "account.dat"); File.WriteAllText(active, "opaque session");
            var keys = Path.Combine(root, ".keys"); PrivateFiles.CreateDirectory(keys);
            File.WriteAllText(Path.Combine(keys, "known-key"), "opaque native key");
            var committed = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), "saved"); stores.Capture(committed);
            var pending = stores.CreatePending(); stores.Capture(pending);
            var storage = Path.Combine(stores.Folder(pending.Folder), "AppData", "Hytale");
            var snapshot = Path.Combine(storage, "account.dat");
            var unknown = Path.Combine(storage, ".keys", "unknown-key"); File.WriteAllText(unknown, "unknown native file");
            var runtime = Path.Combine(stores.Folder(pending.Folder), "run-fixture"); PrivateFiles.CreateDirectory(runtime);
            var executable = Path.Combine(runtime, "hytale-launcher.exe"); File.WriteAllText(executable, "generated image");
            var unknownRuntime = Path.Combine(runtime, "native.log"); File.WriteAllText(unknownRuntime, "native log");
            var abandoned = Path.Combine(stores.Root, "pending-unknown", "AppData", "Hytale"); PrivateFiles.CreateDirectory(abandoned);
            var unknownSnapshot = Path.Combine(abandoned, "account.dat"); File.WriteAllText(unknownSnapshot, "untracked native snapshot");
            var session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NativeSession).GetField("runImage", flags)!.SetValue(session, executable);
            var image = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            typeof(NativeSession).GetField("image", flags)!.SetValue(session, image);
            try
            {
                using (var locked = File.Open(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    stores.DiscardPending(pending);
                    check(File.Exists(snapshot) && !stores.IsPending(pending) && !File.Exists(Path.Combine(storage, ".keys", "known-key")) &&
                        File.ReadAllText(unknown) == "unknown native file" && File.Exists(executable),
                        "discard removes known disposable keys, preserves unknown files and queues a locked snapshot without touching the runtime");
                }
                await (Task)typeof(NativeSession).GetMethod("Stop", flags)!.Invoke(session, null)!;
                check(!File.Exists(snapshot) && !File.Exists(executable) && File.ReadAllText(unknownRuntime) == "native log" &&
                    File.ReadAllText(unknown) == "unknown native file" && File.ReadAllText(active) == "opaque session" &&
                    File.ReadAllText(unknownSnapshot) == "untracked native snapshot" &&
                    File.ReadAllText(Path.Combine(stores.Folder(committed.Folder), "AppData", "Hytale", "account.dat")) == "opaque session",
                    "runtime release retries blocked snapshot cleanup and preserves committed, official and unknown native files");
                stores.DiscardPending(committed); stores.CleanupPending();
                check(File.Exists(Path.Combine(stores.Folder(committed.Folder), "AppData", "Hytale", "account.dat")),
                    "discard and repeated cleanup never delete a committed snapshot");
            }
            finally { image.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }
}
