using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using HytaleAccounts;

internal static class NativeExitChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(NativeSession session, string name, object value) => typeof(NativeSession).GetField(name, Fields)!.SetValue(session, value);
    private static Task Observe(NativeSession session, Process process) => (Task)typeof(NativeSession).GetMethod("ObserveExit", Fields)!.Invoke(session, [process])!;
    private static string MutexName(string root) => "Local\\HytaleAccounts-exit-check-" + Path.GetFileName(root);
    private static Process ExitedProcess() => Process.Start(new ProcessStartInfo("cmd.exe", "/d /c exit 0")
        { UseShellExecute = false, CreateNoWindow = true })!;

    public static async Task Run(Action<bool, string> check)
    {
        foreach (var failure in new[] { "snapshot", "runtime", "status", "none", "pending-login" })
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
                check(child.ExitCode == 0, "helper exits through App shutdown after " + failure + ": " + await errors);
                using (var released = new Mutex(false, MutexName(root), out var first)) check(first, "App exit releases the single-instance mutex");
                var log = File.ReadAllText(Path.Combine(root, "helper.log"));
                check(log.Contains(failure is "none" or "pending-login" ? "Native launcher closed." : "Native launcher cleanup failed; files preserved."), "exit outcome is logged: " + failure);
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
                // A recovery failure must also leave a running replacement alone.
                session.Status += text => { if (text == "Native launcher closed.") { Set(session, "launcher", replacement); throw new IOException("synthetic recovery publication failure"); } };
                Set(session, "launcher", exited);
                await Observe(session, exited);
                if (closed != 0 || !session.IsOpen) throw new Exception("Cleanup failure closed a running replacement.");

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
    }

    public static void Child(string root, string failure)
    {
        var thread = new Thread(() => Environment.ExitCode = new FixtureApp(root, failure).Run());
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }
}
