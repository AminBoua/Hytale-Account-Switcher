using System.IO;
using System.Reflection;
using System.Windows.Threading;
using HytaleAccounts;

internal static class LoggingChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = SecurityFixture.Create("logging-check-");
        var log = Path.Combine(root, "hytale-accounts.log");
        try
        {
            var session = new NativeSession(new NativeStores(root), Dispatcher.CurrentDispatcher);
            var published = 0;
            session.Status += message => App.LogStatus(root, message);
            session.Status += _ => published++;
            Task Publish() => (Task)typeof(NativeSession).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!;

            App.LogStatus(root, "before");
            var before = File.ReadAllText(log);
            check(before.EndsWith(" before" + Environment.NewLine), "writable status log appends its message");
            File.SetAttributes(log, FileAttributes.ReadOnly);
            await Publish();
            check(published == 1 && File.ReadAllText(log) == before, "read-only log does not interrupt native status publication");
            File.SetAttributes(log, FileAttributes.Normal);
            using (var locked = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await Publish();
                check(published == 2, "locked log does not interrupt native status publication");
            }
            App.LogStatus(root, "after");
            check(File.ReadAllText(log).StartsWith(before) && File.ReadAllText(log).EndsWith(" after" + Environment.NewLine),
                "logging resumes after the log becomes writable");
            try { App.LogStatus("\0", "invalid path"); throw new Exception("Invalid log path was hidden"); }
            catch (ArgumentException) { check(true, "unrelated logging errors still propagate"); }
        }
        finally
        {
            if (File.Exists(log)) File.SetAttributes(log, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
