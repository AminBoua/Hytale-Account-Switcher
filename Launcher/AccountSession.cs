using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HytaleAccounts;

internal sealed class AccountSession(AccountStorage storage)
{
    private readonly object gate = new();
    private LocalAccountBridge? bridge;
    private Process? launcher;
    private CancellationTokenSource? processLifetime;
    private Task? transition;
    private SessionExchange? exchange;
    private NativeStore? active, login, recoveryAccount;
    private string? readyRequest, signInRecovery;
    private bool closing, verified, holdLogin = true;
    private string message = "Opening Hytale…";

    public async Task<int> RunAsync()
    {
        try
        {
            LauncherImage.RequireIdle();
            while (await RunImage()) { }
            return 0;
        }
        catch (Exception error)
        {
            LogError("Launcher session", error);
            throw;
        }
    }

    private async Task<bool> RunImage()
    {
        using var lifetime = new CancellationTokenSource();
        await using var connection = new LocalAccountBridge(Command);
        var image = await LauncherImage.PrepareAsync(storage.Root, connection.Port, connection.Capability);
        FileStream? imageLease = null;
        Process? process = null;
        var canDelete = true;
        try
        {
            using (var directory = new WindowsFiles.DirectoryPin(Path.GetDirectoryName(image.Start.FileName)!, forMutation: true))
            {
                imageLease = WindowsFiles.Open(image.Start.FileName);
                if (LauncherImage.HashFile(image.Start.FileName) != image.Hash)
                    throw new IOException("The prepared Hytale executable changed before launch.");
                LauncherImage.RequireIdle();
                lock (gate)
                {
                    closing = false;
                    bridge = connection;
                    processLifetime = lifetime;
                    active = InitialStore();
                    process = launcher = Process.Start(image.Start) ?? throw new IOException("Could not open Hytale.");
                    Begin("Launcher started", Startup);
                }
            }
            await process.WaitForExitAsync();
            await Drain();
            imageLease.Dispose();
            imageLease = null;
            canDelete = false;
            var updated = await ResumeUpdate(image);
            canDelete = true;
            Log(updated ? "Launcher update preserved." : "Launcher closed.");
            return updated;
        }
        finally
        {
            await Drain();
            try
            {
                if (process is { HasExited: true } && verified && login is null && active is not null)
                    CleanUp(() => storage.Capture(active));
            }
            finally
            {
                try
                {
                    if (login is not null) CleanUp(() => storage.ReleasePending(login));
                    if (active is not null && storage.IsPending(active)) CleanUp(() => storage.ReleasePending(active));
                }
                finally
                {
                    imageLease?.Dispose();
                    if (canDelete && (process is null or { HasExited: true })) CleanUp(() => LauncherImage.Delete(image));
                    process?.Dispose();
                    launcher = null;
                    bridge = null;
                    processLifetime = null;
                    active = login = recoveryAccount = null;
                    readyRequest = signInRecovery = null;
                    verified = false;
                    holdLogin = true;
                }
            }
        }
    }

    private async Task<bool> ResumeUpdate(PreparedImage image)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            var processes = Process.GetProcessesByName("hytale-launcher");
            try
            {
                var updated = processes.FirstOrDefault(process => IsUpdateProcess(process, image.Start.FileName));
                if (updated is null) return await LauncherImage.PreserveUpdateAsync(storage.Root, image);
                if (updated.MainWindowHandle != IntPtr.Zero && await LauncherImage.PreserveUpdateAsync(storage.Root, image))
                {
                    LauncherImage.RequireNoGame();
                    updated.CloseMainWindow();
                    await updated.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    return true;
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            await Task.Delay(100);
        }
        throw new IOException("The Hytale update is still running. Reopen Hytale after it completes.");
    }

