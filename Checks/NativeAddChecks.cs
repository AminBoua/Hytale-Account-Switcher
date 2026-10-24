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
            session.Status += _ =>
            {
                if (Get("sessionRequest") is string request && handled.Add(request))
                {
                    var operation = (string)Get("sessionOperation")!; operations.Add(operation);
                    var isOther = File.ReadAllText(officialSession) == "opaque-accountA-session";
                    ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(JsonSerializer.SerializeToElement(new
                    {
                        success = operation != "load" || !failLoad, profile = isOther ? other.Profile : oldProfile,
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
            var snapshot = Path.Combine(stores.Folder(other.Folder), "AppData", "Hytale", "account.dat");
            File.WriteAllText(officialSession, "opaque-refreshed-accountA-session");
            using (var locked = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { await Command(completion); throw new Exception("Locked snapshot completion succeeded"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    check(Pending() == retry && !stores.IsPending((NativeStore)Get("pendingLogin")!) &&
                        ((NativeStore)Get("active")!).Folder == other.Folder && stores.Catalog.Selected == other.Folder &&
                        stores.Catalog.Active == current.Folder && stores.Catalog.Stores.Count == 2 &&
                        File.ReadAllText(snapshot) == "opaque-accountA-session" &&
                        File.ReadAllText(officialSession) == "opaque-refreshed-accountA-session" &&
                        session.IsOpen && ReferenceEquals(Get("launcher"), process),
                        "failed snapshot preserves resolved identity, login/Play fence, native session and launcher ownership");
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
}
