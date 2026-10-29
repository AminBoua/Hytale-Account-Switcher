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
        await Run(check, lockSnapshot: false);
        await Run(check, lockSnapshot: true);
    }
    private static Task Run(Action<bool, string> check, bool lockSnapshot)
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
                    var window = owner.MainWindowHandle;
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
                    var logoutRequests = 0;
                    bool failLogout = false;
                    string status = "";
                    FileStream? LockSnapshot(NativeStore store) => lockSnapshot ? new FileStream(
                        Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat"),
                        FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                    void CheckCleanup(NativeStore store)
                    {
                        var snapshot = Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat");
                        if (!lockSnapshot)
                        {
                            check(!File.Exists(snapshot) && !status.Contains("Logout storage update failed:"),
                                "confirmed logout clears its obsolete snapshot despite the authenticated capture requirement");
                            return;
                        }
                        var saved = JsonSerializer.Deserialize<NativeCatalog>(File.ReadAllText(Path.Combine(stores.Root, "native-stores.json")))!;
                        check(File.ReadAllText(snapshot) == store.Label && saved.Stores.Single(s => s.Folder == store.Folder).SignedOut &&
                            saved.Selected != store.Folder && status.Contains("Logout storage update failed:") && status.Contains(snapshot),
                            "locked obsolete snapshot stays signed out on disk and its cleanup error remains in status");
                    }
                    session.Status += text =>
                    {
                        status = text;
                        if (Get("logoutRequest") is string logout && handled.Add(logout))
                        {
                            logoutRequests++;
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
                    using var lockedB = LockSnapshot(accountB);
                    await Invoke("LogoutCore", accountB);
                    check(session.IsOpen && ReferenceEquals(Get("launcher"), owner) && owner.Id == pid && owner.MainWindowHandle == window &&
                        ReferenceEquals(Get("ready"), signal) && resident == accountA.Profile && stores.Catalog.Selected == accountA.Folder &&
                        Get("verifiedSession") is true,
                        "logout restores a usable saved account without replacing the native process");
                    check(logoutRequests == 1 && revoked.SequenceEqual(new[] { accountB.Profile }) && stores.Catalog.Stores.Single(s => s.Folder == accountB.Folder).SignedOut &&
                        !stores.Catalog.Stores.Single(s => s.Folder == accountA.Folder).SignedOut,
                        "logout revokes only the selected account and skips an unusable fallback");
                    check(!operations.Contains("clear"), "sign-in is not shown when another saved account is usable");
                    CheckCleanup(accountB);
                    check(File.ReadAllBytes(Snapshot(stale)).SequenceEqual(staleBytes) && File.ReadAllText(Snapshot(accountA)) == "accountA",
                        "logout cleanup preserves unrelated saved snapshots");
                    if (lockSnapshot)
                        check(new NativeStores(root).Catalog.Stores.Single(s => s.Folder == accountB.Folder).SignedOut &&
                            !status.Contains("Other accounts were preserved."),
                            "locked revoked snapshot stays excluded across reload and cleanup warning survives successful fallback");
                    var inactive = JsonSerializer.SerializeToElement(new { action = "logout", store = stale.Folder });
                    var accepted = await (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!.Invoke(session, new object[] { inactive, signal })!;
                    check(!accepted && revoked.Count == 1, "inactive accounts cannot be logged out through the bridge");
                    failLogout = true;
                    var beforeFailedLogout = stores.Catalog.Stores.ToArray();
                    await Invoke("LogoutCore", accountA);
                    check(session.IsOpen && resident == accountA.Profile && stores.Catalog.Selected == accountA.Folder && revoked.Count == 1 &&
                        logoutRequests == 2 && Get("verifiedSession") is true && !stores.Catalog.Stores.Single(s => s.Folder == accountA.Folder).SignedOut &&
                        stores.Catalog.Stores.SequenceEqual(beforeFailedLogout) && operations.Last() == "run",
                        "failed native logout retains the account and running launcher");
                    failLogout = false;
                    using var lockedA = LockSnapshot(accountA);
                    await Invoke("LogoutCore", accountA);
                    check(session.IsOpen && !owner.HasExited && ReferenceEquals(Get("launcher"), owner) && owner.Id == pid &&
                        owner.MainWindowHandle == window && ReferenceEquals(Get("ready"), signal) && resident is null && stores.Catalog.Selected is null &&
                        Get("active") is NativeStore pending && stores.IsPending(pending) && Get("verifiedSession") is false &&
                        status.EndsWith("Account logged out. Sign in to continue.", StringComparison.Ordinal),
                        "logout with no usable fallback clears cached native identity and shows sign-in without restart");
                    check(logoutRequests == 3 && revoked.SequenceEqual(new[] { accountB.Profile, accountA.Profile }) && operations.Contains("clear"),
                        "final sign-in clears native memory without revoking the failed fallback");
                    CheckCleanup(accountA);
                    foreach (var fallback in new[] { true, false })
                        foreach (var metadata in new[] { false, true })
                            await StorageRecovery(Path.Combine(root, $"storage-{fallback}-{metadata}"), dispatcher, fallback, metadata, check);
                    if (!lockSnapshot)
                    {
                        foreach (var scenario in new[] { "transient", "persistent", "failed" })
                            await Confirmation(Path.Combine(root, "confirmation-" + scenario), dispatcher, scenario, check);
                        foreach (var mismatch in new[] { false, true })
                            foreach (var scenario in new[] { "switch", "failed rollback", "failed precheck rollback", "logout" })
                                await ResumeRecovery(Path.Combine(root, $"resume-{mismatch}-{scenario}"), dispatcher, mismatch, scenario, check);
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

    private static async Task Confirmation(string root, Dispatcher dispatcher, string scenario, Action<bool, string> check)
    {
        var stores = new NativeStores(root);
        var native = Path.Combine(root, "account.dat");
        NativeStore Account(string label)
        {
            var store = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), label);
            File.WriteAllText(native, label); stores.Capture(store);
            return store;
        }
        var fallback = Account("fallback");
        var unrelated = Account("unrelated");
        var current = Account("current");
        stores.Activate(current); stores.Select(current);
        string Snapshot(NativeStore store) => Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat");
        var snapshots = stores.Catalog.Stores.ToDictionary(s => s.Folder, s => File.ReadAllBytes(Snapshot(s)));
        var catalog = File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json"));
        using var owner = Process.GetCurrentProcess();
        var session = new NativeSession(stores, dispatcher);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
        void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
        Task<bool> Command(JsonElement data) => (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!.Invoke(session, new object[] { data, Get("ready")! })!;
        Task<bool> Reply(string request, object? success) => Command(JsonSerializer.SerializeToElement(new { action = "logout-result", request, success }));
        Set("launcher", owner); Set("active", current); Set("verifiedSession", true);
        Set("ready", new TaskCompletionSource<Guid?>());
        Guid? resident = current.Profile;
        var requests = new HashSet<string>();
        var operations = new List<string>();
        var status = "";
        session.Status += text =>
        {
            status = text;
            if (Get("logoutRequest") is string logout && requests.Add(logout) && scenario != "failed")
            {
                resident = null; File.Delete(native);
            }
            if (Get("sessionRequest") is not string request || !requests.Add(request)) return;
            var operation = (string)Get("sessionOperation")!; operations.Add(operation);
            if (operation == "load") resident = stores.Catalog.Stores.Single(s => s.Label == File.ReadAllText(native)).Profile;
            ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(JsonSerializer.SerializeToElement(new
                { success = true, profile = resident?.ToString(), loggedIn = resident is not null }));
        };
        var transition = (Task)typeof(NativeSession).GetMethod("LogoutCore", flags)!.Invoke(session, new object[] { current })!;
        var request = (string)Get("logoutRequest")!;
        check(Get("verifiedSession") is false && Get("busy") is true && Get("holdLogin") is true,
            scenario + " blocks Play as soon as logout is requested");
        check(!await Reply(request + "-stale", true) && !await Reply(request, "unknown") && !transition.IsCompleted,
            scenario + " rejects stale and malformed acknowledgements without settling logout");
        if (scenario != "failed")
            for (var retry = 0; retry < 3; retry++)
                check(await Reply(request, null) && !transition.IsCompleted && operations.Count == 0 && Get("verifiedSession") is false,
                    scenario + " keeps uncertain verification pending without restoring or resuming an account");
        if (scenario != "persistent") check(await Reply(request, scenario == "transient"), scenario + " accepts the final logout acknowledgement");
        // Persistent confirmation exercises the production 30-second acknowledgement timeout.
        await transition.WaitAsync(TimeSpan.FromSeconds(35));
        check(requests.Count == operations.Count + 1 && session.IsOpen && ReferenceEquals(Get("launcher"), owner),
            scenario + " issues one logout request and retains the native process and window");
        if (scenario == "transient")
            check(resident == fallback.Profile && stores.Catalog.Selected == fallback.Folder && Get("verifiedSession") is true &&
                stores.Catalog.Stores.Single(s => s.Folder == current.Folder).SignedOut && !File.Exists(Snapshot(current)),
                "transient verification failure still restores another usable account after confirmed sign-out");
        else
        {
            check(stores.Catalog.Selected == current.Folder && File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json")).SequenceEqual(catalog) &&
                File.ReadAllBytes(Snapshot(current)).SequenceEqual(snapshots[current.Folder]),
                scenario + " preserves the old selection and snapshot without marking it signed out");
            if (scenario == "persistent")
            {
                check(resident is null && Get("verifiedSession") is false && Get("holdLogin") is true && Get("busy") is false &&
                    operations.Count == 0 && status.Contains("Native logout could not be confirmed. Play remains blocked."),
                    "persistent verification timeout never claims the revoked old account remains usable");
                check(!await Reply(request, true) && !await Command(JsonSerializer.SerializeToElement(new
                    { action = "ready", profile = current.Profile.ToString() })) && Get("verifiedSession") is false,
                    "late acknowledgements and cached UI readiness cannot unblock an uncertain logout");
            }
            else
                check(resident == current.Profile && Get("verifiedSession") is true && Get("holdLogin") is false && operations.SequenceEqual(new[] { "run" }),
                    "genuine logout failure resumes and verifies the old account without trying saved accounts");
        }
        check(new[] { fallback, unrelated }.All(s => File.ReadAllBytes(Snapshot(s)).SequenceEqual(snapshots[s.Folder])),
            scenario + " leaves unrelated saved snapshots intact");
    }

    private static async Task ResumeRecovery(string root, Dispatcher dispatcher, bool mismatch, string scenario, Action<bool, string> check)
    {
        var stores = new NativeStores(root);
        var native = Path.Combine(root, "account.dat");
        NativeStore Account(string label)
        {
            var store = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), label);
            File.WriteAllText(native, label); stores.Capture(store);
            return store;
        }
        var accountB = Account("accountB");
        var accountC = Account("accountC");
        var accountA = Account("accountA");
        stores.Activate(accountA); stores.Select(accountA);
        string Snapshot(NativeStore store) => Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat");
        var session = new NativeSession(stores, dispatcher);
        using var owner = Process.GetCurrentProcess();
        var window = owner.MainWindowHandle;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
        void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
        Task Invoke(string name, params object[] args) => (Task)typeof(NativeSession).GetMethod(name, flags)!.Invoke(session, args)!;
        Set("launcher", owner); Set("active", accountA); Set("verifiedSession", true);
        var signal = new TaskCompletionSource<Guid?>(); Set("ready", signal);
        Task<bool> Command(object data) => (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!
            .Invoke(session, [JsonSerializer.SerializeToElement(data), signal])!;
        var saved = stores.Catalog.Stores.ToArray();
        var snapshots = saved.ToDictionary(s => s.Folder, s => File.ReadAllBytes(Snapshot(s)));
        var metadata = File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json"));
        var failedRollback = scenario is "failed rollback" or "failed precheck rollback";
        Guid? resident = accountA.Profile;
        NativeStore? loaded = null;
        var handled = new HashSet<string>();
        var loads = new List<string>();
        var revoked = new List<Guid?>();
        var rejectResume = true;
        var unverified = true;
        var deferredSelection = true;
        var deferredSnapshot = true;
        var signIn = false;
        session.Status += _ =>
        {
            if (Get("logoutRequest") is string logout && handled.Add(logout))
            {
                revoked.Add(resident); resident = null; File.Delete(native);
                ((TaskCompletionSource<bool>)Get("logoutResult")!).TrySetResult(true);
            }
            if (Get("sessionRequest") is not string request || !handled.Add(request)) return;
            var operation = (string)Get("sessionOperation")!;
            if (operation == "load")
            {
                loaded = stores.Catalog.Stores.Single(s => s.Label == File.ReadAllText(native));
                loads.Add(loaded.Folder); resident = loaded.Profile;
                File.WriteAllText(native, loaded.Label + "-refreshed");
            }
            if (operation == "run")
            {
                unverified &= Get("verifiedSession") is false && Get("busy") is true && Get("holdLogin") is true;
                var expectedSelection = scenario == "logout" ? null : accountA.Folder;
                deferredSelection &= stores.Catalog.Selected == expectedSelection && new NativeStores(root).Catalog.Selected == expectedSelection;
                deferredSnapshot &= loaded is null || File.ReadAllText(Snapshot(loaded)) == loaded.Label;
                if (rejectResume && (loaded?.Folder == accountB.Folder || failedRollback))
                {
                    // Resume lost authentication or changed identity and storage.
                    resident = mismatch ? accountC.Profile : null;
                    if (mismatch) File.WriteAllText(native, accountC.Label); else File.Delete(native);
                }
            }
            if (operation == "clear") { signIn = true; resident = null; }
            ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(JsonSerializer.SerializeToElement(new
                { success = true, profile = resident?.ToString(), loggedIn = resident is not null, username = loaded?.Label }));
        };
        var label = scenario + (mismatch ? " with mismatched resume" : " with signed-out resume");
        if (scenario == "logout") await Invoke("LogoutCore", accountA);
        else await session.Open(scenario == "failed precheck rollback" ? accountB with { SignedOut = true } : accountB, true);
        check(unverified && deferredSelection && deferredSnapshot,
            label + " blocks Play and defers selection and snapshot capture until resume verifies the expected profile");
        check(loads.SequenceEqual(scenario == "failed precheck rollback" ? Array.Empty<string>() :
                new[] { accountB.Folder, scenario == "logout" ? accountC.Folder : accountA.Folder }) && !signIn,
            label + " rejects unusable resume without loading an unexpected saved session");
        check(File.ReadAllText(Snapshot(accountB)) == "accountB" && revoked.SequenceEqual(scenario == "logout" ? new[] { accountA.Profile } : Array.Empty<Guid?>()),
            label + " preserves the rejected account snapshot and revokes only the requested logout account");
        if (failedRollback)
        {
            check(Get("verifiedSession") is false && stores.Catalog.Selected == accountA.Folder &&
                File.ReadAllText(Snapshot(accountA)) == "accountA" && File.ReadAllText(Snapshot(accountC)) == "accountC" &&
                ((string)Get("message")!).Contains("Native recovery failed:"),
                label + " keeps Play blocked and snapshots unchanged when rollback also fails verification");
            check(await Command(new { action = "ready", profile = (Guid?)null }) &&
                !await Command(new { action = "ready", profile = accountA.Profile, username = "cached-accountA" }) &&
                Get("verifiedSession") is false && Get("holdLogin") is true && Get("busy") is false &&
                Get("active") is NativeStore kept && kept.Folder == accountA.Folder && Get("pendingLogin") is null &&
                stores.Catalog.Selected == accountA.Folder && stores.Catalog.Stores.SequenceEqual(saved) &&
                File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json")).SequenceEqual(metadata) &&
                saved.All(s => File.ReadAllBytes(Snapshot(s)).SequenceEqual(snapshots[s.Folder])) &&
                !await session.AddAccount() && ReferenceEquals(Get("launcher"), owner) && session.IsOpen && owner.MainWindowHandle == window,
                label + " rejects late cached ready after signed-out UI refresh, preserving snapshots, selection, Play fence and launcher");
            rejectResume = false;
            await session.Open(accountC, true);
            check(File.ReadAllText(Snapshot(accountA)) == "accountA" && File.ReadAllText(Snapshot(accountB)) == "accountB",
                label + " never captures another account's resident session into a failed recovery snapshot");
        }
        var expected = scenario == "switch" ? accountA : accountC;
        check(resident == expected.Profile && Get("verifiedSession") is true && Get("active") is NativeStore active && active.Folder == expected.Folder &&
            stores.Catalog.Selected == expected.Folder && stores.Catalog.Active == expected.Folder &&
            File.ReadAllText(native) == expected.Label + "-refreshed" && File.ReadAllText(Snapshot(expected)) == expected.Label + "-refreshed",
            label + " commits only the verified resumed account and its refreshed snapshot");
        check(ReferenceEquals(Get("launcher"), owner) && session.IsOpen && owner.MainWindowHandle == window &&
            ReferenceEquals(Get("ready"), signal) && Get("busy") is false && Get("holdLogin") is false,
            label + " preserves the existing process, window and bridge without browser authentication");
    }

    private static async Task StorageRecovery(string root, Dispatcher dispatcher, bool fallback, bool metadata, Action<bool, string> check)
    {
        var stores = new NativeStores(root);
        var native = Path.Combine(root, "account.dat");
        NativeStore Account(string label)
        {
            var store = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), label);
            File.WriteAllText(native, label); stores.Capture(store);
            return store;
        }
        var accountB = fallback ? Account("accountB") : null;
        var accountA = Account("accountA");
        var snapshot = Path.Combine(stores.Folder(accountA.Folder), "AppData", "Hytale", "account.dat");
        var lockedPath = metadata ? Path.Combine(stores.Root, "native-stores.json") : snapshot;
        var preserved = File.ReadAllBytes(lockedPath);
        var session = new NativeSession(stores, dispatcher);
        using var owner = Process.GetCurrentProcess();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
        void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
        Task Invoke(string name, params object[] args) => (Task)typeof(NativeSession).GetMethod(name, flags)!.Invoke(session, args)!;
        Set("launcher", owner); Set("active", accountA); Set("verifiedSession", true);
        Guid? resident = accountA.Profile;
        var handled = new HashSet<string>();
        var revoked = new List<Guid?>();
        var operations = new List<string>();
        var status = "";
        session.Status += text =>
        {
            status = text;
            if (Get("logoutRequest") is string logout && handled.Add(logout))
            {
                revoked.Add(resident); resident = null; File.Delete(native);
                ((TaskCompletionSource<bool>)Get("logoutResult")!).TrySetResult(true);
            }
            if (Get("sessionRequest") is not string request || !handled.Add(request)) return;
            var operation = (string)Get("sessionOperation")!; operations.Add(operation);
            if (operation == "load")
            {
                var label = File.ReadAllText(native);
                resident = stores.Catalog.Stores.Single(s => s.Label == label).Profile;
            }
            if (operation == "clear") resident = null;
            ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(JsonSerializer.SerializeToElement(new
                { success = true, profile = resident?.ToString(), loggedIn = resident is not null }));
        };
        var scenario = (metadata ? "locked metadata" : "locked snapshot") + (fallback ? " with fallback" : " on final account");
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Invoke("LogoutCore", accountA);
            check(operations.Contains("stop") && revoked.SequenceEqual(new[] { accountA.Profile }) &&
                status.Contains("Logout storage update failed:") && status.Contains(lockedPath),
                scenario + " attempts recovery after confirmed logout and reports the storage failure");
            check(File.ReadAllBytes(lockedPath).SequenceEqual(preserved) && File.ReadAllText(snapshot) == "accountA" &&
                (accountB is null || File.ReadAllText(Path.Combine(stores.Folder(accountB.Folder), "AppData", "Hytale", "account.dat")) == "accountB"),
                scenario + " preserves locked recovery data and other account snapshots");
            if (metadata)
            {
                check(Get("verifiedSession") is false && !operations.Contains("run") && resident is null &&
                    !File.Exists(native) && status.Contains("Other accounts were preserved.") &&
                    status.Contains("Native logout succeeded, but account recovery did not complete.") &&
                    stores.Catalog.Selected == accountA.Folder && !stores.Catalog.Stores.Single(s => s.Folder == accountA.Folder).SignedOut &&
                    (!fallback || status.Contains("Could not restore accountB:")),
                    scenario + " keeps Play blocked when storage prevents every safe recovery");
            }
            else
            {
                check(fallback ? resident == accountB!.Profile && stores.Catalog.Selected == accountB.Folder && Get("verifiedSession") is true && !operations.Contains("clear") :
                    resident is null && stores.Catalog.Selected is null && Get("verifiedSession") is false && operations.Contains("clear") &&
                    Get("active") is NativeStore pending && stores.IsPending(pending),
                    scenario + " recovers to the saved account or native sign-in despite capture failure");
            }
        }
        if (metadata)
        {
            if (accountB is not null) await session.Open(accountB, true);
            else await Invoke("ShowSignIn");
            check(accountB is not null ? resident == accountB.Profile && stores.Catalog.Selected == accountB.Folder && Get("verifiedSession") is true :
                resident is null && Get("active") is NativeStore pending && stores.IsPending(pending) && Get("verifiedSession") is false,
                scenario + " safely recovers after releasing the metadata lock");
        }
        else
        {
            // Retry only signed-out bookkeeping; never capture the now-active fallback into A's snapshot.
            stores.MarkSignedOut(accountA);
            check(stores.Catalog.Stores.Single(s => s.Folder == accountA.Folder).SignedOut && File.ReadAllText(snapshot) == "accountA",
                scenario + " allows metadata retry without overwriting preserved recovery data");
        }
        check(ReferenceEquals(Get("launcher"), owner) && session.IsOpen && !owner.HasExited &&
            Get("busy") is false && Get("holdLogin") is false && revoked.SequenceEqual(new[] { accountA.Profile }),
            scenario + " retains the existing process and revokes no other account throughout recovery");
    }
}