    private static bool IsUpdateProcess(Process process, string imagePath)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return !process.HasExited && (string.Equals(path, imagePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, imagePath + ".old", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private NativeStore InitialStore()
    {
        var hasOfficialSession = WindowsFiles.FileExists(Path.Combine(storage.HytaleRoot, "account.dat"));
        if (!hasOfficialSession)
        {
            var saved = storage.Catalog.Stores.FirstOrDefault(store => store.Folder == storage.Catalog.Selected &&
                !store.SignedOut && HasSnapshot(store));
            if (saved is not null)
            {
                storage.Activate(saved);
                return saved;
            }
        }
        var pending = storage.CreatePending();
        try
        {
            if (hasOfficialSession) storage.Capture(pending, requireSession: false);
            return pending;
        }
        catch
        {
            storage.ReleasePending(pending);
            throw;
        }
    }

    private async Task Drain()
    {
        Task? current;
        lock (gate)
        {
            closing = true;
            processLifetime?.Cancel();
            current = transition;
        }
        if (current is not null) await current;
    }

    private void Publish()
    {
        _ = bridge?.Update(new
        {
            stores = storage.Catalog.Stores.Where(store => !store.SignedOut && store.Profile is not null)
                .Select(store => new { id = store.Folder, label = store.Label }).ToArray(),
            selected = storage.Catalog.Selected,
            active = active?.Folder,
            profile = verified ? active?.Profile : null,
            busy = transition is not null,
            holdLogin,
            login = login?.Folder,
            logout = exchange?.Action == "logout-result" ? exchange.Id : null,
            session = exchange?.Action == "session-result" ? new { request = exchange.Id, operation = exchange.Operation } : null,
            readyRequest,
            signInRecovery,
            message
        });
    }

    private void Begin(string status, Func<Task> work)
    {
        transition = Task.Run(async () =>
        {
            try
            {
                // The caller publishes the transition before native work can publish its first request.
                lock (gate) { if (closing) return; }
                Log(status + ".");
                await work();
            }
            catch (OperationCanceledException) when (Closing) { }
            catch (Exception error) { message = error.Message; LogError(status, error); }
            finally
            {
                lock (gate)
                {
                    transition = null;
                    if (!closing) Publish();
                }
            }
        });
        Publish();
    }

    private Task<bool> Command(JsonElement data)
    {
        lock (gate) return Task.FromResult(Accept(data));
    }

    private bool Accept(JsonElement data)
    {
        if (closing || launcher is not { HasExited: false }) return false;
        if (exchange?.TryComplete(data) == true) return true;
        var action = Field(data, "action");
        if (transition is not null) return false;

        if (action is "cancel" or "login-failed" || (action == "ready" && Field(data, "loginRequest") is not null))
        {
            if (login is null || active is null || Field(data, "loginRequest") != login.Folder) return false;
            var pending = login;
            var previous = active;
            if (action == "ready")
            {
                if (ReadProfile(data) is not Guid profile) return false;
                Begin("Browser login completed", () => FinishAdd(pending, previous, profile, Field(data, "username")));
            }
            else
                Begin("Recovering browser login", () => RecoverAdd(pending, previous, action == "cancel" ? "Browser login cancelled." : "Browser login failed."));
            return true;
        }

        if (action == "ready")
        {
            if (readyRequest is null || Field(data, "readyRequest") != readyRequest || holdLogin || login is not null ||
                active is null || !ValidReadiness(data)) return false;
            var profile = ReadProfile(data);
            if (verified && active.Profile == profile) return true;
            if (profile is null && storage.IsPending(active)) return true;
            var origin = active;
            Begin("Native readiness changed", () => AcceptSignIn(origin, profile, Field(data, "username")));
            return true;
        }

        if (action == "retry-signin")
        {
            if (signInRecovery is null || Field(data, "request") != signInRecovery) return false;
            signInRecovery = RequestId();
            var previous = recoveryAccount;
            Begin("Retrying native account recovery", previous is null ? ShowSignIn : () => Restore(previous));
            return true;
        }

        if (login is not null || signInRecovery is not null || readyRequest is not null && Field(data, "request") != readyRequest) return false;
        if (action == "add" && active is not null && verified)
        {
            var previous = active;
            Begin("Browser login requested", () => BeginAdd(previous));
            return true;
        }
        var target = storage.Catalog.Stores.FirstOrDefault(store => store.Folder == Field(data, "store"));
        if (target is null || target.SignedOut || target.Profile is null) return false;
        if (action == "select")
        {
            if (target.Folder != active?.Folder || !verified) Begin("Saved account switch requested", () => Switch(target));
            return true;
        }
        if (action != "logout" || !verified || active?.Folder != target.Folder) return false;
        Begin("Native logout requested", () => Logout(target));
        return true;
    }

    private async Task Startup()
    {
        var initial = active!;
        JsonElement response;
        try { response = await Request("ready", timeoutSeconds: 45); }
        catch (TimeoutException)
        {
            PermitStartupSignIn(initial);
            return;
        }
        if (ReadProfile(response) is Guid profile)
        {
            RequireOwner();
            if (initial.Profile is Guid expected && expected != profile)
            {
                signInRecovery = RequestId();
                await ShowSignIn();
                return;
            }
            await VerifyNativeProfile(initial, profile, Field(response, "username"));
            return;
        }
        PermitStartupSignIn(initial);
    }

    private void PermitStartupSignIn(NativeStore initial)
    {
        if (!storage.IsPending(initial)) active = storage.CreatePending();
        OpenNativeSignIn();
    }

    private async Task AcceptSignIn(NativeStore origin, Guid? profile, string? username)
    {
        RequireOwner();
        if (profile is null)
        {
            verified = false;
            active = storage.CreatePending();
            OpenNativeSignIn();
            return;
        }
        if (!verified && origin.Profile is Guid expected && profile != expected)
        {
            signInRecovery = RequestId();
            await ShowSignIn();
            return;
        }
        verified = false;
        if (!storage.IsPending(origin) && origin.Profile != profile) active = storage.CreatePending();
        await VerifyNativeProfile(active!, profile.Value, username);
    }

    private async Task VerifyNativeProfile(NativeStore store, Guid profile, string? username)
    {
        var firstError = 0;
        for (var delay = 25; ; delay += 25)
        {
            RequireOwner();
            try
            {
                AcceptProfile(store, profile, username);
                return;
            }
            catch (Exception error)
            {
                var code = WindowsError(error);
                if (firstError != 0 && (code != firstError || delay > 100))
                {
                    var reason = code == 0 ? error.GetType().Name : $"Windows error {code}";
                    Log($"Native account verification stopped with {reason}. First Windows error {firstError}.");
                    throw;
                }
                if (code is not (32 or 33)) throw;
                if (firstError == 0)
                {
                    firstError = code;
                    Log($"Native account verification started with Windows error {code}.");
                }
            }
            await Task.Delay(delay, processLifetime!.Token);
        }
    }

    private static int WindowsError(Exception error)
    {
        for (Exception? cause = error; cause is IOException or Win32Exception; cause = cause.InnerException)
        {
            if (cause is Win32Exception native) return native.NativeErrorCode;
            if (((uint)cause.HResult >> 16) == 0x8007) return cause.HResult & 0xffff;
        }
        return 0;
    }

    private void AcceptProfile(NativeStore store, Guid profile, string? username)
    {
        var pending = storage.IsPending(store);
        var current = pending ? storage.Catalog.Stores.FirstOrDefault(saved => saved.Profile == profile) ?? store : store;
        if (current.Profile is Guid expected && expected != profile)
            throw new IOException("The native profile does not match the saved account.");
        storage.Capture(current);
        if (pending)
            active = storage.CommitPending(store, profile, username);
        else
        {
            storage.VerifyProfile(store, profile, select: true, username: username);
            active = storage.Catalog.Stores.Single(saved => saved.Folder == store.Folder);
        }
        verified = true;
        holdLogin = false;
        signInRecovery = null;
        recoveryAccount = null;
        readyRequest = RequestId();
        message = "Hytale account verified.";
        Log(message);
        if (pending)
        {
            try { storage.RemoveRedundantPending(store, active); }
            catch (Exception error) { LogError("Removing redundant login staging", error); }
        }
    }

    private Task BeginAdd(NativeStore previous)
    {
        RequireOwner();
        storage.Capture(previous);
        login = storage.CreatePending();
        verified = false;
        message = "Complete Hytale authentication in your browser.";
        return Task.CompletedTask;
    }

    private async Task FinishAdd(NativeStore pending, NativeStore previous, Guid profile, string? username)
    {
        try
        {
            RequireOwner();
            AcceptProfile(pending, profile, username);
            login = null;
        }
        catch (Exception error) when (!verified)
        {
            await RecoverAdd(pending, previous, error.Message);
        }
    }

    private async Task RecoverAdd(NativeStore pending, NativeStore previous, string reason)
    {
        holdLogin = true;
        login = null;
        try
        {
            await Restore(previous);
            message = reason + " Current account restored.";
            Log("Previous account restored after browser login.");
        }
        catch (Exception) when (!Closing)
        {
            ScheduleRecovery(previous);
            throw;
        }
        finally { storage.DiscardPending(pending); }
    }

    private async Task Switch(NativeStore target)
    {
        RequireOwner();
        if (!HasSnapshot(target)) throw new IOException("The saved native session is missing.");
        var previous = active;
        holdLogin = true;
        message = "Switching to " + target.Label + "…";
        try { await Restore(target); }
        catch (Exception error) when (!Closing && previous is { SignedOut: false, Profile: not null })
        {
            verified = false;
            try { await Restore(previous); }
            catch (Exception) when (!Closing) { ScheduleRecovery(previous); throw; }
            message = error.Message + " Previous account restored.";
            LogError("Saved account switch recovered", error);
        }
        finally { holdLogin = !verified; }
    }

    private async Task Restore(NativeStore target)
    {
        RequireOwner();
        if (!HasSnapshot(target)) throw new IOException("The saved native session is missing.");
        var source = verified ? active : null;
        verified = false;
        await Native("stop");
        RequireOwner();
        if (source is not null) storage.Capture(source);
        storage.Activate(target, restoreSnapshot: true);
        var loaded = await Native("load");
        if (ReadProfile(loaded) != target.Profile || !IsLoggedIn(loaded, expected: true))
            throw new IOException("Hytale did not load the selected account.");
        var resumed = await Native("run");
        if (ReadProfile(resumed) != target.Profile || !IsLoggedIn(resumed, expected: true))
            throw new IOException("Hytale did not resume the selected account.");
        RequireOwner();
        AcceptProfile(target, target.Profile!.Value, Field(resumed, "username"));
    }

    private async Task Logout(NativeStore target)
    {
        RequireOwner();
        verified = false;
        holdLogin = true;
        message = "Logging out " + target.Label + "…";
        var result = await Request("logout-result");
        if (!Succeeded(result))
        {
            ScheduleRecovery(target);
            var restored = await Native("run");
            if (ReadProfile(restored) != target.Profile || !IsLoggedIn(restored, expected: true))
                throw new IOException("Hytale logout failed and the previous profile could not be verified.");
            verified = true;
            holdLogin = false;
            signInRecovery = null;
            recoveryAccount = null;
            readyRequest = RequestId();
            message = "Hytale logout failed. The current account is still signed in.";
            Log(message);
            return;
        }

        Log("Hytale confirmed logout.");
        try
        {
            storage.MarkSignedOut(target);
            storage.Capture(target, requireSession: false);
        }
        catch (Exception error) { LogError("Recording native logout", error); }

        foreach (var candidate in storage.Catalog.Stores.Where(store => store.Folder != target.Folder &&
            !store.SignedOut && store.Profile is not null).ToArray())
        {
            try { await Restore(candidate); return; }
            catch (Exception error) when (!Closing) { LogError("Restoring a fallback account", error); }
        }
        signInRecovery = RequestId();
        await ShowSignIn();
    }

    private async Task ShowSignIn()
    {
        RequireOwner();
        verified = false;
        holdLogin = true;
        await Native("stop");
        RequireOwner();
        var pending = active is not null && storage.IsPending(active) ? active : storage.CreatePending();
        try
        {
            storage.Activate(pending, restoreSnapshot: true);
            active = pending;
        }
        finally { if (active != pending) storage.DiscardPending(pending); }
        var cleared = await Native("clear");
        if (!IsLoggedIn(cleared, expected: false)) throw new IOException("Hytale did not clear the current native session.");
        var resumed = await Native("run");
        if (!IsLoggedIn(resumed, expected: false)) throw new IOException("Hytale did not show native sign-in.");
        OpenNativeSignIn();
    }

    private void OpenNativeSignIn()
    {
        readyRequest = RequestId();
        signInRecovery = null;
        recoveryAccount = null;
        holdLogin = false;
        message = "Sign in to Hytale to continue.";
        Log(message);
    }

    private void ScheduleRecovery(NativeStore previous)
    {
        recoveryAccount = previous;
        signInRecovery = RequestId();
    }

    private void CleanUp(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LogError("Cleaning up the launcher session", error); }
    }

    private void Log(string status) => WriteLog(storage.HytaleRoot, status);

    private void LogError(string status, Exception error)
    {
        var entry = status + ": " + error.GetType().Name;
        Log(entry);
        Trace.TraceWarning(entry);
    }

    internal static void WriteLog(string root, string status)
    {
        const int limit = 128 * 1024;
        var path = Path.Combine(root, "hytale-accounts.log");
        try
        {
            using var contents = new MemoryStream();
            if (WindowsFiles.FileExists(path))
            {
                using var previous = WindowsFiles.Open(path);
                if (previous.Length > limit)
                {
                    previous.Position = previous.Length - limit;
                    int next;
                    do { next = previous.ReadByte(); } while (next != '\n' && next != -1);
                }
                previous.CopyTo(contents);
            }
            contents.Write(Encoding.UTF8.GetBytes(DateTimeOffset.Now.ToString("O") + " " + status + Environment.NewLine));
            WindowsFiles.Write(path, contents.ToArray(), privateFile: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private async Task<JsonElement> Native(string operation)
    {
        RequireOwner();
        var result = await Request("session-result", operation);
        if (!Succeeded(result)) throw new IOException("Hytale could not complete native session " + operation + ".");
        return result;
    }

    private async Task<JsonElement> Request(string action, string? operation = null, int timeoutSeconds = 30)
    {
        var request = new SessionExchange(action, operation);
        CancellationToken cancellation;
        lock (gate)
        {
            if (closing) throw new OperationCanceledException();
            cancellation = processLifetime!.Token;
            exchange = request;
            if (action == "ready") readyRequest = request.Id;
            Publish();
        }
        try { return await request.Result.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellation); }
        finally
        {
            lock (gate)
            {
                exchange = null;
                if (action == "ready") readyRequest = null;
            }
        }
    }

    private bool Closing => processLifetime?.IsCancellationRequested == true || launcher is not { HasExited: false };

    private void RequireOwner()
    {
        if (Closing) throw new OperationCanceledException("Hytale has closed.");
        var processes = Process.GetProcessesByName("hytale-launcher");
        try
        {
            if (processes.Any(process => process.Id != launcher!.Id))
                throw new IOException("Another Hytale launcher is open.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
        LauncherImage.RequireNoGame();
    }

    private bool HasSnapshot(NativeStore store) => WindowsFiles.FileExists(Path.Combine(storage.Folder(store.Folder), "AppData", "Hytale", "account.dat"));
    internal static string RequestId() => Guid.NewGuid().ToString("N");
    internal static JsonElement Value(JsonElement data, string field) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out var value) ? value : default;
    internal static string? Field(JsonElement data, string field) => Value(data, field) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    internal static Guid? ReadProfile(JsonElement data) => Guid.TryParse(Field(data, "profile"), out var profile) && profile != Guid.Empty ? profile : null;
    internal static bool ValidReadiness(JsonElement data) => Value(data, "profile").ValueKind == JsonValueKind.Null || ReadProfile(data) is not null;
    private static bool Succeeded(JsonElement data) => Value(data, "success").ValueKind == JsonValueKind.True;
    private static bool IsLoggedIn(JsonElement data, bool expected) => Value(data, "loggedIn").ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
}

internal sealed class SessionExchange(string action, string? operation = null)
{
    private readonly TaskCompletionSource<JsonElement> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Id { get; } = AccountSession.RequestId();
    public string Action => action;
    public string? Operation => operation;
    public Task<JsonElement> Result => result.Task;

    public bool TryComplete(JsonElement response)
    {
        if (AccountSession.Field(response, "action") != action ||
            AccountSession.Field(response, action == "ready" ? "readyRequest" : "request") != Id) return false;
        if (action == "ready")
        {
            if (AccountSession.Field(response, "loginRequest") is not null || !AccountSession.ValidReadiness(response)) return false;
        }
        else
        {
            var success = AccountSession.Value(response, "success").ValueKind;
            if (success == JsonValueKind.Null && action == "logout-result") return true;
            if (success is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)) return false;
        }
        result.TrySetResult(response.Clone());
        return true;
    }
}
