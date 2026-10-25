using System.IO;
using System.Reflection;
using System.Windows.Threading;
using HytaleAccounts;

// Compiled only in the disposable launcher copy with synthetic Start/Stop boundaries.
public static class NativeStartupFailureChecks
{
    public static readonly Queue<object> Results = new();
    public static readonly List<NativeStore> Attempts = [];
    public static bool Start(NativeStore store)
    {
        Attempts.Add(store);
        var result = Results.Dequeue();
        if (result is Exception error) throw error;
        return (bool)result;
    }

    public static async Task Run()
    {
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); }
        foreach (var outcome in new[] { "failure", "access", "incompatible", "success", "signin", "recovered", "recovery-failed", "ordinary" })
        {
            var root = SecurityFixture.Create("native-startup-failure-");
            try
            {
                var stores = new NativeStores(root);
                var previous = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), "previous");
                var target = stores.CommitPending(stores.CreatePending(), Guid.NewGuid(), "target");
                var session = new NativeSession(stores, Dispatcher.CurrentDispatcher);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                if (outcome is "recovered" or "recovery-failed")
                    typeof(NativeSession).GetField("active", flags)!.SetValue(session, previous);
                Exception failure = outcome switch
                {
                    "access" => new UnauthorizedAccessException("synthetic access failure"),
                    "incompatible" => new InvalidOperationException("synthetic incompatible launcher"),
                    _ => new IOException("synthetic startup failure")
                };
                Results.Clear(); Attempts.Clear();
                Results.Enqueue(outcome is "success" or "signin" ? (object)(outcome == "success") : failure);
                if (outcome == "recovered") Results.Enqueue(true);
                if (outcome == "recovery-failed") Results.Enqueue(new IOException("synthetic recovery failure"));
                var status = "";
                session.Status += text => status = text;
                Exception? caught = null;
                try
                {
                    if (outcome == "ordinary") await session.Open(target, true);
                    else await session.OpenInitial();
                }
                catch (Exception ex) { caught = ex; }
                var shouldThrow = outcome is "failure" or "access" or "incompatible" or "recovery-failed";
                Check(shouldThrow ? ReferenceEquals(caught, failure) : caught is null,
                    "initial errors reach the caller unchanged; successful recovery and ordinary failures return normally: " + outcome);
                Check(session.IsOpen == (outcome is "success" or "signin" or "recovered") &&
                    typeof(NativeSession).GetField("busy", flags)!.GetValue(session) is false && Results.Count == 0,
                    "startup leaves the expected process state and clears busy: " + outcome);
                Check(Attempts.SequenceEqual(outcome is "recovered" or "recovery-failed" ? new[] { target, previous } : new[] { target }),
                    "startup attempts only the requested store and existing recovery: " + outcome);
                Check(status == (outcome == "success" ? "Native launcher reopened." : outcome == "signin" ? "Sign in to continue." :
                    failure.Message + " Previous selection kept." + (outcome == "recovery-failed" ? " Could not reopen previous launcher: synthetic recovery failure" : "")),
                    "existing status presentation is preserved: " + outcome);
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
