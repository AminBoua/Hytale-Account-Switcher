using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using HytaleAccounts;

internal static class NativeExitChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(NativeSession session, string name, object value) => typeof(NativeSession).GetField(name, Fields)!.SetValue(session, value);
    private static object? Get(NativeSession session, string name) => typeof(NativeSession).GetField(name, Fields)!.GetValue(session);
    private static Task Invoke(NativeSession session, string name, params object[] args) => (Task)typeof(NativeSession).GetMethod(name, Fields)!.Invoke(session, args)!;
    private static Task Observe(NativeSession session, Process process) => (Task)typeof(NativeSession).GetMethod("ObserveExit", Fields)!.Invoke(session, [process])!;
    private static string MutexName(string root) => "Local\\HytaleAccounts-exit-check-" + Path.GetFileName(root);
    private static Process ExitedProcess() => Process.Start(new ProcessStartInfo("cmd.exe", "/d /c exit 0")
        { UseShellExecute = false, CreateNoWindow = true })!;

    public static async Task Run(Action<bool, string> check)
    {
        foreach (var failure in new[] { "snapshot", "runtime", "status", "none", "pending-login", "pending-session", "pending-logout" })
        {
            var root = SecurityFixture.Create("native-exit-");
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Checks.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--native-exit-child", root, failure }) start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            var errors = child.StandardError.ReadToEndAsync();
            try
            {
                check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready", "synthetic hidden helper starts: " + failure);
                var metadata = File.ReadAllBytes(Path.Combine(root, "Accounts", "native-stores.json"));
                using (var held = new Mutex(false, MutexName(root), out var first)) check(!first, "helper holds its single-instance mutex before native exit");
                await child.StandardInput.WriteLineAsync("exit"); await child.StandardInput.FlushAsync();
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { throw new Exception("Helper remained alive after native exit: " + failure); }
                var error = await errors;
                check(child.ExitCode == 0 && error.Length == 0, "helper exits through App shutdown after " + failure + ": " + error);
                using (var released = new Mutex(false, MutexName(root), out var first)) check(first, "App exit releases the single-instance mutex");
                var log = File.ReadAllText(Path.Combine(root, "helper.log"));
                check(log.Contains(failure is "none" or "pending-login" or "pending-session" or "pending-logout" ? "Native launcher closed." : "Native launcher cleanup failed; files preserved."), "exit outcome is logged: " + failure);
                if (failure is "pending-session" or "pending-logout")
                    check(log.Contains("transition completed before shutdown") &&
                        File.ReadAllBytes(Path.Combine(root, "Accounts", "native-stores.json")).SequenceEqual(metadata),
                        "pending transition ends promptly without recovery commands or account changes: " + failure);
                check(File.ReadAllText(Path.Combine(root, "account.dat")) == (failure == "pending-login" ? "opaque-accountB" : "opaque-current") &&
                    File.ReadAllText(Path.Combine(root, "Accounts", "run", "hytale-launcher.exe")) == "generated runtime fixture" &&
                    File.ReadAllText(Directory.GetFiles(Path.Combine(root, "Accounts"), "account.dat", SearchOption.AllDirectories).Single()) ==
                        (failure is "snapshot" or "pending-login" ? "opaque-saved" : "opaque-current"), "shutdown preserves pending snapshots and captures refreshed sessions otherwise: " + failure);
                if (failure == "pending-login")
                {
                    check(File.ReadAllBytes(Path.Combine(root, "Accounts", "native-stores.json")).SequenceEqual(metadata) &&
                        File.ReadAllBytes(Path.Combine(root, "expected-metadata.json")).SequenceEqual(
                        File.ReadAllBytes(Path.Combine(root, "Accounts", "native-stores.json"))), "pending-login shutdown preserves account A's identity and selection");
                    var snapshot = Path.GetDirectoryName(Directory.GetFiles(Path.Combine(root, "Accounts"), "account.dat", SearchOption.AllDirectories).Single())!;
                    check(File.ReadAllText(Path.Combine(root, ".keys", "saved.key")) == "opaque-saved-key" &&
                        File.ReadAllText(Path.Combine(root, ".keys", "callback.key")) == "opaque-callback-key" &&
                        File.ReadAllText(Path.Combine(snapshot, ".keys", "saved.key")) == "opaque-saved-key" &&
                        !File.Exists(Path.Combine(snapshot, ".keys", "callback.key")), "pending-login shutdown preserves official keys and account A's pre-Add snapshot keys");
                }
            }
            finally
            {
                if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } // Only our synthetic helper.
                Directory.Delete(root, true);
            }
        }
    }

    // Exercise the real App.OnExit and Closed -> Shutdown path without its live-storage startup.
    private sealed class FixtureApp(string root, string failure) : App
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var stores = new NativeStores(root);
                var account = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), "fixture");
                var official = Path.Combine(root, "account.dat");
                if (failure == "pending-login")
                {
                    Directory.CreateDirectory(Path.Combine(root, ".keys"));
                    File.WriteAllText(Path.Combine(root, ".keys", "saved.key"), "opaque-saved-key");
                }
                File.WriteAllText(official, "opaque-saved"); stores.Capture(account);
                File.WriteAllText(official, "opaque-current");
                var runtime = Path.Combine(stores.Root, "run", "hytale-launcher.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(runtime)!); File.WriteAllText(runtime, "generated runtime fixture");
                using var exited = ExitedProcess(); await exited.WaitForExitAsync();
                using var replacement = Process.GetCurrentProcess();
                var session = new NativeSession(stores, Dispatcher);
                var closed = 0; session.Closed += () => closed++;
                Set(session, "launcher", replacement);
                await Observe(session, exited);
                if (closed != 0 || !session.IsOpen) throw new Exception("Stale observer closed a replacement launcher.");
                Set(session, "launcher", exited); Set(session, "busy", true);
                var stale = Observe(session, exited);
                Set(session, "launcher", replacement); Set(session, "busy", false);
                await stale.WaitAsync(TimeSpan.FromSeconds(3));
                if (closed != 0 || !session.IsOpen) throw new Exception("Observer waiting for a transition closed a replacement launcher.");
                // A recovery failure must also leave a running replacement alone.
                session.Status += text => { if (text == "Native launcher closed.") { Set(session, "launcher", replacement); throw new IOException("synthetic recovery publication failure"); } };
                Set(session, "launcher", exited);
                await Observe(session, exited);
                if (closed != 0 || !session.IsOpen) throw new Exception("Cleanup failure closed a running replacement.");

                if (failure is "pending-session" or "pending-logout")
                {
                    await TransitionExit(stores, account, exited);
                    return;
                }

                using var owned = ExitedProcess(); await owned.WaitForExitAsync();
                session = new NativeSession(stores, Dispatcher);
                Set(session, "launcher", owned); Set(session, "active", account); Set(session, "verifiedSession", true);
                if (failure == "pending-login")
                {
                    File.WriteAllText(official, "opaque-saved");
                    File.Copy(Path.Combine(stores.Root, "native-stores.json"), Path.Combine(root, "expected-metadata.json"));
                    Set(session, "launcher", replacement);
                    if (!await session.AddAccount() || !session.IsOpen || !stores.Catalog.Stores.SequenceEqual(new[] { account }) ||
                        stores.Catalog.Selected != account.Folder || stores.Catalog.Active != account.Folder)
                        throw new Exception("Add changed account A identity, selection or launcher ownership.");
                    // Native callback writes B before the addon receives a completion report.
                    File.WriteAllText(official, "opaque-accountB");
                    File.WriteAllText(Path.Combine(root, ".keys", "callback.key"), "opaque-callback-key");
                    Set(session, "launcher", owned);
                }
                if (failure == "runtime") Set(session, "runImage", runtime);
                using var locked = failure is "snapshot" or "runtime" ? new FileStream(failure == "snapshot" ? official : runtime,
                    FileMode.Open, FileAccess.Read, failure == "snapshot" ? FileShare.None : FileShare.Read) : null;
                session.Status += text =>
                {
                    File.AppendAllText(Path.Combine(root, "helper.log"), text + Environment.NewLine);
                    if (failure == "status") throw new IOException("synthetic logging failure");
                };
                typeof(App).GetField("instance", Fields)!.SetValue(this, new Mutex(true, MutexName(root)));
                session.Closed += () => Shutdown();
                Console.WriteLine("ready");
                await Console.In.ReadLineAsync();
                await Observe(session, owned);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
        }

        private async Task TransitionExit(NativeStores stores, NativeStore account, Process stale)
        {
            // A stdin gate keeps only this synthetic process alive until a request is pending.
            using var owned = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c set /p exitSignal=")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
            using var control = Process.GetProcessById(owned.Id);
            try
            {
                var session = new NativeSession(stores, Dispatcher);
                Set(session, "launcher", owned); Set(session, "active", account); Set(session, "verifiedSession", true);
                var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var requests = new HashSet<string>();
                session.Status += text =>
                {
                    File.AppendAllText(Path.Combine(root, "helper.log"), text + Environment.NewLine);
                    if ((Get(session, "sessionRequest") ?? Get(session, "logoutRequest")) is string request && requests.Add(request))
                        pending.TrySetResult();
                };
                Task transition = Task.CompletedTask;
                session.Closed += () =>
                {
                    if (!transition.IsCompletedSuccessfully || Get(session, "busy") is not false || Get(session, "holdLogin") is not false ||
                        Get(session, "sessionResult") is not null || Get(session, "logoutResult") is not null || requests.Count != 1)
                        throw new Exception("Exit did not finish the pending transition or sent a recovery command.");
                    var rejected = Invoke(session, "NativeAction", "run");
                    if (!rejected.IsFaulted || rejected.Exception!.InnerException is not IOException || requests.Count != 1)
                        throw new Exception("Session command was not rejected immediately after launcher exit.");
                    File.AppendAllText(Path.Combine(root, "helper.log"), "transition completed before shutdown");
                    Shutdown();
                };
                var observed = Observe(session, owned);
                transition = failure == "pending-session" ? Invoke(session, "SwitchCore", account, false) : Invoke(session, "LogoutCore", account);
                await pending.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await Observe(session, stale).WaitAsync(TimeSpan.FromSeconds(3));
                if (transition.IsCompleted || !session.IsOpen || Get(session, "busy") is not true ||
                    (failure == "pending-session" ? ((TaskCompletionSource<JsonElement>)Get(session, "sessionResult")!).Task.IsCompleted :
                        ((TaskCompletionSource<bool>)Get(session, "logoutResult")!).Task.IsCompleted))
                    throw new Exception("Stale observer disturbed a running replacement's pending request.");
                typeof(App).GetField("instance", Fields)!.SetValue(this, new Mutex(true, MutexName(root)));
                Console.WriteLine("ready");
                await Console.In.ReadLineAsync();
                await owned.StandardInput.WriteLineAsync("exit"); await owned.StandardInput.FlushAsync();
                await Task.WhenAll(transition, observed).WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                if (!control.HasExited) { control.Kill(); await control.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            }
        }
    }

    public static void Child(string root, string failure)
    {
        var thread = new Thread(() => Environment.ExitCode = new FixtureApp(root, failure).Run());
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
}
