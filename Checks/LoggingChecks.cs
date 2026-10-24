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
            session.Status += message => App.LogStatus(root, message);
            var published = 0;
            session.Status += _ => published++;
            var publish = typeof(NativeSession).GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Task Publish() => (Task)publish.Invoke(session, null)!;
            App.LogStatus(root, "fixture status");
            var original = File.ReadAllText(log);
            check(original.EndsWith(" fixture status" + Environment.NewLine) &&
                DateTimeOffset.TryParse(original.Split(' ')[0], out _), "status logging keeps its timestamp, message and newline");

            foreach (var readOnly in new[] { true, false })
            {
                File.SetAttributes(log, readOnly ? FileAttributes.ReadOnly : FileAttributes.Normal);
                using var locked = readOnly ? null : new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read);
                try { File.AppendAllText(log, "must fail"); throw new Exception("Fixture log was writable."); }
                catch (UnauthorizedAccessException) when (readOnly) { }
                catch (IOException) when (!readOnly) { }
                var before = published;
                await Publish();
                await Task.Run(async () => await Publish());
                check(published == before + 2 && File.ReadAllText(log) == original,
                    $"{(readOnly ? "read-only" : "locked")} log cannot interrupt synchronous or asynchronous status publication");
            }
            App.LogStatus(root, "after");
            check(File.ReadAllText(log).StartsWith(original) && File.ReadAllText(log).EndsWith(" after" + Environment.NewLine),
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
