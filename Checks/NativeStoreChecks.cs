using HytaleAccounts;
using System.Diagnostics;
using System.IO;

if (args.Length == 0) args = ["--safe"];
var safe = args.SequenceEqual(new[] { "--safe" });
if (args.Length == 3 && args[0] == "--native-exit-child") { NativeExitChecks.Child(args[1], args[2]); return; }
if (args.Contains("--live-add")) { await NativeSessionChecks.Run(addAccount: true); return; }
if (args.Contains("--live-cancel")) { await NativeSessionChecks.Run(addAccount: true, abort: "cancel"); return; }
if (args.Contains("--live-failure")) { await NativeSessionChecks.Run(addAccount: true, abort: "login-failed"); return; }
if (args.Contains("--live-native")) { await NativeSessionChecks.Run(); return; }
if (args.Contains("--live-signin")) { await NativeSessionChecks.Run(signIn: true); return; }
if (args.Length == 2 && args[0] == "--verify-image")
{
    await IntegrationSafety.VerifyExecutable(args[1]);
    Console.WriteLine("PASS current signed launcher accepts the resolved adapter and frontend bootstrap");
    var updateRoot = SecurityFixture.Create("native-update-check-");
    var updatedStores = new NativeStores(updateRoot);
    var cached = Path.Combine(updatedStores.Root, "launcher-update.exe");
    try
    {
        if (!await updatedStores.PreserveLauncherUpdate(args[1], new string('0', 64)) || PatchInstaller.Hash(cached) != PatchInstaller.Hash(args[1]))
            throw new Exception("Signed native update was not preserved exactly.");
        if (await updatedStores.PreserveLauncherUpdate(args[1], new string('0', 64))) throw new Exception("Same native update was adopted twice.");
        var invalid = Path.Combine(updateRoot, "unsigned.exe"); File.WriteAllText(invalid, "unsigned update fixture");
        try { await updatedStores.PreserveLauncherUpdate(invalid, new string('0', 64)); throw new Exception("Unsigned update accepted"); }
        catch (InvalidOperationException) { }
        if (PatchInstaller.Hash(cached) != PatchInstaller.Hash(args[1])) throw new Exception("Failed update replaced the preserved signed image.");
        File.Delete(invalid);
        Console.WriteLine("PASS native updater image is preserved exactly, duplicates and unsigned replacements rejected");
    }
    finally
    {
        File.Delete(cached); File.Delete(Path.Combine(updatedStores.Root, "native-stores.json"));
        Directory.Delete(updatedStores.Root); Directory.Delete(updateRoot);
    }
    return;
}

