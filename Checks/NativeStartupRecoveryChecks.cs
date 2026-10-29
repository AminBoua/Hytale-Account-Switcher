using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using HytaleAccounts;

// Compiled only in the disposable launcher copy; Start's verification and completion stay intact.
public static class NativeStartupRecoveryChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Get(object target, string name) => target.GetType().GetField(name, Fields)!.GetValue(target);
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Fields)!.SetValue(target, value);
    private static Task<T> Invoke<T>(NativeSession session, string name, params object[] args) =>
        (Task<T>)typeof(NativeSession).GetMethod(name, Fields)!.Invoke(session, args)!;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }
    public static Action<NativeSession>? BeforeComplete;
    private static string outcome = "";
    private static Process? process;
    public static Process Launch(NativeSession session)
    {
        Check(Get(session, "holdLogin") is true && Get(session, "verifiedSession") is false &&
            Get(session, "signInRecovery") is "previous-sign-in-recovery",
            "startup retains the existing fence before native verification: " + outcome);
        if (outcome == "launch-failure") throw new IOException("Synthetic launch failure.");
        return process = outcome == "exited-process" ? Process.Start(new ProcessStartInfo("cmd.exe", "/d /c set /p startup_fixture_wait=")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })! : Process.GetCurrentProcess();
    }

    public static Task Run()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                try { await RunCore(); complete.TrySetResult(); }
                catch (Exception ex) { complete.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return complete.Task;
    }

    private static async Task RunCore()
    {
        foreach (var scenario in new[] { "authenticated", "selected", "reconciled", "new-account", "signed-out", "signed-out-pending",
            "launch-failure", "readiness-failure", "wrong-profile", "missing-session", "empty-session", "snapshot-failure", "metadata-failure",
            "signed-out-selection", "staging-failure", "replaced-signal", "replaced-process", "exited-process" })
        {
            outcome = scenario; process = null; BeforeComplete = null;
            var root = SecurityFixture.Create("native-startup-recovery-");
            NativeSession? session = null;
            FileStream? locked = null;
            Process? replacement = null;
            var accounts = Path.Combine(root, "Accounts");
            var preserved = Path.Combine(root, "Accounts-preserved");
            try
            {
                var stores = new NativeStores(root);
                var previous = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), "previous");
                var account = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), "startup");
                var official = Path.Combine(root, "account.dat");
                string Snapshot(NativeStore store) => Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat");
                File.WriteAllText(official, "opaque-previous"); stores.Capture(previous);
                File.WriteAllText(official, "opaque-saved"); stores.Capture(account);
                var selection = scenario == "authenticated" ? account : previous;
                stores.Select(selection);
                var staging = scenario is "reconciled" or "new-account" or "signed-out-pending";
                var store = staging ? stores.CreatePending() : account;
                var profile = scenario is "new-account" or "wrong-profile" ? Guid.NewGuid() : account.Profile!.Value;
                var signedOut = scenario.StartsWith("signed-out") || scenario == "staging-failure";
                var commit = scenario is "selected" or "signed-out-selection";
                if (signedOut || scenario == "missing-session") File.Delete(official);
                else File.WriteAllText(official, scenario == "empty-session" ? "" : "opaque-refreshed");
                var metadata = File.ReadAllBytes(Path.Combine(accounts, "native-stores.json"));
                if (scenario is "snapshot-failure" or "metadata-failure")
                    locked = new FileStream(scenario == "snapshot-failure" ? Snapshot(account) : Path.Combine(accounts, "native-stores.json"),
                        FileMode.Open, FileAccess.Read, scenario == "snapshot-failure" ? FileShare.None : FileShare.Read);
                if (scenario == "staging-failure") BeforeComplete = _ =>
                {
                    Directory.Move(accounts, preserved);
                    File.WriteAllText(accounts, "Synthetic blocked storage directory.");
                };
                if (scenario == "replaced-signal") BeforeComplete = s => Set(s, "ready", new TaskCompletionSource<Guid?>());
                if (scenario == "replaced-process") BeforeComplete = s => Set(s, "launcher", replacement = Process.GetCurrentProcess());
                if (scenario == "exited-process") BeforeComplete = _ => { process!.Kill(); process.WaitForExit(); };
                session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
                var oldSignal = new TaskCompletionSource<Guid?>(); Set(session, "ready", oldSignal);
                Set(session, "holdLogin", true); Set(session, "busy", true);
                Set(session, "signInRecovery", "previous-sign-in-recovery");
                Task<bool> Command(object data, TaskCompletionSource<Guid?> signal) =>
                    Invoke<bool>(session, "Command", JsonSerializer.SerializeToElement(data), signal);
                var startup = Invoke<bool>(session, "Start", store, commit);
                var signal = (TaskCompletionSource<Guid?>)Get(session, "ready")!;
                Check(signal != oldSignal && !await Command(new { action = "ready", profile, username = "stale" }, oldSignal) &&
                    Get(session, "holdLogin") is true && Get(session, "nativeName") is null,
                    "old-process readiness cannot complete or authorize the new startup: " + scenario);
                if (scenario != "launch-failure")
                {
                    Check(!startup.IsCompleted, "startup still awaits its own readiness: " + scenario);
                    if (scenario == "readiness-failure") signal.TrySetException(new IOException("Synthetic verification failure."));
                    else Check(await Command(new { action = "ready", profile = signedOut ? (Guid?)null : profile, username = "startup" }, signal),
                        "current-process readiness reaches production startup completion: " + scenario);
                }
                Exception? error = null;
                bool authenticated = false;
                try { authenticated = await startup.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex; }
                var success = scenario is "authenticated" or "selected" or "reconciled" or "new-account" or "signed-out" or "signed-out-pending";
                Check(success ? error is null && authenticated == !signedOut : error is not null,
                    "production Start reports the expected outcome: " + scenario);
                Set(session, "busy", false);
                await (Task)typeof(NativeSession).GetMethod("Publish", Fields)!.Invoke(session, null)!;
                var bridge = (LocalAccountBridge)Get(session, "bridge")!;
                var state = JsonSerializer.Deserialize<JsonElement>((byte[])Get(bridge, "state")!);
                Check(state.GetProperty("holdLogin").GetBoolean() == !success && !state.GetProperty("busy").GetBoolean(),
                    "published startup state releases the fence only on complete success: " + scenario);
                Check(success ? state.GetProperty("signInRecovery").ValueKind == JsonValueKind.Null :
                    state.GetProperty("signInRecovery").GetString() == "previous-sign-in-recovery",
                    "startup retires an obsolete final-account retry only after complete success: " + scenario);
                if (success) Check(!await Command(new { action = "retry-signin", request = "previous-sign-in-recovery" }, signal),
                    "completed startup rejects the previous process's final-account retry: " + scenario);
                if (!success)
                {
                    Check(!await Command(new { action = "ready", profile, username = "late" }, signal) && Get(session, "holdLogin") is true,
                        "late readiness cannot release a failed or superseded startup fence: " + scenario);
                    if (scenario == "staging-failure") { File.Delete(accounts); Directory.Move(preserved, accounts); }
                    locked?.Dispose(); locked = null;
                    Check(stores.Catalog.Selected == previous.Folder && File.ReadAllBytes(Path.Combine(accounts, "native-stores.json")).SequenceEqual(metadata) &&
                        File.ReadAllText(Snapshot(previous)) == "opaque-previous" && File.ReadAllText(Snapshot(account)) == "opaque-saved",
                        "failed startup preserves selection and saved snapshots: " + scenario);
                    continue;
                }
                var active = (NativeStore)Get(session, "active")!;
                if (signedOut)
                {
                    Check(Get(session, "verifiedSession") is false && state.GetProperty("profile").ValueKind == JsonValueKind.Null && stores.IsPending(active) &&
                        (!staging || active == store) && stores.Catalog.Selected == previous.Folder &&
                        File.ReadAllBytes(Path.Combine(accounts, "native-stores.json")).SequenceEqual(metadata) && File.ReadAllText(Snapshot(account)) == "opaque-saved",
                        "signed-out startup reveals sign-in using staging and preserves saved identity, selection and snapshot: " + scenario);
                    var signedIn = Guid.NewGuid(); File.WriteAllText(official, "opaque-native-sign-in");
                    Check(await Command(new { action = "ready", profile = signedIn, username = "signed-in" }, signal) &&
                        Get(session, "verifiedSession") is true && Get(session, "holdLogin") is false &&
                        ((NativeStore)Get(session, "active")!).Profile == signedIn && stores.Catalog.Selected != account.Folder &&
                        File.ReadAllText(Snapshot(account)) == "opaque-saved",
                        "native sign-in completion is accepted after signed-out startup without changing the old saved account: " + scenario);
                }
                else
                {
                    Check(Get(session, "verifiedSession") is true && state.GetProperty("profile").GetGuid() == profile &&
                        File.ReadAllText(Snapshot(active)) == "opaque-refreshed" &&
                        stores.Catalog.Selected == (commit || staging ? active.Folder : selection.Folder),
                        "authenticated startup verifies identity, captures its snapshot and preserves selection semantics: " + scenario);
                    if (scenario == "authenticated")
                    {
                        Check(await Command(new { action = "select", store = active.Folder }, signal) && Get(session, "busy") is false,
                            "selecting the current verified account keeps existing behavior after startup recovery");
                        Check(await session.AddAccount(), "Add is available after authenticated startup recovery");
                        var login = (NativeStore)Get(session, "pendingLogin")!;
                        Check(await Command(new { action = "ready", profile, username = "startup", loginRequest = login.Folder }, signal) &&
                            Get(session, "pendingLogin") is null && Get(session, "verifiedSession") is true && Get(session, "holdLogin") is false,
                            "Add's native authenticated completion is accepted after startup recovery");
                    }
                }
            }
            finally
            {
                BeforeComplete = null; locked?.Dispose();
                if (session is not null && Get(session, "bridge") is LocalAccountBridge bridge) await bridge.DisposeAsync();
                if (scenario == "exited-process" && process is { HasExited: false }) { process.Kill(); process.WaitForExit(); }
                process?.Dispose(); replacement?.Dispose();
                Directory.Delete(root, true);
            }
        }
        Console.WriteLine("PASS startup recovery fences: authenticated and signed-out completion, storage failures, old signals and stale completions");
    }
}
