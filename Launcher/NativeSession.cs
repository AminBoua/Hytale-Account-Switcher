using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

namespace HytaleAccounts;

// One owned native process; game children are never terminated by the helper.
public sealed class NativeSession(NativeStores stores, Dispatcher dispatcher)
{
    private Process? launcher;
    private FileStream? image;
    private LocalAccountBridge? bridge;
    private NativeStore? active;
    private TaskCompletionSource<Guid?>? ready;
    private string? nativeName;
    private string? runImage;
    private string? runHash;
    private string? updateBase;
    private bool busy, holdLogin, verifiedSession;
    private NativeStore? pendingLogin;
    private string? logoutRequest;
    private TaskCompletionSource<bool>? logoutResult;
    private string? sessionRequest, sessionOperation;
    private TaskCompletionSource<JsonElement>? sessionResult;
    private string message = "";
    public event Action<string>? Status;
    public event Action? Closed;
    public bool IsOpen => launcher is { HasExited: false };

    private Task Publish()
    {
        Status?.Invoke(message);
        return bridge?.Update(new { stores = stores.Catalog.Stores.Where(s => !s.SignedOut && s.Profile is not null).Select(s => new { id = s.Folder, label = s.Label }),
            selected = stores.Catalog.Selected, active = active?.Folder, profile = verifiedSession ? active?.Profile : null,
            busy, holdLogin, logout = logoutRequest, login = pendingLogin?.Folder,
            session = sessionRequest is null ? null : new { request = sessionRequest, operation = sessionOperation }, message }) ?? Task.CompletedTask;
    }
    private Task<bool> Command(JsonElement data, TaskCompletionSource<Guid?> signal) => dispatcher.InvokeAsync(async () =>
    {
        if (ready != signal) return false;
        if (!data.TryGetProperty("action", out var action)) return false;
        if (action.GetString() == "session-result")
        {
            if (sessionRequest is null || !data.TryGetProperty("request", out var request) || request.GetString() != sessionRequest) return false;
            sessionResult?.TrySetResult(data.Clone());
            return true;
        }
        if (action.GetString() is "cancel" or "login-failed")
        {
            if (busy || pendingLogin is null || active is null || !data.TryGetProperty("loginRequest", out var request) || request.GetString() != pendingLogin.Folder) return false;
            stores.DiscardPending(pendingLogin); pendingLogin = null;
            var previous = active;
            // Shutdown closes native OAuth's loopback; restore the pre-Add snapshot if a callback already wrote account.dat.
            verifiedSession = false; busy = true; holdLogin = true;
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                await SwitchCore(previous, false);
                if (verifiedSession) { message = "Browser login cancelled. Current account kept."; await Publish(); }
            }), DispatcherPriority.Background);
            await Publish();
            return true;
        }
        if (action.GetString() == "ready")
        {
            // Validate the attempt before touching native identity or completing startup.
            if (data.TryGetProperty("loginRequest", out var request))
            {
                if (pendingLogin is null || request.GetString() != pendingLogin.Folder) return false;
            }
            else if (pendingLogin is not null) return false;
            Guid? profile = data.TryGetProperty("profile", out var value) && Guid.TryParse(value.GetString(), out var id) ? id : null;
            if (profile == Guid.Empty) return false;
            if (profile is not null && holdLogin && !busy) return false; // A cached UI profile cannot confirm uncertain native recovery.
            nativeName = data.TryGetProperty("username", out var name) ? name.GetString() : null;
            if (profile is null && !holdLogin && pendingLogin is null && active is not null && !stores.IsPending(active))
            {
                // Native sign-in may authenticate a different account; preserve the restored store's identity and snapshot.
                active = stores.CreatePending(); verifiedSession = false;
                await Publish();
            }
            ready?.TrySetResult(profile);
            if (!busy && active is not null && profile is Guid verified)
            {
                CommitProfile(verified, true, pendingLogin);
                pendingLogin = null;
                message = "Native account verified and selected.";
                await Publish();
            }
            return true;
        }
        if (action.GetString() == "logout-result")
        {
            if (logoutRequest is null || !data.TryGetProperty("request", out var request) || request.GetString() != logoutRequest ||
                !data.TryGetProperty("success", out var success) || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) return false;
            // Null means logout completed, but native sign-out is still unconfirmed.
            if (success.ValueKind != JsonValueKind.Null) logoutResult?.TrySetResult(success.GetBoolean());
            return true;
        }
        if (busy) return false;
        if (action.GetString() == "add") return await AddAccount();
        if (pendingLogin is not null) return false;
        NativeStore? target;
        if (action.GetString() is "select" or "logout" && data.TryGetProperty("store", out var folder))
            target = stores.Catalog.Stores.FirstOrDefault(s => s.Folder == folder.GetString());
        else return false;
        if (target is null) return false;
        if (action.GetString() == "logout" && target.Folder != active?.Folder) return false;
        if (action.GetString() == "select" && target.Folder == active?.Folder && verifiedSession) return true;
        // Return the HTTP response before beginning the native transition.
        _ = dispatcher.BeginInvoke(new Action(async () => await (action.GetString() == "logout" ? LogoutCore(target) : OpenCore(target, true))), DispatcherPriority.Background);
        busy = true;
        await Publish();
        return true;
    }).Task.Unwrap();

    public async Task Open(NativeStore store, bool commit)
    {
        if (busy) return;
        await OpenCore(store, commit);
    }
    public async Task<bool> AddAccount()
    {
        if (busy || pendingLogin is not null || !IsOpen || active is null || !verifiedSession) return false;
        RequireNoGame();
        stores.Capture(active);
        pendingLogin = stores.CreatePending();
        // Native authentication may replace account.dat before its completion is acknowledged.
        verifiedSession = false;
        message = "Complete Hytale authentication in your browser.";
        await Publish();
        return true;
    }
    public Task OpenInitial() => OpenCore(InitialStore(), false, initial: true);
    internal NativeStore InitialStore()
    {
        var nativeSession = Path.Combine(stores.HytaleRoot, "account.dat");
        NativeStores.RegularPath(nativeSession);
        var hasNativeSession = StorageSafety.FileExists(nativeSession);
        var selected = stores.Catalog.Stores.FirstOrDefault(s => s.Folder == stores.Catalog.Selected && !s.SignedOut);
        if (!hasNativeSession && selected is not null) return selected;
        var initial = stores.CreatePending();
        // The official session may have changed accounts; verify it before updating any saved snapshot.
        if (hasNativeSession) stores.Capture(initial, requireSession: false);
        return initial;
    }
    private void CommitProfile(Guid profile, bool select, NativeStore? login = null)
    {
        var store = login ?? active ?? throw new IOException("No active native account.");
        verifiedSession = false;
        var pending = stores.IsPending(store);
        var target = pending ? stores.Catalog.Stores.FirstOrDefault(s => s.Profile == profile) ?? store :
            stores.Catalog.Stores.Single(s => s.Folder == store.Folder);
        if (profile == Guid.Empty || (target.Profile is not null && target.Profile != profile))
            throw new IOException("Native profile does not match this store. Previous selection kept.");
        // Keep the attempt and previous selection until its required snapshot is safely captured.
        stores.Capture(target);
        if (pending)
        {
            active = stores.CommitPending(store, profile, nativeName);
        }
        else
        {
            stores.VerifyProfile(target, profile, select, nativeName);
            active = stores.Catalog.Stores.Single(s => s.Folder == target.Folder);
        }
        verifiedSession = true;
        if (pending) stores.RemoveRedundantPending(store, active);
    }
    private async Task OpenCore(NativeStore store, bool commit, bool initial = false)
    {
        if (IsOpen) { await SwitchCore(store, commit); return; }
        var previous = active;
        var adding = stores.IsPending(store);
        var stopped = false;
        busy = true; message = "Opening " + store.Label + "…"; await Publish();
        try
        {
            RequireNoGame();
            await Stop();
            stopped = true;
            var authenticated = await Start(store, commit);
            message = !authenticated ? "Sign in to continue." : adding ? "Native account verified and selected." : commit ? "Native profile verified." : "Native launcher reopened.";
        }
        catch (Exception ex)
        {
            message = ex.Message + " Previous selection kept.";
            try
            {
                if (stopped)
                {
                    await Stop();
                    if (previous is not null)
                    {
                        await Start(stores.Catalog.Stores.FirstOrDefault(s => s.Folder == previous.Folder) ?? stores.CreatePending(), false);
                        return;
                    }
                }
            }
            catch (Exception recovery) { message += " Could not reopen previous launcher: " + recovery.Message; }
            if (initial) throw;
        }
        finally { if (adding && active?.Folder != store.Folder) stores.DiscardPending(store); busy = false; await Publish(); }
    }
    private async Task<JsonElement> NativeAction(string operation)
    {
        if (!IsOpen) throw new IOException("Native launcher has exited.");
        sessionResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionRequest = Guid.NewGuid().ToString("N"); sessionOperation = operation;
        try
        {
            await Publish();
            var result = await sessionResult.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new IOException("Native session " + operation + " failed.");
            return result;
        }
        finally { sessionRequest = null; sessionOperation = null; sessionResult = null; }
    }
    private async Task RestoreSession(NativeStore store, bool commit)
    {
        if (store.SignedOut || store.Profile is null ||
            !File.Exists(Path.Combine(stores.Folder(store.Folder), "AppData", "Hytale", "account.dat")))
            throw new IOException("Saved account is signed out. Add it again to sign in.");
        await NativeAction("stop");
        if (active is not null && verifiedSession) stores.Capture(active);
        verifiedSession = false;
        stores.ActivatePaused(store, launcher!);
        var result = await NativeAction("load");
        if (!result.TryGetProperty("profile", out var profile) || !Guid.TryParse(profile.GetString(), out var uuid) || uuid != store.Profile)
            throw new IOException("Native profile does not match the selected account.");
        result = await NativeAction("run");
        if (!result.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind != JsonValueKind.True ||
            !result.TryGetProperty("profile", out profile) || !Guid.TryParse(profile.GetString(), out uuid) || uuid != store.Profile)
            throw new IOException("Native resume did not verify the selected account.");
        nativeName = result.TryGetProperty("username", out var name) ? name.GetString() : null;
        active = store;
        CommitProfile(uuid, commit);
    }
    private async Task ShowSignIn()
    {
        await NativeAction("stop");
        verifiedSession = false;
        var pending = stores.CreatePending();
        try
        {
            stores.ActivatePaused(pending, launcher!);
            var result = await NativeAction("clear");
            if (!result.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind != JsonValueKind.False)
                throw new IOException("Native sign-out could not be verified.");
            active = pending;
            result = await NativeAction("run");
            if (!result.TryGetProperty("loggedIn", out loggedIn) || loggedIn.ValueKind != JsonValueKind.False)
                throw new IOException("Native sign-in refresh could not be verified.");
            holdLogin = false;
        }
        finally { if (active?.Folder != pending.Folder) stores.DiscardPending(pending); }
    }
    private async Task SwitchCore(NativeStore store, bool commit)
    {
        var previous = active;
        var attempted = false;
        busy = true; holdLogin = true; message = "Switching to " + store.Label + "…";
        await Publish();
        try
        {
            RequireNoGame();
            if (previous is null) throw new IOException("No active native account.");
            attempted = true;
            await RestoreSession(store, commit);
            message = "Native profile verified.";
        }
        catch (Exception ex)
        {
            if (attempted) verifiedSession = false;
            message = ex.Message + " Previous selection kept.";
            try
            {
                if (attempted && IsOpen && previous is not null && !previous.SignedOut)
                {
                    if (stores.Catalog.Active != previous.Folder || active?.Folder != previous.Folder)
                        await RestoreSession(stores.Catalog.Stores.Single(s => s.Folder == previous.Folder), true);
                    else
                    {
                        var result = await NativeAction("run");
                        if (!result.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind != JsonValueKind.True ||
                            !result.TryGetProperty("profile", out var profile) || !Guid.TryParse(profile.GetString(), out var uuid) || uuid != previous.Profile)
                            throw new IOException("Previous native account could not be verified.");
                        verifiedSession = true;
                    }
                }
            }
            catch (Exception recovery) { verifiedSession = false; message += " Native recovery failed: " + recovery.Message; }
        }
        finally { busy = false; holdLogin = IsOpen && !verifiedSession; await Publish(); }
    }
    private async Task LogoutCore(NativeStore target)
    {
        var confirmed = false;
        var requested = false;
        var failed = false;
        var recoveryErrors = "";
        busy = true; holdLogin = true;
        message = "Logging out " + target.Label + "…"; await Publish();
        try
        {
            RequireNoGame();
            if (!IsOpen || active?.Folder != target.Folder) throw new IOException("Only the current native account can be logged out.");
            requested = true;
            verifiedSession = false;
            logoutResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
            logoutRequest = Guid.NewGuid().ToString("N");
            await Publish();
            failed = !await logoutResult.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (failed)
                throw new IOException("Native logout failed.");
            confirmed = true;
            logoutRequest = null;
            verifiedSession = false;
            // Revocation is committed; bookkeeping must not block native recovery.
            try
            {
                stores.MarkSignedOut(target);
                stores.Capture(target, requireSession: false);
            }
            catch (Exception ex) { recoveryErrors = "Logout storage update failed: " + ex.Message + " "; }
            // Verify remaining sessions through their native backend, never by reading credentials.
            var candidates = stores.Catalog.Stores.Where(s => s.Folder != target.Folder && !s.SignedOut).ToArray();
            foreach (var candidate in candidates)
            {
                try
                {
                    await RestoreSession(candidate, true);
                    message = recoveryErrors + "Account logged out. Switched to " + active!.Label + ".";
                    return;
                }
                catch (Exception ex) { recoveryErrors += "Could not restore " + candidate.Label + ": " + ex.Message + " "; }
            }
            // Native sign-in is refreshed in the existing window when no usable session remains.
            await ShowSignIn();
            message = recoveryErrors + "Account logged out. Sign in to continue.";
        }
        catch (Exception ex)
        {
            if (requested) verifiedSession = false;
            message = recoveryErrors + ex.Message + (confirmed ? " Native logout succeeded, but account recovery did not complete. Other accounts were preserved." : " Other accounts and selection were preserved.");
            if (requested && !confirmed && !failed) message += " Native logout could not be confirmed. Play remains blocked.";
            logoutRequest = null;
            try
            {
                // Only a reported logout failure may recover the old native session.
                if (failed && IsOpen)
                {
                    var result = await NativeAction("run");
                    if (!result.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind != JsonValueKind.True ||
                        !result.TryGetProperty("profile", out var profile) || !Guid.TryParse(profile.GetString(), out var uuid) || uuid != target.Profile)
                        throw new IOException("Previous native account could not be verified.");
                    verifiedSession = true;
                }
            }
            catch (Exception recovery) { verifiedSession = false; message += " Native recovery failed: " + recovery.Message; }
        }
        finally { logoutRequest = null; logoutResult = null; holdLogin = IsOpen && holdLogin && !verifiedSession; busy = false; await Publish(); }
    }
    private static void RequireNoGame()
    {
        var games = Process.GetProcessesByName("HytaleClient");
        try { if (games.Length != 0) throw new IOException("Close the game before switching accounts."); }
        finally { foreach (var game in games) game.Dispose(); }
    }
    private async Task<bool> Start(NativeStore store, bool commit)
    {
        NativeStores.RequireIdle();
        active = store; verifiedSession = false;
        nativeName = null; ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = ready;
        bridge = new LocalAccountBridge(data => Command(data, signal)); await Publish();
        var prepared = await stores.PrepareIntegrated(store, bridge);
        var start = prepared.Start;
        runImage = start.FileName;
        runHash = prepared.Hash;
        updateBase = prepared.UpdateBase;
        image = new FileStream(start.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (PatchInstaller.Hash(start.FileName) != prepared.Hash) throw new IOException("Native launch image changed before startup; files preserved.");
        launcher = Process.Start(start) ?? throw new IOException("Could not open native launcher.");
        var process = launcher;
        var exit = process.WaitForExitAsync();
        var verified = signal.Task.WaitAsync(TimeSpan.FromSeconds(45));
        if (await Task.WhenAny(verified, exit) == exit)
        {
            // The native updater must be able to rename the old image after its launcher exits.
            image?.Dispose(); image = null;
            if (await ResumeAfterUpdate()) { await Stop(); return await Start(store, commit); }
            throw new IOException("Native launcher exited before profile verification.");
        }
        var profile = await verified;
        if (launcher != process || ready != signal || process.HasExited)
            throw new IOException("Native launcher changed before startup verification completed.");
        if (profile is null && commit) throw new IOException("Native store is signed out. Sign in before selecting it.");
        if (profile is Guid uuid) CommitProfile(uuid, commit);
        else if (active is not null && !stores.IsPending(active)) active = stores.CreatePending();
        // Only this process's completed verification and storage work can release an earlier recovery fence.
        holdLogin = false;
        _ = ObserveExit(process);
        return profile is not null;
    }
    private async Task ObserveExit(Process process)
    {
        var ownedExit = false;
        try
        {
            await process.WaitForExitAsync();
            if (launcher != process) return;
            // Release transitions before waiting for busy; the exited process cannot acknowledge them.
            sessionResult?.TrySetException(new IOException("Native launcher exited during a session request."));
            logoutResult?.TrySetException(new IOException("Native launcher exited during logout."));
            image?.Dispose(); image = null;
            while (busy && launcher == process) await Task.Delay(100);
            if (launcher != process) return;
            ownedExit = true;
            if (active is not null && await ResumeAfterUpdate())
            {
                busy = true;
                try
                {
                    var store = active;
                    await Stop(); await Start(store, false);
                    message = "Launcher updated; account addon restored.";
                }
                finally { busy = false; await Publish(); }
                return;
            }
            message = "Native launcher closed.";
            var stopped = active;
            await Stop();
            if (stopped is not null) stores.ReleasePending(stopped);
            await Publish();
        }
        catch (Exception ex)
        {
            message = "Native launcher cleanup failed; files preserved. " + ex.Message;
            try { await Publish(); } catch { /* Never turn cleanup into helper failure. */ }
        }
        finally
        {
            // Failed cleanup must still release the hidden helper and its instance mutex.
            if (ownedExit && !IsOpen) Closed?.Invoke();
        }
    }
    private async Task<bool> ResumeAfterUpdate()
    {
        if (runImage is null || runHash is null || updateBase is null) return false;
        var deadline = DateTime.UtcNow.AddSeconds(45);
        bool Matches(Process process)
        {
            try
            {
                var path = process.MainModule?.FileName;
                return !process.HasExited && (string.Equals(path, runImage, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, runImage + ".old", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
        }
        while (DateTime.UtcNow < deadline)
        {
            var processes = Process.GetProcessesByName("hytale-launcher");
            try
            {
                var updates = processes.Where(Matches).ToArray();
                if (updates.Length == 0) return false;
                // Hytale's updater owns replacement and verification; wait for its restarted window.
                var restarted = updates.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (restarted is not null && await stores.PreserveLauncherUpdate(runImage, runHash, updateBase))
                {
                    RequireNoGame();
                    restarted.CloseMainWindow();
                    await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    return true;
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            await Task.Delay(100);
        }
        throw new IOException("The native launcher update is still running. Reopen your usual shortcut after it completes; accounts were preserved.");
    }
    private async Task Stop()
    {
        if (launcher is { HasExited: false })
        {
            if (!launcher.CloseMainWindow()) throw new IOException("Native launcher could not close gracefully.");
            try { await launcher.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { throw new IOException("Native launcher did not close; switch cancelled."); }
        }
        // A pending native callback may already have replaced the official session with another account.
        if (launcher is not null && active is not null && verifiedSession && pendingLogin is null) stores.Capture(active);
        launcher?.Dispose(); launcher = null;
        image?.Dispose(); image = null;
        if (runImage is not null)
        {
            // Only remove our generated executable; preserve any native files in its directory.
            NativeStores.RegularPath(runImage);
            File.Delete(runImage);
            var directory = Path.GetDirectoryName(runImage)!;
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            stores.PrunePending(Path.GetFileName(Path.GetDirectoryName(directory)!));
            runImage = null;
        }
        stores.CleanupPending();
        if (bridge is not null) { await bridge.DisposeAsync(); bridge = null; }
        active = null;
    }
}
