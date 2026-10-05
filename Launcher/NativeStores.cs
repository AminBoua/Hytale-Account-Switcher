using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace HytaleAccounts;

// Labels describe stores, never decrypted native credentials.
public sealed record NativeStore(string Folder, string Label, Guid? Profile = null, bool SignedOut = false);
public sealed class NativeCatalog
{
    public List<NativeStore> Stores { get; set; } = [];
    public string? Selected { get; set; }
    public string? Active { get; set; }
}
public sealed class NativeStores
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string HytaleRoot { get; }
    public string Root => Path.Combine(HytaleRoot, "Accounts");
    private string Metadata => Path.Combine(Root, "native-stores.json");
    public NativeCatalog Catalog { get; }
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    public bool IsPending(NativeStore store) => pending.Contains(store.Folder);
    public NativeStores(string? root = null)
    {
        HytaleRoot = Path.GetFullPath(root ?? OfficialRoot());
        RegularPath(Root);
        Directory.CreateDirectory(Root);
        RegularPath(Metadata);
        Catalog = File.Exists(Metadata) ? JsonSerializer.Deserialize<NativeCatalog>(File.ReadAllBytes(Metadata), Json) ?? throw new IOException("Invalid store metadata; files preserved.") : new();
        foreach (var store in Catalog.Stores) Folder(store.Folder);
        if (Catalog.Stores.Select(s => s.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Catalog.Stores.Count)
            throw new IOException("Duplicate store paths; metadata preserved.");
        foreach (var directory in Directory.EnumerateDirectories(Root).Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(directory);
            if (id.StartsWith("pending-", StringComparison.OrdinalIgnoreCase)) continue;
            var store = new NativeStore(id, id);
            if (!File.Exists(Path.Combine(Folder(store.Folder), "AppData", "Hytale", "account.dat"))) continue;
            var at = Catalog.Stores.FindIndex(s => s.Folder.Equals(store.Folder, StringComparison.OrdinalIgnoreCase));
            if (at < 0) Catalog.Stores.Add(store);
        }
        if (Catalog.Selected is not null && !Catalog.Stores.Any(s => s.Folder == Catalog.Selected))
            throw new IOException("Selected store is missing from metadata; files preserved.");
        Save();
    }
    private static string OfficialRoot()
    {
        using var user = Registry.CurrentUser.OpenSubKey(@"Software\Hypixel Studios\Hytale");
        using var machine = Registry.LocalMachine.OpenSubKey(@"Software\Hypixel Studios\Hytale");
        return user?.GetValue("GameInstallPath") as string ?? machine?.GetValue("GameInstallPath") as string ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hytale");
    }
    public string Folder(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new IOException("Invalid native store folder.");
        var path = Path.Combine(Root, id);
        RegularPath(path);
        return path;
    }
    internal static void RegularPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Store path contains a link; files preserved.");
    }
    public void Save() { RegularPath(Metadata); AtomicWrite(Metadata, JsonSerializer.SerializeToUtf8Bytes(Catalog, Json)); }
    public NativeStore CreatePending()
    {
        var store = new NativeStore("pending-" + Guid.NewGuid().ToString("N"), "New native account");
        Directory.CreateDirectory(Folder(store.Folder));
        pending.Add(store.Folder);
        return store;
    }
    // Native files remain private and undiscovered after cancellation; never traverse shared junctions.
    public void DiscardPending(NativeStore store) => pending.Remove(store.Folder);
    public NativeStore CommitPending(NativeStore store, Guid profile, string? username)
    {
        if (!IsPending(store) || profile == Guid.Empty) throw new IOException("Pending native login is not verified.");
        var existing = Catalog.Stores.FirstOrDefault(s => s.Profile == profile);
        if (existing is not null)
        {
            VerifyProfile(existing, profile, true, username); DiscardPending(store);
            return Catalog.Stores.Single(s => s.Folder == existing.Folder);
        }
        var previous = Catalog.Selected;
        Catalog.Stores.Add(store);
        try { VerifyProfile(store, profile, true, username); }
        catch { Catalog.Stores.RemoveAll(s => s.Folder == store.Folder); Catalog.Selected = previous; throw; }
        DiscardPending(store);
        return Catalog.Stores.Single(s => s.Folder == store.Folder);
    }
    public void Select(NativeStore store)
    {
        if (!Catalog.Stores.Contains(store)) throw new IOException("Unknown native store.");
        var previous = Catalog.Selected;
        Catalog.Selected = store.Folder;
        try { Save(); } catch { Catalog.Selected = previous; throw; }
    }
    public void VerifyProfile(NativeStore store, Guid profile, bool select, string? username = null)
    {
        if (profile == Guid.Empty || (store.Profile is not null && store.Profile != profile))
            throw new IOException("Native profile does not match this store. Previous selection kept.");
        var at = Catalog.Stores.IndexOf(store);
        if (at < 0) throw new IOException("Unknown native store.");
        var previous = Catalog.Selected;
        var label = store.Label;
        if ((label == "New native account" || label == store.Folder) && username is { Length: > 0 and <= 80 } && !username.Any(char.IsControl)) label = username;
        var verified = store with { Profile = profile, Label = label, SignedOut = false };
        if (verified == store && (!select || Catalog.Selected == store.Folder)) return;
        Catalog.Stores[at] = verified;
        if (select) Catalog.Selected = store.Folder;
        try { Save(); }
        catch { Catalog.Stores[at] = store; Catalog.Selected = previous; throw; }
    }
    public void MarkSignedOut(NativeStore store)
    {
        var at = Catalog.Stores.FindIndex(s => s.Folder == store.Folder);
        if (at < 0) throw new IOException("Unknown native store.");
        var previous = Catalog.Stores[at];
        var selected = Catalog.Selected;
        Catalog.Stores[at] = previous with { SignedOut = true };
        if (selected == store.Folder) Catalog.Selected = null;
        try { Save(); }
        catch { Catalog.Stores[at] = previous; Catalog.Selected = selected; throw; }
    }
    // Hytale writes the active encrypted session in its official location. Snapshots only
    // retain other accounts for switching; credentials are never decoded or re-encrypted.
    private string Storage(NativeStore store) => Path.Combine(Folder(store.Folder), "AppData", "Hytale");
    private static void CopyKeys(string source, string destination)
    {
        RegularPath(source); RegularPath(destination);
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var key in Directory.EnumerateFiles(source))
        {
            RegularPath(key);
            var target = Path.Combine(destination, Path.GetFileName(key)); RegularPath(target);
            var bytes = File.ReadAllBytes(key);
            if (File.Exists(target))
            {
                if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) throw new IOException("Native key conflict; files preserved.");
            }
            else AtomicWrite(target, bytes);
        }
    }
    public void Capture(NativeStore store)
    {
        var storage = Storage(store); RegularPath(storage); Directory.CreateDirectory(storage);
        CopyKeys(Path.Combine(HytaleRoot, ".keys"), Path.Combine(storage, ".keys"));
        var source = Path.Combine(HytaleRoot, "account.dat"); RegularPath(source);
        var target = Path.Combine(storage, "account.dat"); RegularPath(target);
        if (File.Exists(source)) AtomicWrite(target, File.ReadAllBytes(source));
        else File.Delete(target);
        var previous = Catalog.Active;
        Catalog.Active = store.Folder;
        try { Save(); } catch { Catalog.Active = previous; throw; }
    }
    public void Activate(NativeStore store)
    {
        RequireIdle();
        ActivateCore(store);
    }
    // Caller has acknowledged native Shutdown, which pauses refresh without revoking login.
    internal void ActivatePaused(NativeStore store, Process owner)
    {
        var processes = Process.GetProcessesByName("hytale-launcher");
        try
        {
            if (owner.HasExited || processes.Any(p => p.Id != owner.Id)) throw new IOException("Native launcher ownership changed; switch cancelled.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
        var games = Process.GetProcessesByName("HytaleClient");
        try { if (games.Length != 0) throw new IOException("Close the game before switching accounts."); }
        finally { foreach (var game in games) game.Dispose(); }
        ActivateCore(store);
    }
    private void ActivateCore(NativeStore store)
    {
        if (!Catalog.Stores.Contains(store) && !IsPending(store)) throw new IOException("Unknown native store.");
        // A normal restart keeps Hytale's latest refreshed session, not an older snapshot.
        if (Catalog.Active == store.Folder) return;
        var storage = Storage(store); RegularPath(storage);
        CopyKeys(Path.Combine(storage, ".keys"), Path.Combine(HytaleRoot, ".keys"));
        var source = Path.Combine(storage, "account.dat"); RegularPath(source);
        var target = Path.Combine(HytaleRoot, "account.dat"); RegularPath(target);
        var previousBytes = File.Exists(target) ? File.ReadAllBytes(target) : null;
        var nextBytes = File.Exists(source) && !store.SignedOut ? File.ReadAllBytes(source) : null;
        var previous = Catalog.Active;
        try
        {
            if (nextBytes is not null) AtomicWrite(target, nextBytes); else File.Delete(target);
            Catalog.Active = store.Folder; Save();
        }
        catch
        {
            Catalog.Active = previous;
            if (previousBytes is not null) AtomicWrite(target, previousBytes); else File.Delete(target);
            throw;
        }
    }
    public async Task<(ProcessStartInfo Start, string Hash)> PrepareIntegrated(NativeStore store, LocalAccountBridge bridge)
    {
        RequireIdle();
        var original = IntegrationSafety.OfficialPath;
        if (PatchInstaller.Hash(original) != IntegrationSafety.SupportedHash)
        {
            var installer = new PatchInstaller();
            var record = installer.Read(); installer.VerifyInstalled(record);
            original = installer.BackupPath;
        }
        await IntegrationSafety.VerifyPublisher(original);
        var bytes = PatchInstaller.Patch(File.ReadAllBytes(original), bridge.Port, bridge.Capability);
        var run = Path.Combine(Folder(store.Folder), "run-" + Guid.NewGuid().ToString("N"));
        RegularPath(run); Directory.CreateDirectory(run);
        var executable = Path.Combine(run, "hytale-launcher.exe"); AtomicWrite(executable, bytes);
        var start = IntegrationSafety.StartInfo(executable);
        start.Environment["HYTALE_LAUNCHER_NO_SELF_UPDATE"] = "1";
        Activate(store);
        return (start, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }
    public static void RequireIdle()
    {
        foreach (var name in new[] { "hytale-launcher", "HytaleClient" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new IOException("Close the native launcher and game before switching accounts."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    public static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
