using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using HytaleAccounts;

internal static class NativeAddChecks
{
    public static Task Run(Action<bool, string> check)
    {
        NativeStores.RequireIdle();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await RunCore(check); complete.TrySetResult(); }
                catch (Exception ex) { complete.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return complete.Task;
    }
    private static async Task RunCore(Action<bool, string> check)
    {
        await SavedFallback(check);
        var root = SecurityFixture.Create("native-add-");
        var log = Path.Combine(root, "hytale-accounts.log");
        try
        {
            App.LogStatus(root, "writable log");
            check(File.ReadAllText(log).Contains("writable log"), "status logging still writes when available");
            using (var locked = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read))
                App.LogStatus(root, "locked log");
            check(!File.ReadAllText(log).Contains("locked log"), "locked status logging is best effort");
            File.SetAttributes(log, FileAttributes.ReadOnly);
            try { File.AppendAllText(log, "denied"); throw new Exception("Read-only log fixture accepted a write"); }
            catch (UnauthorizedAccessException) { }
            var stores = new NativeStores(root);
            var other = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), "accountA");
            var officialSession = Path.Combine(root, "account.dat");
            File.WriteAllText(officialSession, "opaque-accountA-session"); stores.Capture(other);
            var oldProfile = Guid.NewGuid();
            var current = stores.CommitPending(stores.CreatePending(), oldProfile, "accountB");
            File.WriteAllText(officialSession, "opaque-accountB-session"); stores.Capture(current);
            using var process = Process.GetCurrentProcess();
            var session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
            session.Status += text => App.LogStatus(stores.HytaleRoot, text);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
            object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
            var signal = new TaskCompletionSource<Guid?>(); Set("ready", signal); Set("nativeName", "accountB");
            Task<bool> Command(object data) => (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!
                .Invoke(session, new object[] { JsonSerializer.SerializeToElement(data), signal })!;
            string Pending() => ((NativeStore)Get("pendingLogin")!).Folder;
            Set("launcher", process); Set("active", current); Set("verifiedSession", true);
            check(await session.AddAccount(), "Add progresses while status logging is denied");
            check(session.IsOpen && !process.HasExited && stores.Catalog.Selected == current.Folder && stores.Catalog.Active == current.Folder &&
                File.ReadAllText(officialSession) == "opaque-accountB-session", "Add preserves the process, native session and selected account without restart");
            check(stores.Catalog.Stores.Count == 2 && !await session.AddAccount(), "pending browser login saves no duplicate account and rejects a second Add");
            var first = Pending();
            var metadata = File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json"));
            check(!await Command(new { action = "ready", profile = oldProfile, username = "wrong-name" }) &&
                !await Command(new { action = "cancel" }) && !await Command(new { action = "login-failed" }) &&
                (string?)Get("nativeName") == "accountB" && !signal.Task.IsCompleted,
                "untagged login reports cannot change identity, cancel Add or complete startup");
            var operations = new List<string>();
            var handled = new HashSet<string>();
            var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failLoad = false;
            var mismatchLoad = false;
            session.Status += _ =>
            {
                if (Get("sessionRequest") is string request && handled.Add(request))
                {
                    var operation = (string)Get("sessionOperation")!; operations.Add(operation);
                    var isOther = File.ReadAllText(officialSession) is "opaque-accountA-session" or "opaque-refreshed-accountA-session";
                    ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(JsonSerializer.SerializeToElement(new
                    {
                        success = operation != "load" || !failLoad,
                        profile = operation == "load" && mismatchLoad && isOther ? Guid.NewGuid() : isOther ? other.Profile : oldProfile,
                        username = isOther ? "accountA" : "accountB", loggedIn = true
                    }));
                }
                if (Get("busy") is false) restored.TrySetResult();
            };
            // Simulate Hytale writing a callback while Cancel is being clicked.
            File.WriteAllText(officialSession, "opaque-late-browser-session");
            check(await Command(new { action = "cancel", loginRequest = first }) && Get("pendingLogin") is null &&
                Get("busy") is true && Get("verifiedSession") is false &&
                !await Command(new { action = "select", store = other.Folder }),
                "cancel clears the attempt immediately and blocks Play/switching until native recovery verifies");
            check(!await Command(new { action = "ready", loginRequest = first, profile = Guid.NewGuid(), username = "late" }),
                "a completion arriving after cancellation is rejected");
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(session.IsOpen && !process.HasExited && Get("verifiedSession") is true && Get("holdLogin") is false &&
                stores.Catalog.Selected == current.Folder && stores.Catalog.Active == current.Folder &&
                File.ReadAllText(officialSession) == "opaque-accountB-session" &&
                File.ReadAllText(Path.Combine(stores.Folder(current.Folder), "AppData", "Hytale", "account.dat")) == "opaque-accountB-session" &&
                File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json")).SequenceEqual(metadata) &&
                operations.SequenceEqual(new[] { "stop", "load", "run" }),
                "cancel restores the original native session in the same process without poisoning its snapshot or saved selection");
            check(await session.AddAccount(), "Add is usable again after cancellation");
            var second = Pending();
            check(second != first && !await Command(new { action = "cancel", loginRequest = first }) &&
                !await Command(new { action = "login-failed", loginRequest = first }) &&
                !await Command(new { action = "ready", loginRequest = first, profile = Guid.NewGuid(), username = "late" }) &&
                Pending() == second && (string?)Get("nativeName") == "accountB",
                "late cancellation, failure and completion cannot affect a newer login attempt");
            check(await Command(new { action = "ready", loginRequest = second, profile = oldProfile, username = "accountB" }) &&
                Get("pendingLogin") is null && stores.Catalog.Selected == current.Folder && stores.Catalog.Stores.Count == 2,
                "a matching same-account completion clears pending login without duplicating the saved account");
            check(await session.AddAccount(), "a saved account can be re-added after completion");
            var retry = Pending();
            var completion = new { action = "ready", loginRequest = retry, profile = other.Profile, username = "accountA" };
            var previousMetadata = File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json"));
            var snapshot = Path.Combine(stores.Folder(other.Folder), "AppData", "Hytale", "account.dat");
            foreach (var missing in new[] { false, true })
            {
                if (missing) File.Delete(officialSession); else File.WriteAllText(officialSession, "");
                try { await Command(completion); throw new Exception("Missing or empty login completion succeeded"); }
                catch (IOException) { }
                stores.CleanupPending();
                check(Pending() == retry && stores.IsPending((NativeStore)Get("pendingLogin")!) &&
                    Get("verifiedSession") is false && ((NativeStore)Get("active")!).Folder == current.Folder &&
                    stores.Catalog.Selected == current.Folder && stores.Catalog.Active == current.Folder && stores.Catalog.Stores.Count == 2 &&
                    File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json")).SequenceEqual(previousMetadata) &&
                    File.ReadAllText(snapshot) == "opaque-accountA-session" &&
                    session.IsOpen && ReferenceEquals(Get("launcher"), process),
                    $"{(missing ? "missing" : "empty")} login completion preserves the prior selection, snapshot, retry fence and launcher while blocking Play");
            }
            File.WriteAllText(officialSession, "opaque-refreshed-accountA-session");
            using (var locked = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { await Command(completion); throw new Exception("Locked snapshot completion succeeded"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    check(Pending() == retry && stores.IsPending((NativeStore)Get("pendingLogin")!) &&
                        ((NativeStore)Get("active")!).Folder == current.Folder && stores.Catalog.Selected == current.Folder &&
                        Get("verifiedSession") is false && File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json")).SequenceEqual(previousMetadata) &&
                        stores.Catalog.Active == current.Folder && stores.Catalog.Stores.Count == 2 &&
                        File.ReadAllText(snapshot) == "opaque-accountA-session" &&
                        File.ReadAllText(officialSession) == "opaque-refreshed-accountA-session" &&
                        session.IsOpen && ReferenceEquals(Get("launcher"), process),
                        "failed snapshot preserves prior identity and selection, login/Play fence, native session and launcher ownership");
                }
            }
            check(!await Command(new { action = "ready", loginRequest = second, profile = oldProfile, username = "late" }) &&
                !await Command(new { action = "cancel", loginRequest = second }) &&
                !await Command(new { action = "login-failed", loginRequest = second }) && Pending() == retry &&
                !await Command(new { action = "select", store = current.Folder }),
                "failed completion retains its request fence against late callbacks and switching");
            check(await Command(completion) && Get("pendingLogin") is null && Get("verifiedSession") is true &&
                ((NativeStore)Get("active")!).Folder == other.Folder && stores.Catalog.Selected == other.Folder &&
                stores.Catalog.Active == other.Folder && stores.Catalog.Stores.Count == 2 &&
                File.ReadAllText(snapshot) == "opaque-refreshed-accountA-session" &&
                File.ReadAllText(officialSession) == "opaque-refreshed-accountA-session" &&
                session.IsOpen && ReferenceEquals(Get("launcher"), process),
                "the same completion retries successfully after releasing the snapshot lock without restart or duplicate accounts");
            check(!await Command(completion), "a late duplicate completion is rejected after the successful retry");
            restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
            check(await Command(new { action = "select", store = current.Folder }), "switching is accepted after cancellation and completion");
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(session.IsOpen && Get("verifiedSession") is true && stores.Catalog.Selected == current.Folder &&
                File.ReadAllText(officialSession) == "opaque-accountB-session", "switching still restores another saved account in the existing process");
            mismatchLoad = true; restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
            check(await Command(new { action = "select", store = other.Folder }), "saved-account switch begins native verification");
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(session.IsOpen && ReferenceEquals(Get("launcher"), process) && Get("verifiedSession") is true &&
                Get("active") is NativeStore kept && kept.Folder == current.Folder && stores.Catalog.Selected == current.Folder &&
                stores.Catalog.Active == current.Folder && Get("pendingLogin") is null &&
                File.ReadAllText(officialSession) == "opaque-accountB-session" && File.ReadAllText(snapshot) == "opaque-refreshed-accountA-session" &&
                ((string)Get("message")!).Contains("Native profile does not match the selected account."),
                "mismatched saved-account switch rejects the profile and restores the previous session without rebinding either account or restarting");
            mismatchLoad = false;
            check(await session.AddAccount(), "another Add can follow switching");
            var third = Pending();
            failLoad = true; restored = new(TaskCreationOptions.RunContinuationsAsynchronously);
            check(await Command(new { action = "login-failed", loginRequest = third }), "matching native login failure uses the cancellation recovery path");
            await restored.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(Get("pendingLogin") is null && Get("busy") is false && Get("holdLogin") is false &&
                Get("verifiedSession") is false && stores.Catalog.Selected == current.Folder && session.IsOpen &&
                !await session.AddAccount(), "failed cancellation recovery keeps Play unverified, preserves selection and leaves the process open");
            var failure = new UnauthorizedAccessException("synthetic session failure");
            session.Status += _ => throw failure;
            try { typeof(NativeSession).GetMethod("Publish", flags)!.Invoke(session, null); throw new Exception("Session failure was swallowed"); }
            catch (TargetInvocationException ex) when (ReferenceEquals(ex.InnerException, failure))
            { check(true, "best-effort logging does not swallow unrelated session errors"); }
        }
        finally
        {
            if (File.Exists(log)) File.SetAttributes(log, FileAttributes.Normal);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task SavedFallback(Action<bool, string> check)
    {
        foreach (var login in new[] { "new-B", "saved-B", "saved-A" })
        foreach (var starting in new[] { true, false })
        {
            var root = SecurityFixture.Create("native-saved-fallback-");
            try
            {
                var stores = new NativeStores(root);
                var official = Path.Combine(root, "account.dat");
                string Snapshot(NativeStore store) => Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat");
                var profileB = Guid.NewGuid();
                NativeStore? accountB = null;
                if (login == "saved-B")
                {
                    accountB = stores.CommitPending(stores.CreatePending(), profileB, "accountB");
                    File.WriteAllText(official, "opaque-old-B"); stores.Capture(accountB);
                }
                var accountA = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), "accountA");
                File.WriteAllText(official, "opaque-A"); stores.Capture(accountA);
                File.Delete(official);
                var session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
                var initial = session.InitialStore();
                stores.Activate(initial);
                check(initial == accountA && File.ReadAllText(official) == "opaque-A", "missing official session restores saved fallback A");
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
                object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
                var signal = new TaskCompletionSource<Guid?>(); Set("ready", signal);
                Task<bool> Command(object data) => (Task<bool>)typeof(NativeSession).GetMethod("Command", flags)!
                    .Invoke(session, [JsonSerializer.SerializeToElement(data), signal])!;
                using var process = Process.GetCurrentProcess();
                Set("launcher", process); Set("active", initial); Set("busy", starting);
                Set("holdLogin", true);
                check(await Command(new { action = "ready", profile = (Guid?)null }) && Equals(Get("active"), initial),
                    "signed-out reports during a saved-account transition keep strict store identity");
                Set("holdLogin", false); Set("verifiedSession", !starting);
                signal = new TaskCompletionSource<Guid?>(); Set("ready", signal);
                File.Delete(official); // Native rejected the restored opaque session and displayed sign-in.
                check(await Command(new { action = "ready", profile = (Guid?)null }) &&
                    Get("active") is NativeStore pending && stores.IsPending(pending) && Get("pendingLogin") is null &&
                    Get("verifiedSession") is false && signal.Task.IsCompletedSuccessfully && signal.Task.Result is null &&
                    stores.Catalog.Selected == accountA.Folder && stores.Catalog.Stores.Single(s => s.Folder == accountA.Folder) == accountA &&
                    File.ReadAllText(Snapshot(accountA)) == "opaque-A" && (accountB is null || File.ReadAllText(Snapshot(accountB)) == "opaque-old-B"),
                    $"native signed-out fallback becomes pending without changing saved accounts or opening Add login (startup: {starting})");
                var staged = (NativeStore)Get("active")!;
                check(await Command(new { action = "ready", profile = (Guid?)null }) && Equals(Get("active"), staged),
                    "repeated native sign-in reports reuse the same pending store");
                Set("busy", false);
                var profile = login == "saved-A" ? accountA.Profile!.Value : profileB;
                File.WriteAllText(official, "opaque-new-login");
                check(await Command(new { action = "ready", profile, username = login == "saved-A" ? "accountA" : "accountB" }),
                    "native login after saved fallback is accepted");
                var reloaded = new NativeStores(root);
                var selected = reloaded.Catalog.Stores.Single(s => s.Folder == reloaded.Catalog.Selected);
                check(selected.Profile == profile && reloaded.Catalog.Active == selected.Folder && Get("verifiedSession") is true &&
                    Get("active") is NativeStore active && active.Folder == selected.Folder &&
                    reloaded.Catalog.Stores.Count == (login == "saved-A" ? 1 : 2) &&
                    (login == "saved-A" ? selected.Folder == accountA.Folder : accountB is null || selected.Folder == accountB.Folder) &&
                    reloaded.Catalog.Stores.Single(s => s.Folder == accountA.Folder) == accountA &&
                    File.ReadAllText(Snapshot(accountA)) == (login == "saved-A" ? "opaque-new-login" : "opaque-A") &&
                    File.ReadAllText(Snapshot(selected)) == "opaque-new-login" && File.ReadAllText(official) == "opaque-new-login" &&
                    session.IsOpen && ReferenceEquals(Get("launcher"), process) && !process.HasExited,
                    $"{login} is saved or reused and selected after native sign-in, preserving A's identity and the launcher process");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
