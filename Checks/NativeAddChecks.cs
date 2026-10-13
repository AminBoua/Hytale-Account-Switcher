using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Threading;
using HytaleAccounts;

internal static class NativeAddChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = SecurityFixture.Create("native-add-");
        try
        {
            var stores = new NativeStores(root);
            var oldProfile = Guid.NewGuid();
            var current = stores.CommitPending(stores.CreatePending(), oldProfile, "accountB");
            var officialSession = Path.Combine(root, "account.dat");
            File.WriteAllText(officialSession, "opaque-accountB-session"); stores.Capture(current);
            using var process = Process.GetCurrentProcess();
            var session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
            void Set(string name, object value) => typeof(NativeSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, value);
            Set("launcher", process); Set("active", current); Set("verifiedSession", true);
            check(await session.AddAccount(), "Add accepts a browser login while the launcher is open");
            check(session.IsOpen && !process.HasExited && stores.Catalog.Selected == current.Folder && stores.Catalog.Active == current.Folder &&
                File.ReadAllText(officialSession) == "opaque-accountB-session", "Add preserves the process, native session and selected account without restart");
            check(stores.Catalog.Stores.Count == 1 && !await session.AddAccount(), "pending browser login saves no duplicate account and rejects a second Add");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
