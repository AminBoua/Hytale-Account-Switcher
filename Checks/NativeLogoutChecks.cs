using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using HytaleAccounts;

internal static class NativeLogoutChecks
{
    // Exercise production logout orchestration with opaque fixture sessions and
    // simulated native acknowledgements. Never revoke a user's real browser login.
    public static async Task Run(Action<bool, string> check)
    {
        await RunCore(check, false);
        await RunCore(check, true);
    }
    private static Task RunCore(Action<bool, string> check, bool lockSnapshots)
    {
        NativeStores.RequireIdle();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                var root = SecurityFixture.Create("native-logout-");
                try
                {
                    var stores = new NativeStores(root);
                    var sessionFile = Path.Combine(root, "account.dat");
                    NativeStore Account(string label)
                    {
                        var store = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), label);
                        File.WriteAllText(sessionFile, label); stores.Capture(store);
                        return store;
                    }
                    var accountB = Account("accountB");
                    var stale = Account("stale");
                    var accountA = Account("accountA");
                    stores.Activate(accountB); stores.Select(accountB);
                    using var owner = Process.GetCurrentProcess();
                    var pid = owner.Id;
                    var session = new NativeSession(stores, dispatcher);
                    const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
                    void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
                    Task Invoke(string name, params object[] args) => (Task)typeof(NativeSession).GetMethod(name, flags)!.Invoke(session, args)!;
                    Set("launcher", owner); Set("active", accountB); Set("verifiedSession", true);
                    var signal = new TaskCompletionSource<Guid?>(); Set("ready", signal);
                    Guid? resident = accountB.Profile;
                    var handled = new HashSet<string>();
                    var revoked = new List<Guid?>();
                    var operations = new List<string>();
                    bool failLogout = false;
                    string status = "";
                    session.Status += text =>
                    {
                        status = text;
                        if (Get("logoutRequest") is string logout && handled.Add(logout))
                        {
                            if (!failLogout) { revoked.Add(resident); resident = null; File.Delete(sessionFile); }
                            ((TaskCompletionSource<bool>)Get("logoutResult")!).TrySetResult(!failLogout);
                        }
                        if (Get("sessionRequest") is not string request || !handled.Add(request)) return;
                        var operation = (string)Get("sessionOperation")!; operations.Add(operation);
                        if (operation == "load")
                        {
                            var label = File.ReadAllText(sessionFile);
                            resident = stores.Catalog.Stores.Single(s => s.Label == label).Profile;
                        }
                        if (operation == "clear") resident = null;
                        var result = JsonSerializer.SerializeToElement(new { success = operation != "load" || resident != stale.Profile,
                            profile = resident?.ToString(), username = resident == accountA.Profile ? "accountA" : null,
                            loggedIn = resident is not null });
                        ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(result);
                    };
                    string Snapshot(NativeStore store) => Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat");
                    var staleBytes = File.ReadAllBytes(Snapshot(stale));
                    using (var locked = lockSnapshots ? new FileStream(Snapshot(accountB), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                        await Invoke("LogoutCore", accountB);
                    check(session.IsOpen && owner.Id == pid && resident == accountA.Profile && stores.Catalog.Selected == accountA.Folder,
                        "logout restores a usable saved account without replacing the native process");
                    if (lockSnapshots)
                    {
                        check(File.ReadAllText(Snapshot(accountB)) == "accountB" &&
                            new NativeStores(root).Catalog.Stores.Single(s => s.Folder == accountB.Folder).SignedOut &&
                            status.Contains("Saved session cleanup failed:") && !status.Contains("Other accounts were preserved."),
                            "locked revoked snapshot stays excluded across reload and cleanup warning survives successful fallback");
                    }
                    check(File.ReadAllBytes(Snapshot(stale)).SequenceEqual(staleBytes) && File.ReadAllText(Snapshot(accountA)) == "accountA",
                        "logout cleanup preserves unrelated saved snapshots");
                    check(revoked.SequenceEqual(new[] { accountB.Profile }) && stores.Catalog.Stores.Single(s => s.Folder == accountB.Folder).SignedOut &&
                        !stores.Catalog.Stores.Single(s => s.Folder == accountA.Folder).SignedOut,
                        "logout revokes only the selected account and skips an unusable fallback");
                    check(!operations.Contains("clear"), "sign-in is not shown when another saved account is usable");
                    var inactive = JsonSerializer.SerializeToElement(new { action = "logout", store = stale.Folder });
                    var accepted = await (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!.Invoke(session, new object[] { inactive, signal })!;
                    check(!accepted && revoked.Count == 1, "inactive accounts cannot be logged out through the bridge");
                    failLogout = true;
                    await Invoke("LogoutCore", accountA);
                    check(session.IsOpen && resident == accountA.Profile && stores.Catalog.Selected == accountA.Folder && revoked.Count == 1,
                        "failed native logout retains the account and running launcher");
                    failLogout = false;
                    using (var locked = lockSnapshots ? new FileStream(Snapshot(accountA), FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                        await Invoke("LogoutCore", accountA);
                    check(session.IsOpen && !owner.HasExited && owner.Id == pid && resident is null && stores.Catalog.Selected is null &&
                        Get("active") is NativeStore pending && stores.IsPending(pending) && status.StartsWith("Account logged out. Sign in to continue.") &&
                        status.Contains("Saved session cleanup failed:") == lockSnapshots,
                        "logout with no usable fallback clears cached native identity and shows sign-in without restart");
                    check(revoked.SequenceEqual(new[] { accountB.Profile, accountA.Profile }) && operations.Contains("clear"),
                        "final sign-in clears native memory without revoking the failed fallback");
                    if (!lockSnapshots)
                    {
                        var blocked = Account("metadataFailure");
                        Set("active", blocked); Set("verifiedSession", true); resident = blocked.Profile;
                        var metadata = Path.Combine(stores.Root, "native-stores.json");
                        var metadataBytes = File.ReadAllBytes(metadata);
                        var snapshotBytes = File.ReadAllBytes(Snapshot(blocked));
                        operations.Clear();
                        using (var locked = new FileStream(metadata, FileMode.Open, FileAccess.Read, FileShare.Read))
                            await Invoke("LogoutCore", blocked);
                        check(session.IsOpen && owner.Id == pid && resident is null && !(bool)Get("verifiedSession")! &&
                            operations.Count == 0 && status.Contains("Native logout succeeded, but account recovery did not complete."),
                            "failed sign-out persistence reports incomplete recovery without claiming success or restarting");
                        check(File.ReadAllBytes(metadata).SequenceEqual(metadataBytes) &&
                            File.ReadAllBytes(Snapshot(blocked)).SequenceEqual(snapshotBytes) && stores.Catalog.Selected == blocked.Folder &&
                            !stores.Catalog.Stores.Single(s => s.Folder == blocked.Folder).SignedOut,
                            "failed sign-out persistence preserves metadata and recovery snapshot before fallback");
                    }
                    complete.TrySetResult();
                }
                catch (Exception ex) { complete.TrySetException(ex); }
                finally
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return complete.Task;
    }
}
