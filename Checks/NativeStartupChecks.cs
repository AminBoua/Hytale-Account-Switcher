using System.IO;
using System.Reflection;
using System.Windows.Threading;
using HytaleAccounts;

internal static class NativeStartupChecks
{
    // Simulate Start's activation and verified profile report; never launch Hytale.
    public static void Run(Action<bool, string> check)
    {
        foreach (var savedB in new[] { false, true })
        foreach (var staleActive in new[] { false, true })
        {
            var root = SecurityFixture.Create("native-startup-");
            try
            {
                var stores = new NativeStores(root);
                var official = Path.Combine(root, "account.dat");
                string Snapshot(NativeStore store) => Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat");
                string Key(NativeStore store) => Path.Combine(Path.GetDirectoryName(Snapshot(store))!, ".keys", "key.bin");
                var nativeKeys = Path.Combine(root, ".keys"); Directory.CreateDirectory(nativeKeys);
                File.WriteAllText(Path.Combine(nativeKeys, "key.bin"), "opaque-key");
                var profileB = Guid.NewGuid();
                NativeStore? accountB = null;
                if (savedB)
                {
                    accountB = stores.CommitPending(stores.CreatePending(), profileB, "accountB");
                    File.WriteAllText(official, "opaque-old-B"); stores.Capture(accountB);
                }
                var accountA = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), "accountA");
                File.WriteAllText(official, "opaque-A"); stores.Capture(accountA);
                if (staleActive) { stores.Catalog.Active = accountB?.Folder; stores.Save(); }
                File.WriteAllText(official, "opaque-current-B"); // Later login through the official launcher.
                stores = new NativeStores(root);
                var session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
                var initial = session.InitialStore();
                check(stores.IsPending(initial) && stores.Catalog.Selected == accountA.Folder &&
                    stores.Catalog.Stores.Count == (savedB ? 2 : 1) &&
                    File.ReadAllText(official) == "opaque-current-B" && File.ReadAllText(Snapshot(accountA)) == "opaque-A" &&
                    (accountB is null || File.ReadAllText(Snapshot(accountB)) == "opaque-old-B"),
                    $"startup stages the official session without trusting selection A (saved B: {savedB}, stale active: {staleActive})");
                stores.Activate(initial);
                check(File.ReadAllText(official) == "opaque-current-B", "startup activation preserves the official session before native verification");
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(NativeSession).GetField("active", flags)!.SetValue(session, initial);
                typeof(NativeSession).GetField("nativeName", flags)!.SetValue(session, "accountB");
                var commit = typeof(NativeSession).GetMethod("CommitProfile", flags)!;
                using (var locked = new FileStream(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { commit.Invoke(session, [profileB, false, null]); throw new Exception("Locked startup reconciliation committed"); }
                    catch (TargetInvocationException ex) when (ex.InnerException is IOException or UnauthorizedAccessException) { }
                    check(stores.Catalog.Selected == accountA.Folder && stores.IsPending(initial) &&
                        File.ReadAllText(Snapshot(initial)) == "opaque-current-B" && File.ReadAllText(Key(initial)) == "opaque-key" &&
                        File.ReadAllText(Snapshot(accountA)) == "opaque-A" &&
                        (accountB is null || File.ReadAllText(Snapshot(accountB)) == "opaque-old-B"),
                        "failed startup commit keeps the previous selection and both saved snapshots");
                }
                commit.Invoke(session, [profileB, false, null]);
                var reloaded = new NativeStores(root);
                var verifiedB = reloaded.Catalog.Stores.Single(s => s.Profile == profileB);
                check(reloaded.Catalog.Selected == verifiedB.Folder && reloaded.Catalog.Active == verifiedB.Folder &&
                    reloaded.Catalog.Stores.Count == 2 && (accountB is null || verifiedB.Folder == accountB.Folder),
                    "native verification selects B and reuses its saved store when present");
                check(File.ReadAllText(official) == "opaque-current-B" && File.ReadAllText(Snapshot(verifiedB)) == "opaque-current-B" &&
                    File.ReadAllText(Snapshot(accountA)) == "opaque-A" && reloaded.Catalog.Stores.Single(s => s.Folder == accountA.Folder) == accountA,
                    "startup reconciliation preserves A's identity and snapshot and captures B only under B");
                check(initial.Folder == verifiedB.Folder || !Directory.Exists(stores.Folder(initial.Folder)),
                    "successful reconciliation removes only the redundant pending snapshot and keys");
                for (var restart = 0; restart < 3; restart++)
                {
                    stores = new NativeStores(root);
                    session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
                    initial = session.InitialStore(); stores.Activate(initial);
                    typeof(NativeSession).GetField("active", flags)!.SetValue(session, initial);
                    var run = Path.Combine(stores.Folder(initial.Folder), "run-fixture"); Directory.CreateDirectory(run);
                    var executable = Path.Combine(run, "hytale-launcher.exe"); File.WriteAllText(executable, "synthetic-runtime");
                    using var image = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
                    typeof(NativeSession).GetField("image", flags)!.SetValue(session, image);
                    typeof(NativeSession).GetField("runImage", flags)!.SetValue(session, executable);
                    commit.Invoke(session, [profileB, false, null]);
                    check(!File.Exists(Snapshot(initial)) && !File.Exists(Key(initial)) &&
                        File.ReadAllText(executable) == "synthetic-runtime" && !image.SafeFileHandle.IsClosed,
                        "repeated startup removes redundant credentials while retaining the owned runtime");
                    ((Task)typeof(NativeSession).GetMethod("Stop", flags)!.Invoke(session, null)!).GetAwaiter().GetResult();
                    check(!Directory.Exists(stores.Folder(initial.Folder)) &&
                        Directory.GetDirectories(stores.Root).Length == stores.Catalog.Stores.Count &&
                        File.ReadAllText(Snapshot(verifiedB)) == "opaque-current-B" && File.ReadAllText(Key(verifiedB)) == "opaque-key" &&
                        File.ReadAllText(official) == "opaque-current-B" && File.ReadAllText(Path.Combine(nativeKeys, "key.bin")) == "opaque-key",
                        "runtime exit prunes empty staging directories without accumulating snapshots or touching native storage");
                }
                File.WriteAllText(official, "opaque-recovery-B");
                session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
                var recovery = session.InitialStore(); stores.Activate(recovery);
                typeof(NativeSession).GetField("active", flags)!.SetValue(session, recovery);
                using (var locked = new FileStream(official, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { commit.Invoke(session, [profileB, false, null]); throw new Exception("Locked startup capture succeeded"); }
                    catch (TargetInvocationException ex) when (ex.InnerException is IOException or UnauthorizedAccessException) { }
                }
                check(File.ReadAllText(Snapshot(recovery)) == "opaque-recovery-B" && File.ReadAllText(Key(recovery)) == "opaque-key" &&
                    File.ReadAllText(Snapshot(verifiedB)) == "opaque-current-B",
                    "capture failure preserves the staged session and keys for recovery after reconciliation");
                session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
                initial = session.InitialStore(); stores.Activate(initial);
                typeof(NativeSession).GetField("active", flags)!.SetValue(session, initial);
                var unknown = Path.Combine(stores.Folder(initial.Folder), "recovery.txt"); File.WriteAllText(unknown, "keep");
                File.WriteAllText(Key(initial), "unique-key");
                var extraKey = Path.Combine(Path.GetDirectoryName(Key(initial))!, "recovery.bin"); File.WriteAllText(extraKey, "extra-key");
                commit.Invoke(session, [profileB, false, null]);
                check(File.ReadAllText(unknown) == "keep" && File.ReadAllText(Snapshot(recovery)) == "opaque-recovery-B" &&
                    File.ReadAllText(Key(recovery)) == "opaque-key" && File.ReadAllText(Key(initial)) == "unique-key" &&
                    File.ReadAllText(extraKey) == "extra-key" && !File.Exists(Snapshot(initial)),
                    "later success preserves unknown files, unmatched keys and earlier failed-operation recovery data");
                check(File.ReadAllText(Snapshot(verifiedB)) == "opaque-recovery-B", "successful retry captures the latest official session before committing selection");
                File.Delete(official);
                check(session.InitialStore() == verifiedB, "startup without an official session retains the saved-selection fallback");
                using (var locked = File.Open(Snapshot(verifiedB), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    try { stores.Activate(stores.Catalog.Stores.Single(s => s.Folder == verifiedB.Folder)); throw new Exception("Locked missing-session restoration succeeded"); }
                    catch (IOException) { }
                    check(!File.Exists(official) && stores.Catalog.Active == verifiedB.Folder && stores.Catalog.Selected == verifiedB.Folder,
                        "missing-session restoration reports a locked snapshot without changing selection or the active marker");
                }
                stores.Activate(stores.Catalog.Stores.Single(s => s.Folder == verifiedB.Folder));
                check(File.ReadAllText(official) == "opaque-recovery-B" && stores.Catalog.Active == verifiedB.Folder,
                    "startup restores a missing official session even when the active marker already matches the saved fallback");
                File.WriteAllText(official, "opaque-latest-B");
                stores.Activate(stores.Catalog.Stores.Single(s => s.Folder == verifiedB.Folder));
                check(File.ReadAllText(official) == "opaque-latest-B", "matching existing native session keeps the latest refresh");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
