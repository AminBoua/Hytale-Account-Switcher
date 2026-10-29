using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
                        await UnverifiedLogout(Path.Combine(root, "unverified-logout"), dispatcher, check);
                        foreach (var scenario in new[] { "transient", "persistent", "failed" })
                            await Confirmation(Path.Combine(root, "confirmation-" + scenario), dispatcher, scenario, check);
                        foreach (var failure in new[] { "stop", "clear", "run", "authenticated refresh" })
                            await ConfirmedRecoveryFence(Path.Combine(root, "confirmed-recovery-" + failure), dispatcher, failure, check);
                        foreach (var failure in new[] { "stop", "clear", "run", "authenticated refresh", "stop deadline", "clear deadline", "run deadline", "helper deadline" })
                            await FinalSignInRetry(Path.Combine(root, "final-signin-" + failure), dispatcher, failure, check);
                        foreach (var mismatch in new[] { false, true })
                            foreach (var scenario in new[] { "switch", "failed rollback", "failed precheck rollback", "deadline rollback", "logout" })
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

    private static async Task UnverifiedLogout(string root, Dispatcher dispatcher, Action<bool, string> check)
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
        using var owner = Process.GetCurrentProcess();
        var pid = owner.Id;
        var window = owner.MainWindowHandle;
        var session = new NativeSession(stores, dispatcher);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
        void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
        Set("launcher", owner); Set("active", accountA); Set("verifiedSession", true);
        var signal = new TaskCompletionSource<Guid?>(); Set("ready", signal);
        Task<bool> Command(object data) => (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!
            .Invoke(session, [JsonSerializer.SerializeToElement(data), signal])!;
        Task Flush() => dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task.WaitAsync(TimeSpan.FromSeconds(5));
        Guid? resident = accountA.Profile;
        var handled = new HashSet<string>();
        var operations = new List<string>();
        var revoked = new List<Guid?>();
        var logoutRequests = 0;
        var failRecovery = true;
        session.Status += _ =>
        {
            if (Get("logoutRequest") is string logout && handled.Add(logout))
            {
                logoutRequests++; revoked.Add(resident); resident = null; File.Delete(native);
                ((TaskCompletionSource<bool>)Get("logoutResult")!).TrySetResult(true);
            }
            if (Get("sessionRequest") is not string request || !handled.Add(request)) return;
            var operation = (string)Get("sessionOperation")!; operations.Add(operation);
            if (operation == "load") resident = stores.Catalog.Stores.Single(s => s.Label == File.ReadAllText(native)).Profile;
            if (operation == "clear") resident = null;
            // B stays resident when its resume and the shutdown needed to restore A both fail.
            var success = !(failRecovery && resident == accountB.Profile && operation is "run" or "stop");
            ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(JsonSerializer.SerializeToElement(new
                { success, profile = resident, loggedIn = resident is not null }));
        };
        check(await Command(new { action = "select", store = accountB.Folder }), "verified A accepts a switch to saved B");
        await Flush();
        check(operations.SequenceEqual(new[] { "stop", "load", "run", "stop" }) && resident == accountB.Profile &&
            File.ReadAllText(native) == accountB.Label && Get("active") is NativeStore active && active.Folder == accountA.Folder &&
            Get("verifiedSession") is false && Get("holdLogin") is true && Get("busy") is false,
            "failed B resume and failed A recovery leave native B resident while recorded A remains unverified");
        var saved = stores.Catalog.Stores.ToArray();
        var metadata = File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json"));
        var snapshots = saved.ToDictionary(s => s.Folder, s => File.ReadAllBytes(Snapshot(s)));
        var operationCount = operations.Count;
        var accepted = await Command(new { action = "logout", store = accountA.Folder });
        await Flush();
        check(!accepted, "unverified logout is rejected through the normal command path");
        await (Task)typeof(NativeSession).GetMethod("LogoutCore", flags)!.Invoke(session, [accountA])!;
        check(logoutRequests == 0 && revoked.Count == 0 && Get("logoutRequest") is null && Get("logoutResult") is null &&
            operations.Count == operationCount && resident == accountB.Profile && File.ReadAllText(native) == accountB.Label,
            "unverified logout never requests native revocation or session recovery, including at dispatch");
        check(stores.Catalog.Stores.SequenceEqual(saved) && stores.Catalog.Selected == accountA.Folder && stores.Catalog.Active == accountB.Folder &&
            File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json")).SequenceEqual(metadata) &&
            saved.All(s => File.ReadAllBytes(Snapshot(s)).SequenceEqual(snapshots[s.Folder])) &&
            Get("verifiedSession") is false && Get("holdLogin") is true && Get("busy") is false,
            "rejected logout preserves all account metadata, snapshots and the recovery fence");
        check(session.IsOpen && ReferenceEquals(Get("launcher"), owner) && !owner.HasExited && owner.Id == pid &&
            owner.MainWindowHandle == window && ReferenceEquals(Get("ready"), signal),
            "rejected logout preserves launcher process, window and bridge ownership");
        failRecovery = false;
        check(await Command(new { action = "select", store = accountA.Folder }), "saved-account selection remains available after rejected logout");
        await Flush();
        check(resident == accountA.Profile && Get("verifiedSession") is true && Get("holdLogin") is false && Get("busy") is false &&
            stores.Catalog.Selected == accountA.Folder && saved.All(s => File.ReadAllBytes(Snapshot(s)).SequenceEqual(snapshots[s.Folder])),
            "selecting recorded A again verifies native recovery without corrupting saved snapshots");
        check(await Command(new { action = "logout", store = accountA.Folder }), "verified logout is accepted after recovery");
        await Flush();
        check(logoutRequests == 1 && revoked.SequenceEqual(new[] { accountA.Profile }) && resident == accountB.Profile &&
            stores.Catalog.Stores.Single(s => s.Folder == accountA.Folder).SignedOut && !File.Exists(Snapshot(accountA)) &&
            stores.Catalog.Selected == accountB.Folder && Get("verifiedSession") is true && !operations.Contains("clear"),
            "verified logout revokes only A and restores saved B in the existing launcher");
        check(await Command(new { action = "select", store = accountC.Folder }), "saved-account selection still works after verified logout fallback");
        await Flush();
        check(resident == accountC.Profile && Get("verifiedSession") is true && Get("holdLogin") is false && Get("busy") is false &&
            logoutRequests == 1 && stores.Catalog.Selected == accountC.Folder && session.IsOpen && ReferenceEquals(Get("launcher"), owner) &&
            owner.Id == pid && owner.MainWindowHandle == window && ReferenceEquals(Get("ready"), signal),
            "subsequent saved-account recovery preserves launcher ownership without repeating logout");
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

    private static async Task ConfirmedRecoveryFence(string root, Dispatcher dispatcher, string failure, Action<bool, string> check)
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
        var unrelated = Account("unrelated");
        stores.MarkSignedOut(unrelated);
        var accountA = Account("accountA");
        stores.Activate(accountA); stores.Select(accountA);
        string Snapshot(NativeStore store) => Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat");
        var snapshots = new[] { accountB, unrelated }.ToDictionary(s => s.Folder, s => File.ReadAllBytes(Snapshot(s)));
        using var owner = Process.GetCurrentProcess();
        var window = owner.MainWindowHandle;
        var session = new NativeSession(stores, dispatcher);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
        void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
        Task Invoke(string name, params object[] args) => (Task)typeof(NativeSession).GetMethod(name, flags)!.Invoke(session, args)!;
        var signal = new TaskCompletionSource<Guid?>();
        Set("launcher", owner); Set("active", accountA); Set("verifiedSession", true); Set("ready", signal);
        Task<bool> Command(object data) => (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!
            .Invoke(session, [JsonSerializer.SerializeToElement(data), signal])!;
        Guid? resident = accountA.Profile;
        var requests = new Dictionary<string, string>();
        var operations = new List<string>();
        var revoked = new List<Guid?>();
        var failRecovery = true;
        var status = "";
        session.Status += text =>
        {
            status = text;
            if (Get("logoutRequest") is string logout && requests.TryAdd(logout, "logout"))
            {
                revoked.Add(resident); resident = null; File.Delete(native);
                ((TaskCompletionSource<bool>)Get("logoutResult")!).TrySetResult(true);
            }
            if (Get("sessionRequest") is not string request || !requests.TryAdd(request, (string)Get("sessionOperation")!)) return;
            var operation = requests[request]; operations.Add(operation);
            if (operation == "load")
            {
                // Native load can replace the resident session before reporting failure.
                resident = accountB.Profile;
                if (!failRecovery) File.WriteAllText(native, "accountB-refreshed");
            }
            if (operation == "clear" && (!failRecovery || failure != "clear")) resident = null;
            if (failRecovery && operation == "run" && failure == "authenticated refresh")
            {
                resident = accountB.Profile; File.WriteAllText(native, "accountB");
            }
            var success = !failRecovery || operation switch
            {
                "load" => false,
                "stop" => failure != "stop" || !operations.Contains("load"),
                "clear" => failure != "clear",
                "run" => failure != "run",
                _ => true
            };
            ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(JsonSerializer.SerializeToElement(new
                { success, profile = resident?.ToString(), loggedIn = resident is not null }));
        };
        var label = "confirmed logout with failed load and sign-in " + failure;
        await Invoke("LogoutCore", accountA);
        check(Get("holdLogin") is true && Get("verifiedSession") is false && Get("busy") is false &&
            stores.Catalog.Selected is null && stores.Catalog.Stores.Single(s => s.Folder == accountA.Folder).SignedOut &&
            status.Contains("Native logout succeeded, but account recovery did not complete.") && operations.Contains("load"),
            label + " keeps the recovery fence and Play block after revocation succeeds");
        var saved = stores.Catalog.Stores.ToArray();
        var metadata = File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json"));
        var active = Get("active");
        foreach (var profile in new[] { accountA.Profile, accountB.Profile })
            check(!await Command(new { action = "ready", profile, username = "cached" }),
                label + " rejects cached authenticated readiness before signed-out refresh");
        foreach (var request in requests)
            check(!await Command(new { action = request.Value == "logout" ? "logout-result" : "session-result", request = request.Key,
                success = true, loggedIn = true, profile = accountA.Profile }),
                label + " rejects late completion of " + request.Value);
        check(await Command(new { action = "ready", profile = (Guid?)null }) &&
            !await Command(new { action = "ready", profile = accountA.Profile }) &&
            Get("holdLogin") is true && Get("verifiedSession") is false && ReferenceEquals(Get("active"), active) &&
            stores.Catalog.Stores.SequenceEqual(saved) && File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json")).SequenceEqual(metadata) &&
            snapshots.All(s => File.ReadAllBytes(Snapshot(saved.Single(a => a.Folder == s.Key))).SequenceEqual(s.Value)) &&
            !File.Exists(Snapshot(accountA)) && !await session.AddAccount(),
            label + " fences late readiness after signed-out UI reports without capturing B into A or changing unrelated snapshots");
        failRecovery = false;
        if (failure == "stop")
        {
            await session.Open(accountB, true);
            check(Get("holdLogin") is false && Get("verifiedSession") is true && stores.Catalog.Selected == accountB.Folder &&
                resident == accountB.Profile && File.ReadAllText(Snapshot(accountB)) == "accountB-refreshed" && !File.Exists(Snapshot(accountA)),
                label + " releases the fence after retry verifies and captures only the expected account");
        }
        else
        {
            check(await Command(new { action = "retry-signin", request = Get("signInRecovery") }), label + " accepts the frontend sign-in retry");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(Get("holdLogin") is false && Get("verifiedSession") is false && resident is null &&
                Get("active") is NativeStore pending && stores.IsPending(pending) && operations.Last() == "run" &&
                await Command(new { action = "ready", profile = (Guid?)null }),
                label + " releases the fence only after a successful signed-out UI refresh");
            var signedIn = Guid.NewGuid(); File.WriteAllText(native, "new-native-session");
            check(await Command(new { action = "ready", profile = signedIn, username = "new account" }) &&
                Get("verifiedSession") is true && stores.Catalog.Stores.Single(s => s.Folder == stores.Catalog.Selected).Profile == signedIn &&
                !File.Exists(Snapshot(accountA)) && File.ReadAllBytes(Snapshot(accountB)).SequenceEqual(snapshots[accountB.Folder]),
                label + " accepts subsequent native authentication without reusing a saved account's snapshot");
        }
        check(ReferenceEquals(Get("launcher"), owner) && session.IsOpen && owner.MainWindowHandle == window &&
            ReferenceEquals(Get("ready"), signal) && revoked.SequenceEqual(new[] { accountA.Profile }) &&
            File.ReadAllBytes(Snapshot(unrelated)).SequenceEqual(snapshots[unrelated.Folder]),
            label + " retains the launcher process, window and unrelated snapshot without repeating logout");
    }

    private static async Task FinalSignInRetry(string root, Dispatcher dispatcher, string failure, Action<bool, string> check)
    {
        var stores = new NativeStores(root);
        var native = Path.Combine(root, "account.dat");
        NativeStore Account(string label)
        {
            var store = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), label);
            File.WriteAllText(native, label); stores.Capture(store);
            return store;
        }
        var unrelated = Account("unrelated"); stores.MarkSignedOut(unrelated);
        var account = Account("final account"); stores.Activate(account); stores.Select(account);
        string Snapshot(NativeStore store) => Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat");
        var preserved = File.ReadAllBytes(Snapshot(unrelated));
        var session = new NativeSession(stores, dispatcher);
        using var owner = Process.GetCurrentProcess();
        var window = owner.MainWindowHandle;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
        void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
        var signal = new TaskCompletionSource<Guid?>();
        Set("launcher", owner); Set("active", account); Set("verifiedSession", true); Set("ready", signal);
        await using var bridge = new LocalAccountBridge(data => (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!
            .Invoke(session, [data, signal])!);
        Set("bridge", bridge);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Add("Origin", LocalAccountBridge.Origin);
        http.DefaultRequestHeaders.Add("Authorization", "Bearer " + bridge.Capability);
        async Task<bool> Command(object data)
        {
            using var body = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(bridge.Endpoint + "command", body);
            if (response.StatusCode == HttpStatusCode.BadRequest) return false;
            response.EnsureSuccessStatusCode();
            return true;
        }
        async Task<JsonElement> State()
        {
            using var json = JsonDocument.Parse(await http.GetStringAsync(bridge.Endpoint + "state"));
            return json.RootElement.Clone();
        }
        async Task Until(Func<bool> condition)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(string _) { if (condition()) reached.TrySetResult(); }
            session.Status += Changed;
            try { if (!condition()) await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { session.Status -= Changed; }
        }
        var operations = new List<string>();
        var requests = new List<string>();
        var logouts = new HashSet<string>();
        session.Status += _ =>
        {
            if (Get("logoutRequest") is string logout) logouts.Add(logout);
            if (Get("sessionRequest") is string request && !requests.Contains(request))
            { requests.Add(request); operations.Add((string)Get("sessionOperation")!); }
        };
        async Task Reply(string operation, bool? success = true, bool loggedIn = false)
        {
            check((string?)Get("sessionOperation") == operation, "final sign-in " + failure + " requests " + operation);
            var request = (string)Get("sessionRequest")!;
            check(await Command(new { action = "session-result", request, success, loggedIn }), "current native result is accepted");
            await Until(() => (string?)Get("sessionRequest") != request && (Get("sessionRequest") is not null || Get("busy") is false));
        }
        var label = "final sign-in " + failure;
        check(await Command(new { action = "logout", store = account.Folder }), label + " starts through the frontend logout command");
        await Until(() => Get("logoutRequest") is not null);
        var logoutRequest = (string)Get("logoutRequest")!;
        File.Delete(native);
        check(await Command(new { action = "logout-result", request = logoutRequest, success = true }), label + " confirms native logout exactly once");
        await Until(() => Get("sessionRequest") is not null);
        var fault = failure.StartsWith("stop") ? "stop" : failure.StartsWith("clear") ? "clear" : "run";
        foreach (var operation in new[] { "stop", "clear", "run" })
        {
            if (operation != fault) { await Reply(operation); continue; }
            if (failure == "helper deadline")
            {
                var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Changed(string _) { if (Get("busy") is false) idle.TrySetResult(); }
                session.Status += Changed;
                try { await idle.Task.WaitAsync(TimeSpan.FromSeconds(35)); }
                finally { session.Status -= Changed; }
            }
            else await Reply(operation, failure.EndsWith("deadline") ? null : failure == "authenticated refresh", failure == "authenticated refresh");
            break;
        }
        check(Get("holdLogin") is true && Get("verifiedSession") is false && Get("busy") is false && Get("signInRecovery") is string &&
            stores.Catalog.Stores.All(s => s.SignedOut) && stores.Catalog.Selected is null && !await session.AddAccount(),
            label + " advertises retryable confirmed recovery with no usable account and retains the fence");
        var saved = stores.Catalog.Stores.ToArray();
        var metadata = File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json"));
        var recoveryStore = Get("active") as NativeStore;
        var beforeReady = operations.Count;
        for (var i = 0; i < 3; i++)
        {
            check(await Command(new { action = "ready", profile = (Guid?)null }) &&
                !await Command(new { action = "ready", profile = account.Profile }), label + " refuses cached readiness as recovery proof");
        }
        check(operations.Count == beforeReady && Get("holdLogin") is true && Get("verifiedSession") is false &&
            stores.Catalog.Stores.SequenceEqual(saved) && File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json")).SequenceEqual(metadata),
            label + " leaves the fence and account identities unchanged during readiness reports");
        check(!await Command(new { action = "logout", store = account.Folder }) &&
            !await Command(new { action = "logout-result", request = logoutRequest, success = true }), label + " cannot repeat completed logout");
        foreach (var request in requests.ToArray())
            check(!await Command(new { action = "session-result", request, success = true, loggedIn = false }), label + " rejects late native completion");
        var state = await State();
        var firstRetry = state.GetProperty("signInRecovery").GetString()!;
        check(state.GetProperty("holdLogin").GetBoolean() && !state.GetProperty("busy").GetBoolean() &&
            state.GetProperty("profile").ValueKind == JsonValueKind.Null, label + " advertises fenced recovery over the normal helper state endpoint");
        check(await Command(new { action = "retry-signin", request = firstRetry }) &&
            !await Command(new { action = "retry-signin", request = firstRetry }), label + " serializes duplicate frontend retries");
        await Until(() => Get("sessionRequest") is not null);
        await Reply("stop", false);
        check(Get("holdLogin") is true && Get("busy") is false &&
            !await Command(new { action = "retry-signin", request = firstRetry }), label + " keeps ordinary retry failure fenced and consumes its request");
        state = await State();
        check(await Command(new { action = "retry-signin", request = state.GetProperty("signInRecovery").GetString() }), label + " retries again through the frontend exchange");
        await Until(() => Get("sessionRequest") is not null);
        await Reply("stop"); await Reply("clear");
        check(await Command(new { action = "ready", profile = (Guid?)null }) && Get("holdLogin") is true && Get("busy") is true &&
            !await session.AddAccount(), label + " waits for fresh signed-out refresh even after native clear succeeds");
        await Reply("run");
        state = await State();
        check(Get("holdLogin") is false && Get("busy") is false && Get("verifiedSession") is false && Get("signInRecovery") is null &&
            !state.GetProperty("holdLogin").GetBoolean() && state.GetProperty("signInRecovery").ValueKind == JsonValueKind.Null &&
            Get("active") is NativeStore pending && stores.IsPending(pending) && operations.TakeLast(3).SequenceEqual(new[] { "stop", "clear", "run" }) &&
            !await Command(new { action = "retry-signin", request = firstRetry }), label + " releases recovery only after fresh signed-out refresh");
        check(logouts.SetEquals(new[] { logoutRequest }) && ReferenceEquals(Get("launcher"), owner) && session.IsOpen && owner.MainWindowHandle == window &&
            ReferenceEquals(Get("ready"), signal) && stores.Catalog.Stores.SequenceEqual(saved) && !File.Exists(Snapshot(account)) &&
            (recoveryStore is null || !stores.IsPending(recoveryStore) || ((NativeStore)Get("active")!).Folder == recoveryStore.Folder) &&
            File.ReadAllBytes(Snapshot(unrelated)).SequenceEqual(preserved), label + " preserves process, window, snapshots and identities with exactly one logout");
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
        var failedRollback = scenario is "failed rollback" or "failed precheck rollback" or "deadline rollback";
        var timedOutRequests = new List<string>();
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
            var timedOut = scenario == "deadline rollback" && rejectResume && operation == "run";
            if (timedOut) timedOutRequests.Add(request);
            ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(JsonSerializer.SerializeToElement(new
                { success = timedOut ? (bool?)null : true, profile = resident?.ToString(), loggedIn = resident is not null, username = loaded?.Label }));
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
            foreach (var request in timedOutRequests)
                check(!await Command(new { action = "session-result", request, success = true, loggedIn = true, profile = accountA.Profile }),
                    label + " rejects late successful completion of a withdrawn frontend deadline");
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
        var signal = new TaskCompletionSource<Guid?>(); Set("ready", signal);
        Task<bool> Command(object data) => (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!
            .Invoke(session, [JsonSerializer.SerializeToElement(data), signal])!;
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
            else
            {
                check(await Command(new { action = "retry-signin", request = Get("signInRecovery") }), scenario + " accepts the frontend retry after unlocking storage");
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
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
