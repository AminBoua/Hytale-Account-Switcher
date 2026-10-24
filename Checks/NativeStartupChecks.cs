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
                check(savedB ? !File.Exists(Snapshot(initial)) : File.Exists(Snapshot(initial)),
                    "successful startup reconciliation removes disposable snapshots and retains newly committed snapshots");
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
                check(File.ReadAllText(official) == "opaque-current-B" && stores.Catalog.Active == verifiedB.Folder,
                    "startup restores a missing official session even when the active marker already matches the saved fallback");
                File.WriteAllText(official, "opaque-latest-B");
                stores.Activate(stores.Catalog.Stores.Single(s => s.Folder == verifiedB.Folder));
                check(File.ReadAllText(official) == "opaque-latest-B", "matching existing native session keeps the latest refresh");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
