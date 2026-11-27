using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace HytaleAccounts.Checks;

internal static class SessionChecks
{
    internal static async Task Run()
    {
        var account = Guid.NewGuid();
        var startup = new SessionExchange("ready");
        Assert(!startup.TryComplete(Payload(new { action = "ready", profile = account })), "Startup needs its own readiness nonce.");
        Assert(!startup.TryComplete(Payload(new { action = "ready", readyRequest = startup.Id, profile = Guid.Empty })),
            "An empty profile is neither an identity nor a signed-out report.");
        Assert(!startup.TryComplete(Payload(new { action = "ready", readyRequest = startup.Id, loginRequest = "old-add", profile = account })),
            "An obsolete Add callback cannot complete startup.");
        Assert(startup.TryComplete(Payload(new { action = "ready", readyRequest = startup.Id, profile = account })), "Accept current startup readiness.");
        Assert(AccountSession.ReadProfile(await startup.Result) == account, "Startup adopts the verified identity.");

        string? previous = null;
        foreach (var operation in new[] { "stop", "load", "run" })
        {
            var stage = new SessionExchange("session-result", operation);
            Assert(stage.Operation == operation, "The same window receives the next native operation.");
            Assert(!stage.TryComplete(Payload(new { action = "session-result", request = previous, success = true })),
                "An earlier stage cannot advance the current switch.");
            Assert(!stage.TryComplete(Payload(new { action = "logout-result", request = stage.Id, success = true })),
                "A different kind of native result cannot advance a switch.");
            Assert(!stage.Result.IsCompleted, "The owner waits for a matching completion.");
            Assert(stage.TryComplete(Payload(new { action = "session-result", request = stage.Id, success = true, profile = account })),
                "Accept a matching result while the transition waits.");
            Assert(stage.TryComplete(Payload(new { action = "session-result", request = stage.Id, success = false })),
                "Accept delivery retries for the same operation while it is published.");
            Assert((await stage.Result).GetProperty("success").GetBoolean(), "A duplicate cannot overwrite the first result.");
            previous = stage.Id;
        }

        var logout = new SessionExchange("logout-result");
        Assert(!logout.TryComplete(Payload(new { action = "logout-result", request = logout.Id, success = "true" })),
            "Logout confirmation must carry a JSON boolean.");
        Assert(logout.TryComplete(Payload(new { action = "logout-result", request = logout.Id, success = (bool?)null })),
            "Accept delivery of an uncertain logout result.");
        Assert(!logout.Result.IsCompleted, "An uncertain logout cannot move to a fallback account.");
        Assert(logout.TryComplete(Payload(new { action = "logout-result", request = logout.Id, success = true })),
            "The same logout request can later confirm native sign-out.");
        Assert((await logout.Result).GetProperty("success").GetBoolean(), "Confirmed logout releases the owner to recover the window.");

        var signIn = new SessionExchange("ready");
        Assert(signIn.TryComplete(Payload(new { action = "ready", readyRequest = signIn.Id, profile = (Guid?)null })),
            "Native sign-in can report an explicitly missing profile.");
        Assert(AccountSession.ReadProfile(await signIn.Result) is null, "Native sign-in stays signed out.");
        Console.WriteLine("PASS correlated startup, in-place switch stages, logout confirmation and native sign-in");
        CheckLog();
        await CheckNativeVerification(releaseLock: true);
        await CheckNativeVerification(releaseLock: false);
    }