void Check(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine("PASS " + message); }
Check(typeof(PatchInstaller).GetMethod("Install") is null, "legacy installation entry point is absent from shipped assembly");
if (safe || args.Contains("--native-adapter")) NativeAdapterChecks.Run(Check);
if (args.Contains("--native-adapter")) return;
if (safe || args.Contains("--native-exit")) await NativeExitChecks.Run(Check);
if (args.Contains("--native-exit")) return;
if (safe || args.Contains("--native-startup")) NativeStartupChecks.Run(Check);
if (args.Contains("--native-startup")) return;
if (args.Contains("--acl-access")) { EffectiveAccessChecks.Run(Check); return; }
if (args.Contains("--acl-propagation")) { AclPropagationChecks.Run(Check); return; }
if (args.Contains("--storage-safety")) { StorageSafetyChecks.Run(Check); return; }
if (safe || args.Contains("--bridge-auth"))
{
    await BridgeAuthenticationChecks.Run(Check);
    await BridgeLimitChecks.Run(Check);
}
if (args.Contains("--bridge-auth")) { await BridgeShutdownChecks.Run(Check); return; }
if (safe || args.Contains("--setup")) await SetupChecks.Run(Check);
if (args.Contains("--setup")) return;
StorageSafetyChecks.Run(Check);
AclChecks.Run(Check);
if (safe) { EffectiveAccessChecks.Run(Check); AclPropagationChecks.Run(Check); }
if (args.Contains("--acl")) return;
foreach (var name in new[] { "Install.ps1", "Uninstall.ps1", "InstallerSafety.ps1", "Repair-Integration.ps1", "ShortcutIntegration.ps1", "ShortcutFile.cs" })
{
    using var resource = typeof(Setup).Assembly.GetManifestResourceStream("HytaleAccounts." + name);
    Check(resource is { Length: > 0 }, "single-file setup embeds " + name);
}
if (!args.Contains("--stores"))
{
    await NativeAddChecks.Run(Check);
    if (args.Contains("--native-add")) return;
    await BridgeShutdownChecks.Run(Check);
    if (args.Contains("--bridge-shutdown")) return;
    await NativeLogoutChecks.Run(Check);
    if (args.Contains("--native-logout")) return;
    if (!safe) await LifecycleChecks.Run(Check);
    if (args.Contains("--lifecycle")) return;
}
var root = SecurityFixture.Create("native-store-check-");
Directory.CreateDirectory(root);
void DeleteFixture(string path)
{
    var full = Path.GetFullPath(path);
    if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new IOException("Fixture cleanup escaped its root.");
    if ((File.GetAttributes(path) & FileAttributes.Directory) == 0) { File.Delete(path); return; }
    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) DeleteFixture(child);
    Directory.Delete(path); // Remove a junction itself, never enumerate its target.
}
try
{
    foreach (var folder in new[] { "accountA", "accountB" })
    {
        var storage = Path.Combine(root, "Accounts", folder, "AppData", "Hytale");
        Directory.CreateDirectory(Path.Combine(storage, ".keys"));
        File.WriteAllText(Path.Combine(storage, "account.dat"), "opaque-native-session");
        File.WriteAllText(Path.Combine(storage, ".keys", "key"), "opaque-native-key");
    }
    var stores = new NativeStores(root);
    Check(stores.Catalog.Stores.Select(s => s.Label).SequenceEqual(new[] { "accountA", "accountB" }), "native stores discovered uniformly without account-specific identity assumptions");
    stores.Select(stores.Catalog.Stores[0]);
    var pending = stores.CreatePending();
    Check(new NativeStores(root).Catalog.Selected == "accountA", "pending/cancelled native login preserves committed selection across restart");
    stores.Select(stores.Catalog.Stores[1]);
    Check(new NativeStores(root).Catalog.Selected == "accountB", "switch selection persists independently of credentials");
    Check(new NativeStores(root).Catalog.Stores.Count == 2, "discovery reuses stores without duplicates");
    using (var locked = new FileStream(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { stores.Select(stores.Catalog.Stores[0]); throw new Exception("Locked selection changed"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Check(stores.Catalog.Selected == "accountB", "failed metadata commit rolls back in-memory selection"); }
    }
    Check(!stores.Catalog.Stores.Contains(pending), "pending login does not save an account");
    var unchanged = File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json"));
    var failed = stores.CreatePending();
    using (var locked = new FileStream(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { stores.CommitPending(failed, Guid.NewGuid(), "Failed"); throw new Exception("Locked pending login committed"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Check(stores.Catalog.Stores.Count == 2 && stores.Catalog.Selected == "accountB", "failed pending commit preserves accounts and selection"); }
    }
    stores.DiscardPending(failed);
    var cancelledStorage = Path.Combine(stores.Folder(failed.Folder), "AppData", "Hytale");
    Directory.CreateDirectory(cancelledStorage); File.WriteAllText(Path.Combine(cancelledStorage, "account.dat"), "opaque-cancelled");
    Check(new NativeStores(root).Catalog.Stores.Count == 2 && File.ReadAllBytes(Path.Combine(stores.Root, "native-stores.json")).SequenceEqual(unchanged), "cancelled native session is never discovered as a saved account");
    pending = stores.CommitPending(pending, Guid.NewGuid(), "Signed in");
    Check(new NativeStores(root).Catalog.Selected == pending.Folder && stores.Catalog.Stores.Count == 3, "successful pending login commits account and selection together");
    var duplicate = stores.CreatePending();
    Check(stores.CommitPending(duplicate, pending.Profile!.Value, "Same account").Folder == pending.Folder && new NativeStores(root).Catalog.Stores.Count == 3, "repeated native login selects existing account without duplicates");
    stores.MarkSignedOut(pending);
    var reconnect = stores.CreatePending();
    pending = stores.CommitPending(reconnect, pending.Profile!.Value, "Signed in");
    Check(!pending.SignedOut && stores.Catalog.Stores.Count == 3 && stores.Catalog.Selected == pending.Folder, "reauthenticating a logged-out profile reuses and restores its saved account");
    var profile = Guid.NewGuid();
    var first = stores.Catalog.Stores[0];
    stores.VerifyProfile(first, profile, true);
    Check(new NativeStores(root).Catalog.Stores[0].Profile == profile && stores.Catalog.Selected == first.Folder, "verified native profile and selection commit together");
    first = stores.Catalog.Stores[0];
    try { stores.VerifyProfile(first, Guid.NewGuid(), true); throw new Exception("Wrong profile accepted"); }
    catch (IOException) { Check(stores.Catalog.Selected == first.Folder && stores.Catalog.Stores[0].Profile == profile, "mismatched native profile preserves previous identity and selection"); }
    using (var locked = new FileStream(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var second = stores.Catalog.Stores[1];
        try { stores.VerifyProfile(second, Guid.NewGuid(), true); throw new Exception("Locked verification committed"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Check(stores.Catalog.Selected == first.Folder && stores.Catalog.Stores[1] == second, "failed verified commit rolls back profile and selection together"); }
    }
    foreach (var folder in new[] { "accountA", "accountB" })
    {
        var storage = Path.Combine(root, "Accounts", folder, "AppData", "Hytale");
        Check(File.ReadAllText(Path.Combine(storage, "account.dat")) == "opaque-native-session" && File.ReadAllText(Path.Combine(storage, ".keys", "key")) == "opaque-native-key", "native session and key remain byte-identical: " + folder);
    }
    var other = stores.Catalog.Stores[1];
    var selectedBeforeLogout = stores.Catalog.Selected;
    using (var locked = new FileStream(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { stores.MarkSignedOut(first); throw new Exception("Locked logout metadata committed"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Check(!stores.Catalog.Stores[0].SignedOut && stores.Catalog.Selected == selectedBeforeLogout, "failed logout metadata rolls back selection and account state"); }
    }
    stores.MarkSignedOut(other);
    Check(stores.Catalog.Selected == selectedBeforeLogout && stores.Catalog.Stores[0] == first, "logging out an inactive store preserves active selection and identity");
    stores.MarkSignedOut(first);
    var reloaded = new NativeStores(root);
    Check(reloaded.Catalog.Selected is null && reloaded.Catalog.Stores.Count == 3 && reloaded.Catalog.Stores[0].SignedOut && reloaded.Catalog.Stores[1].SignedOut,
        "confirmed logout clears only the chosen selection and persists signed-out stores without rediscovering them as usable");
    stores.VerifyProfile(stores.Catalog.Stores[0], profile, true);
    Check(!new NativeStores(root).Catalog.Stores[0].SignedOut, "native reauthentication restores store usability with the same verified identity");
    Check(File.ReadAllText(Path.Combine(stores.Folder(other.Folder), "AppData", "Hytale", "account.dat")) == "opaque-native-session",
        "logout metadata never edits or deletes native credentials");
    // Active native files live at the official root; snapshots preserve account isolation.
    first = stores.Catalog.Stores[0];
    other = stores.Catalog.Stores[1] with { SignedOut = false };
    stores.Catalog.Stores[1] = other; stores.Save();
    stores.Activate(first);
    Check(File.ReadAllText(Path.Combine(root, "account.dat")) == "opaque-native-session", "switch restores native encrypted session into official location");
    Check(File.ReadAllText(Path.Combine(root, ".keys", "key")) == "opaque-native-key", "native keys retain their format at official location");
    File.WriteAllText(Path.Combine(root, "account.dat"), "refreshed-accountB");
    stores.Capture(first);
    File.WriteAllText(Path.Combine(root, "account.dat"), "latest-accountB");
    new NativeStores(root).Activate(first);
    Check(File.ReadAllText(Path.Combine(root, "account.dat")) == "latest-accountB", "normal restart retains latest official refresh rather than stale snapshot");
    stores.Capture(first);
    stores.Activate(other);
    File.WriteAllText(Path.Combine(root, "account.dat"), "accountA-session"); stores.Capture(other); stores.Select(other);
    stores.Activate(first);
    File.Delete(Path.Combine(root, "account.dat")); // Native logout deletes the active session.
    stores.MarkSignedOut(first); stores.Capture(first);
    stores.Activate(other); stores.Select(other);
    Check(File.ReadAllText(Path.Combine(root, "account.dat")) == "accountA-session" && stores.Catalog.Selected == other.Folder,
        "accountB login, accountA login, accountB logout preserves and restores accountA");
    var cancelled = stores.CreatePending();
    stores.Activate(cancelled);
    Check(!File.Exists(Path.Combine(root, "account.dat")), "Add account starts signed out at official location");
    stores.DiscardPending(cancelled); stores.Activate(other);
    Check(File.ReadAllText(Path.Combine(root, "account.dat")) == "accountA-session", "cancelled login restores accountA session");
    using (var locked = new FileStream(Path.Combine(stores.Root, "native-stores.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { stores.Activate(first); throw new Exception("Locked activation committed"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Check(File.ReadAllText(Path.Combine(root, "account.dat")) == "accountA-session" && stores.Catalog.Active == other.Folder, "failed activation restores official session and active marker"); }
    }
    var malicious = Path.Combine(stores.Folder(first.Folder), "AppData", "Hytale", ".keys", "key");
    File.WriteAllText(malicious, "wrong key");
    try { stores.Activate(first); throw new Exception("Conflicting key accepted"); }
    catch (IOException) { Check(File.ReadAllText(Path.Combine(root, "account.dat")) == "accountA-session", "conflicting native key rejects switch before changing active session"); }
    try { stores.Folder("../escape"); throw new Exception("Traversal accepted"); } catch (IOException) { Check(true, "store traversal rejected"); }
    var inherited = IntegrationSafety.StartInfo(IntegrationSafety.OfficialPath);
    foreach (var key in new[] { "APPDATA", "LOCALAPPDATA", "USERPROFILE", "TEMP", "TMP" })
        Check(inherited.Environment[key] == Environment.GetEnvironmentVariable(key), "default browser inherits real Windows profile: " + key);
    Check(!inherited.Environment.Keys.Any(k => k.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)), "native launch removes debug overrides");
    if (safe) return; // Native binary/legacy lifecycle checks are explicitly opt-in.
    var source = IntegrationSafety.OfficialPath;
    if (PatchInstaller.Hash(source) != IntegrationSafety.SupportedHash) source = new PatchInstaller().BackupPath;
    var original = File.ReadAllBytes(source);
    Check(System.Text.Encoding.Latin1.GetString(original).Contains("function la(){return window.go.account.Service.LogoutUser()}") &&
        System.Text.Encoding.Latin1.GetString(original).Contains("function ca(){return window.go.account.Service.IsUserLoggedIn()}"),
        "supported native build exposes logout and signed-in verification backends");
    var integrated = PatchInstaller.Patch(original, 49152, new string('a', 43));
    var adaptive = PatchInstaller.PatchCurrent(original, 49152, new string('a', 43));
    Check(adaptive.AsSpan(0x43fca0, 96).SequenceEqual(integrated.AsSpan(0x43fca0, 96)), "resolved adapter matches verified native machine code exactly");
    var updated = (byte[])original.Clone();
    var oldAsset = System.Text.Encoding.ASCII.GetBytes("index-C4Qf_qVT.js");
    var updatedAsset = System.Text.Encoding.ASCII.GetBytes("index-UPDATED1.js");
    var titleAt = updated.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes("<title>Hytale Launcher</title>"));
    var assetAt = titleAt + updated.AsSpan(titleAt).IndexOf(oldAsset);
    updatedAsset.CopyTo(updated, assetAt);
    var updatedImage = PatchInstaller.PatchCurrent(updated, 49152, new string('a', 43));
    Check(updatedImage.Length == updated.Length && System.Text.Encoding.Latin1.GetString(updatedImage).Contains("src=\"/assets/index-UPDATED1.js\""), "changed build hash keeps updated native frontend assets without downgrade");
    // Simulate a linker moving all file sections, so no historical raw offsets remain usable.
    using (var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(updated)))
    {
        var textSection = pe.PEHeaders.SectionHeaders.Single(s => s.Name == ".text");
        const int shift = 512;
        var relocated = new byte[updated.Length + shift];
        updated.AsSpan(0, textSection.PointerToRawData).CopyTo(relocated);
        updated.AsSpan(textSection.PointerToRawData).CopyTo(relocated.AsSpan(textSection.PointerToRawData + shift));
        var sectionHeaders = pe.PEHeaders.PEHeaderStartOffset + pe.PEHeaders.CoffHeader.SizeOfOptionalHeader;
        for (var i = 0; i < pe.PEHeaders.SectionHeaders.Length; i++)
        {
            var offset = pe.PEHeaders.SectionHeaders[i].PointerToRawData;
            if (offset >= textSection.PointerToRawData)
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(relocated.AsSpan(sectionHeaders + i * 40 + 20, 4), offset + shift);
        }
        var relocatedImage = PatchInstaller.PatchCurrent(relocated, 49152, new string('a', 43));
        Check(relocatedImage.AsSpan(0x43fca0 + shift, 96).SequenceEqual(adaptive.AsSpan(0x43fca0, 96)), "relocated native functions resolve from Go metadata without old file offsets");
    }
    var invalidAdapter = (byte[])updated.Clone(); invalidAdapter[0x43fca0 + 22] ^= 1;
    try { PatchInstaller.PatchCurrent(invalidAdapter, 49152, new string('a', 43)); throw new Exception("Changed receiver accepted"); }
    catch (IOException) { Check(true, "changed native receiver layout rejects patch before account activation"); }
    try { PatchInstaller.PatchCurrent(new byte[128], 49152, new string('a', 43)); throw new Exception("Invalid image accepted"); }
    catch (IOException) { Check(true, "malformed update is rejected without changing official bytes"); }
    Check(integrated.Length == original.Length && System.Text.Encoding.Latin1.GetString(integrated).Contains("http://127.0.0.1:49152/" + new string('a', 43) + "/ui.js"), "integrated copy embeds a local capability bootstrap and preserves binary size");
    Check(integrated.AsSpan(0x124bd5a, 3).SequenceEqual(new byte[] { 2, 0x60, 0 }) &&
        integrated.AsSpan(0x124bd6e, 3).SequenceEqual(new byte[] { 1, 0x60, 0 }) &&
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(integrated.AsSpan(0x1536730, 4)) == 0x124bd5a - 0x12042e8 &&
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(integrated.AsSpan(0x153674c, 4)) == 0x124bd6e - 0x12042e8,
        "native reload adapter has valid Go leaf stack and asynchronous preemption metadata");
    await using (var bridge = new LocalAccountBridge(data => Task.FromResult(data.GetProperty("action").GetString() == "ready")))
    {
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Add("Origin", LocalAccountBridge.Origin);
        var denied = await http.GetAsync(bridge.Endpoint + "state");
        Check(denied.StatusCode == System.Net.HttpStatusCode.Unauthorized, "local bridge rejects requests without its per-launch capability");
        http.DefaultRequestHeaders.Add("Authorization", "Bearer " + bridge.Capability);
        http.DefaultRequestHeaders.Referrer = new Uri(LocalAccountBridge.Origin + "/launch-game");
        var script = await http.GetStringAsync(bridge.Endpoint + bridge.Capability + "/ui.js");
        Check(script.Contains("data-native-store") && !script.Contains("command('play'"), "production bridge serves the embedded native dropdown without helper Play");
        using var response = await http.PostAsync(bridge.Endpoint + "command", new System.Net.Http.StringContent("{\"action\":\"ready\"}", System.Text.Encoding.UTF8, "application/json"));
        Check(response.StatusCode == System.Net.HttpStatusCode.NoContent, "authenticated native profile report reaches the bridge command handler");
    }
    Check(System.Text.Encoding.Latin1.GetString(integrated).Contains(@"Software\Hypixel Studios\Hytale") &&
        !System.Text.Encoding.Latin1.GetString(integrated).Contains(@"Software\Hypixel Studios\Native"), "integrated launcher keeps official registry storage lookup");
    Check(!typeof(NativeStores).Assembly.GetTypes().Any(t => t.Name is "BrowserLogin" or "AccountService" or "HytaleApi" or "GameLaunch" or "Store" or "AccountCommands" or "DebugJob"), "credential vault/authentication/helper Play and kill-on-close job excluded from production assembly");
}
finally { DeleteFixture(root); }
