using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using HytaleAccounts;

internal static class NativeSessionChecks
{
    // Real native reload check: no browser login, game launch, or credential inspection.
    public static Task Run(bool addAccount = false, string? abort = null, bool signIn = false)
    {
        NativeStores.RequireIdle();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = dispatcher.InvokeAsync(async () =>
            {
                var stores = new NativeStores();
                var previous = stores.Catalog.Selected;
                Process? owned = null;
                try
                {
                    if (signIn)
                    {
                        // Test the actual native empty-session reload without revoking a login.
                        var signInSession = new NativeSession(stores, dispatcher);
                        await signInSession.OpenInitial();
                        var signInStore = stores.Catalog.Stores.Single(s => s.Folder == stores.Catalog.Selected);
                        owned = Process.GetProcessesByName("hytale-launcher").Single();
                        var pid = owned.Id; var window = owned.MainWindowHandle;
                        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        void Set(string name, bool value) => typeof(NativeSession).GetField(name, flags)!.SetValue(signInSession, value);
                        Set("busy", true); Set("holdLogin", true);
                        await (Task)typeof(NativeSession).GetMethod("ShowSignIn", flags)!.Invoke(signInSession, null)!;
                        Set("busy", false); Set("holdLogin", false);
                        await (Task)typeof(NativeSession).GetMethod("Publish", flags)!.Invoke(signInSession, null)!;
                        owned.Refresh();
                        if (owned.HasExited || owned.Id != pid || owned.MainWindowHandle != window || File.Exists(Path.Combine(stores.HytaleRoot, "account.dat")))
                            throw new Exception("Native sign-in refresh replaced the launcher or retained its session.");
                        Console.WriteLine("PASS native cached identity cleared and sign-in refreshed in the same process/window, without revoking login");
                        await signInSession.Open(signInStore, true);
                        owned.Refresh();
                        if (!signInSession.IsOpen || owned.Id != pid || owned.MainWindowHandle != window || stores.Catalog.Selected != signInStore.Folder ||
                            typeof(NativeSession).GetField("verifiedSession", flags)!.GetValue(signInSession) is not true)
                            throw new Exception("Saved session could not be restored after native sign-in refresh.");
                        Console.WriteLine("PASS saved account restored after native sign-in refresh in the same process/window");
                        completion.TrySetResult(); return;
                    }
                    if (addAccount)
                    {
                        var login = new NativeSession(stores, dispatcher);
                        var outcome = "";
                        login.Status += text => { outcome = text; Console.WriteLine(text); };
                        await login.OpenInitial();
                        if (!login.IsOpen || stores.Catalog.Selected is null) throw new Exception("An authenticated account is required for live Add.");
                        using var originalProcess = Process.GetProcessesByName("hytale-launcher").Single();
                        var originalPid = originalProcess.Id;
                        var baselineAccounts = stores.Catalog.Stores.ToArray();
                        var baselineSelection = stores.Catalog.Selected;
                        if (!await login.AddAccount()) throw new Exception("Native Add was not accepted.");
                        if (abort is not null)
                        {
                            var deadline = DateTime.UtcNow.AddSeconds(45);
                            while (!login.IsOpen && DateTime.UtcNow < deadline) await Task.Delay(100);
                            using var native = Process.GetProcessesByName("hytale-launcher").Single();
                            while (native.MainWindowHandle == IntPtr.Zero && !native.HasExited && DateTime.UtcNow < deadline)
                            { await Task.Delay(100); native.Refresh(); }
                            var bootstrap = Regex.Match(Encoding.Latin1.GetString(File.ReadAllBytes(native.MainModule!.FileName)), "http://127\\.0\\.0\\.1:(?<port>[0-9]+)/(?<cap>[A-Za-z0-9_-]{43})/ui\\.js");
                            using var http = new System.Net.Http.HttpClient();
                            http.DefaultRequestHeaders.Add("Origin", LocalAccountBridge.Origin);
                            http.DefaultRequestHeaders.Add("Authorization", "Bearer " + bootstrap.Groups["cap"].Value);
                            using var response = await http.PostAsync("http://127.0.0.1:" + bootstrap.Groups["port"].Value + "/command",
                                new System.Net.Http.StringContent(JsonSerializer.Serialize(new { action = abort,
                                    loginRequest = ((NativeStore?)typeof(NativeSession).GetField("pendingLogin", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(login))?.Folder }), Encoding.UTF8, "application/json"));
                            response.EnsureSuccessStatusCode();
                        }
                        var loginDeadline = DateTime.UtcNow.AddMinutes(5);
                        while (abort is null && outcome != "Native account verified and selected." && DateTime.UtcNow < loginDeadline) await Task.Delay(100);
                        owned = Process.GetProcessesByName("hytale-launcher").SingleOrDefault();
                        if (abort is not null)
                        {
                            if (!login.IsOpen || owned?.Id != originalPid || !stores.Catalog.Stores.SequenceEqual(baselineAccounts) ||
                                stores.Catalog.Selected != baselineSelection)
                                throw new Exception("Aborted login changed saved accounts or selection.");
                            Console.WriteLine("PASS live native " + abort + " keeps the native process and saved accounts unchanged");
                            completion.TrySetResult(); return;
                        }
                        if (owned?.Id != originalPid || stores.Catalog.Selected is null || outcome != "Native account verified and selected." ||
                            stores.Catalog.Stores.Single(s => s.Folder == stores.Catalog.Selected).Profile is null)
                            throw new Exception("Browser login did not commit a verified selection.");
                        Console.WriteLine("PASS live native browser login committed a verified account and selection; saved count: " + stores.Catalog.Stores.Count);
                        completion.TrySetResult(); return;
                    }
                    var candidates = stores.Catalog.Stores.Where(s => File.Exists(Path.Combine(stores.Folder(s.Folder), "AppData", "Hytale", "account.dat"))).ToArray();
                    if (candidates.Length < 2) throw new Exception("Two authenticated native stores are required.");
                    var session = new NativeSession(stores, dispatcher);
                    string status = "";
                    int? switchPid = null;
                    IntPtr switchWindow = IntPtr.Zero;
                    session.Status += text => status = text;
                    foreach (var candidate in candidates.Take(2).Concat(candidates.Take(2)))
                    {
                        var store = stores.Catalog.Stores.Single(s => s.Folder == candidate.Folder);
                        if (owned is null) await session.Open(store, true);
                        else
                        {
                            var bootstrap = Regex.Match(Encoding.Latin1.GetString(File.ReadAllBytes(owned.MainModule!.FileName)), "http://127\\.0\\.0\\.1:(?<port>[0-9]+)/(?<cap>[A-Za-z0-9_-]{43})/ui\\.js");
                            if (!bootstrap.Success) throw new Exception("Missing native dropdown bootstrap.");
                            using var http = new System.Net.Http.HttpClient();
                            http.DefaultRequestHeaders.Add("Origin", LocalAccountBridge.Origin);
                            http.DefaultRequestHeaders.Add("Authorization", "Bearer " + bootstrap.Groups["cap"].Value);
                            using var response = await http.PostAsync("http://127.0.0.1:" + bootstrap.Groups["port"].Value + "/command",
                                new System.Net.Http.StringContent(JsonSerializer.Serialize(new { action = "select", store = store.Folder }), Encoding.UTF8, "application/json"));
                            response.EnsureSuccessStatusCode();
                            var deadline = DateTime.UtcNow.AddSeconds(60);
                            while ((stores.Catalog.Selected != store.Folder || status != "Native profile verified.") && DateTime.UtcNow < deadline) await Task.Delay(100);
                        }
                        if (!session.IsOpen || stores.Catalog.Selected != store.Folder || stores.Catalog.Stores.Single(s => s.Folder == store.Folder).Profile is null)
                            throw new Exception("Native startup/switch failed: " + status);
                        if (owned is null)
                        {
                            owned = Process.GetProcessesByName("hytale-launcher").Single();
                            switchPid = owned.Id; switchWindow = owned.MainWindowHandle;
                        }
                        owned.Refresh();
                        if (owned.HasExited || owned.Id != switchPid || owned.MainWindowHandle != switchWindow)
                            throw new Exception("Account switch closed or replaced the native launcher window.");
                        Console.WriteLine("PASS real native profile verification and persisted selection, same PID/window: " + store.Label);
                    }
                    // A signed-out target must fail verification and gracefully restore the previous store.
                    var selected = stores.Catalog.Selected;
                    using (var locked = new FileStream(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        await session.Open(stores.Catalog.Stores.Single(s => s.Folder == candidates[0].Folder), true);
                        if (!session.IsOpen || stores.Catalog.Selected != selected || !status.Contains("Previous selection kept"))
                            throw new Exception("Real failed commit rollback failed: " + status);
                    }
                    owned!.Refresh();
                    if (owned.HasExited || owned.Id != switchPid || owned.MainWindowHandle != switchWindow)
                        throw new Exception("Failed selection closed or replaced the native launcher.");
                    Console.WriteLine("PASS real failed selection keeps previous native account, PID and window");
                    // A real native identity mismatch must restore the previous session in place.
                    var wrong = stores.Catalog.Stores.Single(s => s.Folder == candidates[0].Folder);
                    var wrongAt = stores.Catalog.Stores.IndexOf(wrong);
                    try
                    {
                        var mismatch = wrong with { Profile = Guid.NewGuid() };
                        stores.Catalog.Stores[wrongAt] = mismatch; stores.Save();
                        await session.Open(mismatch, true);
                        if (!session.IsOpen || stores.Catalog.Selected != selected || !status.Contains("Previous selection kept"))
                            throw new Exception("Native identity mismatch did not recover: " + status);
                        owned.Refresh();
                        if (owned.HasExited || owned.MainWindowHandle != switchWindow || owned.Id != switchPid)
                            throw new Exception("Native identity recovery replaced the launcher.");
                        Console.WriteLine("PASS real mismatched identity restores the previous native session in the same process/window");
                    }
                    finally { stores.Catalog.Stores[wrongAt] = wrong; stores.Save(); }
                    var pending = stores.Catalog.Stores.FirstOrDefault(s => !File.Exists(Path.Combine(stores.Folder(s.Folder), "AppData", "Hytale", "account.dat")));
                    if (pending is not null)
                    {
                        await session.Open(pending, true);
                        if (!session.IsOpen || stores.Catalog.Selected != selected || !status.Contains("Previous selection kept"))
                            throw new Exception("Real signed-out rollback failed: " + status);
                        owned.Refresh();
                        if (owned.HasExited || owned.Id != switchPid || owned.MainWindowHandle != switchWindow)
                            throw new Exception("Signed-out selection closed the native launcher.");
                        Console.WriteLine("PASS real signed-out switch keeps previous native account, PID and window");
                    }
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally
                {
                    if (owned is { HasExited: false })
                    {
                        owned.CloseMainWindow();
                        try { await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                        catch (TimeoutException) { Console.WriteLine("Native test launcher still open; close it normally."); }
                    }
                    owned?.Dispose();
                    if (!addAccount) { stores.Catalog.Selected = previous; stores.Save(); }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
}
