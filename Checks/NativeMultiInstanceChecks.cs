using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using HytaleAccounts;

internal static class NativeMultiInstanceChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = SecurityFixture.Create("native-multi-instance-");
        try
        {
            var clientChecks = 0;
            var launcherRunning = false;
            var gameRunning = true;
            using var owner = Process.GetCurrentProcess();
            var stores = new NativeStores(root, name =>
            {
                if (name == "HytaleClient") clientChecks++;
                if ((name == "HytaleClient" && gameRunning) || (name == "hytale-launcher" && launcherRunning))
                    return [Process.GetProcessById(owner.Id)];
                return [];
            });
            stores.RequireNoLauncher();
            check(clientChecks == 0, "the native account launcher can open while a Hytale client is running");
            launcherRunning = true;
            var nativeSession = Path.Combine(root, "account.dat");
            NativeStore Account(string label)
            {
                var store = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), label);
                File.WriteAllText(nativeSession, label);
                stores.Capture(store);
                return store;
            }

            var accountA = Account("accountA");
            var accountB = Account("accountB");
            File.WriteAllText(nativeSession, "accountA");
            stores.Capture(accountA);
            stores.Select(accountA);

            var session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
            var ready = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var activeProfile = accountA.Profile;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            object? Get(string name) => typeof(NativeSession).GetField(name, flags)!.GetValue(session);
            void Set(string name, object value) => typeof(NativeSession).GetField(name, flags)!.SetValue(session, value);
            Set("launcher", owner);
            Set("active", accountA);
            Set("verifiedSession", true);
            Set("ready", ready);
            var logoutRequests = 0;
            clientChecks = 0;
            session.Status += _ =>
            {
                if (Get("logoutRequest") is not null) logoutRequests++;
                if (Get("sessionRequest") is null) return;
                var operation = (string)Get("sessionOperation")!;
                if (operation == "load")
                {
                    var label = File.ReadAllText(nativeSession);
                    activeProfile = stores.Catalog.Stores.Single(store => store.Label == label).Profile;
                }
                var result = JsonSerializer.SerializeToElement(new
                {
                    success = true,
                    profile = activeProfile?.ToString(),
                    username = "native-test",
                    loggedIn = activeProfile is not null
                });
                ((TaskCompletionSource<JsonElement>)Get("sessionResult")!).TrySetResult(result);
            };

            await (Task)typeof(NativeSession).GetMethod("SwitchCore", flags)!.Invoke(session, [accountB, true])!;
            check(stores.Catalog.Selected == accountB.Folder && Get("active") is NativeStore active && active.Folder == accountB.Folder &&
                Get("verifiedSession") is true && clientChecks == 0,
                "switching between saved accounts does not query or block on running Hytale clients");

            try { await session.AddAccount(); throw new Exception("Add account succeeded with a running Hytale client."); }
            catch (IOException ex) when (ex.Message.Contains("Close the game before adding an account", StringComparison.Ordinal))
            {
                check(true, "Add account remains blocked while a Hytale client is running");
            }

            await (Task)typeof(NativeSession).GetMethod("LogoutCore", flags)!.Invoke(session, [accountB])!;
            check(logoutRequests == 0 && Get("verifiedSession") is true &&
                Get("message") is string message && message.Contains("Close the game before adding an account, logging out", StringComparison.Ordinal),
                "logout remains blocked while a Hytale client is running");
        }
        finally { Directory.Delete(root, true); }
    }
}