    private static async Task CheckNativeVerification(bool releaseLock)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hytale-verification-check-" + Guid.NewGuid().ToString("N"));
        WindowsPermissions.CreatePrivateDirectory(root);
        using var process = Process.GetCurrentProcess();
        using var lifetime = new CancellationTokenSource();
        Task? transition = null;
        try
        {
            var storage = new AccountStorage(root);
            var pending = storage.CreatePending();
            var marker = Path.Combine(root, "account.dat");
            WindowsFiles.Write(marker, [65], privateFile: true);
            var session = new AccountSession(storage);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(AccountSession);
            void Set(string field, object value) => type.GetField(field, fields)!.SetValue(session, value);
            T Get<T>(string field) => (T)type.GetField(field, fields)!.GetValue(session)!;
            var command = type.GetMethod("Command", fields)!.CreateDelegate<Func<JsonElement, Task<bool>>>(session);
            var request = AccountSession.RequestId();
            Set("launcher", process);
            Set("processLifetime", lifetime);
            Set("active", pending);
            Set("holdLogin", false);
            Set("readyRequest", request);
            var profile = Guid.NewGuid();
            var readiness = Payload(new { action = "ready", readyRequest = request, profile });
            Task<bool> Add(string nonce) => command(Payload(new { action = "add", request = nonce }));

            Assert(!await Add(request), "Add waits for a verified account.");
            Assert(storage.Catalog.Stores.Count == 0 && storage.Catalog.Selected is null, "The blocked request preserves the empty catalog.");
            var log = Path.Combine(root, "hytale-accounts.log");
            const string firstFailure = "Native account verification started with Windows error 32.";
            using (var locked = File.Open(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert(await command(readiness), "The helper accepts the current readiness report.");
                transition = Get<Task>("transition");
                if (releaseLock)
                {
                    var deadline = Stopwatch.StartNew();
                    while (!ReadEvidence(log).Contains(firstFailure, StringComparison.Ordinal))
                    {
                        Assert(deadline.Elapsed < TimeSpan.FromSeconds(3), "The first sharing error is available in the session log.");
                        await Task.Delay(5);
                    }
                }
                Assert(!await Add(request), "Add stays blocked while the marker is locked.");
                if (!releaseLock) await transition.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await transition.WaitAsync(TimeSpan.FromSeconds(5));
            if (!releaseLock)
            {
                var evidence = File.ReadAllText(log);
                Assert(evidence.Contains(firstFailure, StringComparison.Ordinal) &&
                    evidence.Contains("Native account verification stopped with Windows error 32. First Windows error 32.", StringComparison.Ordinal),
                    "The bounded retry records the first sharing error and stops after a persistent error.");
                Assert(!await Add(request) && storage.Catalog.Stores.Count == 0 && storage.Catalog.Selected is null,
                    "A persistent sharing error leaves Add blocked and preserves the catalog.");
                Console.WriteLine("PASS bounded verification retry preserves the first sharing error");
                return;
            }

            Assert(storage.Catalog.Stores.Single().Profile == profile && storage.Catalog.Selected == pending.Folder,
                "The same readiness report verifies the account after the marker is unlocked.");
            Assert(!await Add(request), "Add rejects the obsolete readiness request.");
            Assert(await Add(Get<string>("readyRequest")), "Add accepts the verified request immediately.");
            transition = Get<Task>("transition");
            await transition.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(Get<NativeStore>("login") is not null && storage.Catalog.Selected == pending.Folder,
                "Add publishes a browser request and preserves the selected account.");
            Console.WriteLine("PASS native verification recovers from the first sharing error and accepts Add with the current request");
        }
        finally
        {
            lifetime.Cancel();
            if (transition is not null) await transition;
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ReadEvidence(string path)
    {
        if (!File.Exists(path)) return "";
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private static void CheckLog()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hytale-session-check-" + Guid.NewGuid().ToString("N"));
        WindowsPermissions.CreatePrivateDirectory(root);
        var log = Path.Combine(root, "hytale-accounts.log");
        try
        {
            AccountSession.WriteLog(root, "Startup verified.");
            var entry = File.ReadAllText(log);
            Assert(DateTimeOffset.TryParse(entry.Split(' ')[0], out _) && entry.EndsWith(" Startup verified." + Environment.NewLine),
                "The private session log records the timestamp and status.");
            using (var locked = File.Open(log, FileMode.Open, FileAccess.Read, FileShare.Read))
                AccountSession.WriteLog(root, "Locked status.");
            Assert(File.ReadAllText(log) == entry, "A locked log cannot interrupt a session or replace its existing entry.");
            AccountSession.WriteLog(root, "Closed.");
            var entries = File.ReadAllText(log);
            Assert(entries.StartsWith(entry, StringComparison.Ordinal) && entries.EndsWith(" Closed." + Environment.NewLine),
                "Logging appends after the file becomes writable.");
            Console.WriteLine("PASS timestamped private session log and locked-file recovery");
        }
        finally
        {
            File.Delete(log);
            Directory.Delete(root);
        }
    }

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
