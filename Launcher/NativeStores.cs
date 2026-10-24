using System.Diagnostics;
using System.Security.AccessControl;
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
        PrivateFiles.SecureTree(Root);
        using var directories = new SetupPayload.PinnedDirectories(Root, requireTrusted: false, forMutation: true);
        RegularPath(Metadata);
        Catalog = StorageSafety.FileExists(Metadata) ? JsonSerializer.Deserialize<NativeCatalog>(ReadFile(Metadata), Json) ?? throw new IOException("Invalid store metadata; files preserved.") : new();
        foreach (var store in Catalog.Stores) Folder(store.Folder);
        if (Catalog.Stores.Select(s => s.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Catalog.Stores.Count)
            throw new IOException("Duplicate store paths; metadata preserved.");
        foreach (var directory in Directory.EnumerateDirectories(Root).Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(directory);
            if (id.StartsWith("pending-", StringComparison.OrdinalIgnoreCase)) continue;
            var store = new NativeStore(id, id);
            if (!StorageSafety.FileExists(Path.Combine(Folder(store.Folder), "AppData", "Hytale", "account.dat"))) continue;
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
            if (StorageSafety.Attributes(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Store path '{current}' contains a link; files preserved.");
    }
    public void Save() { RegularPath(Metadata); AtomicWrite(Metadata, JsonSerializer.SerializeToUtf8Bytes(Catalog, Json), privateFile: true); }
    public NativeStore CreatePending()
    {
        var store = new NativeStore("pending-" + Guid.NewGuid().ToString("N"), "New native account");
        PrivateFiles.CreateDirectory(Folder(store.Folder));
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
    private static void CopyKeys(string source, string destination, bool privateFiles = false)
    {
        RegularPath(source); RegularPath(destination);
        if (!StorageSafety.DirectoryExists(source)) return;
        using var sourceDirectories = new SetupPayload.PinnedDirectories(source, requireTrusted: false, forMutation: true);
        PrivateFiles.CreateDirectory(destination);
        using var destinationDirectories = new SetupPayload.PinnedDirectories(destination, requireTrusted: false, forMutation: true);
        foreach (var key in Directory.EnumerateFiles(source))
        {
            RegularPath(key);
            var target = Path.Combine(destination, Path.GetFileName(key)); RegularPath(target);
            var bytes = ReadFile(key);
            if (StorageSafety.FileExists(target))
            {
                if (!ReadFile(target).AsSpan().SequenceEqual(bytes)) throw new IOException("Native key conflict; files preserved.");
                if (privateFiles) AtomicWrite(target, bytes, privateFile: true);
            }
            else AtomicWrite(target, bytes, privateFile: privateFiles);
        }
    }
    public void Capture(NativeStore store)
    {
        var storage = Storage(store); PrivateFiles.CreateDirectory(storage);
        using var directories = new SetupPayload.PinnedDirectories(storage, requireTrusted: false, forMutation: true);
        using var nativeDirectories = new SetupPayload.PinnedDirectories(HytaleRoot, requireTrusted: false, forMutation: true);
        CopyKeys(Path.Combine(HytaleRoot, ".keys"), Path.Combine(storage, ".keys"), privateFiles: true);
        var source = Path.Combine(HytaleRoot, "account.dat"); RegularPath(source);
        var target = Path.Combine(storage, "account.dat"); RegularPath(target);
        if (StorageSafety.FileExists(source)) AtomicWrite(target, ReadFile(source), privateFile: true);
        else DeleteFile(target);
        var previous = Catalog.Active;
        Catalog.Active = store.Folder;
        try { Save(); } catch { Catalog.Active = previous; throw; }
    }
    // Only called after native verification reconciles staging and Capture saves the current session.
    internal void RemoveReconciledSnapshot(NativeStore staging, NativeStore saved)
    {
        if (Catalog.Stores.Any(s => s.Folder.Equals(staging.Folder, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Catalog.Selected, staging.Folder, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Catalog.Active, staging.Folder, StringComparison.OrdinalIgnoreCase) ||
            !Catalog.Stores.Contains(saved) || saved.Profile is null || Catalog.Active != saved.Folder) return;
        try
        {
            var source = Storage(staging); var destination = Storage(saved);
            if (!StorageSafety.DirectoryExists(source) || !StorageSafety.FileExists(Path.Combine(destination, "account.dat"))) return;
            using var sourceDirectories = new SetupPayload.PinnedDirectories(source, requireTrusted: false);
            using var savedDirectories = new SetupPayload.PinnedDirectories(destination, requireTrusted: false);
            using var capturedSession = SetupPayload.OpenSource(Path.Combine(destination, "account.dat"));
            if (capturedSession.Length == 0) return;
            var keys = Path.Combine(source, ".keys"); RegularPath(keys);
            if (StorageSafety.DirectoryExists(keys))
            {
                using var keyDirectories = new SetupPayload.PinnedDirectories(keys, requireTrusted: false);
                foreach (var key in Directory.EnumerateFiles(keys))
                {
                    var captured = Path.Combine(destination, ".keys", Path.GetFileName(key)); RegularPath(captured);
                    if (!StorageSafety.FileExists(captured)) continue;
                    using var capturedDirectories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(captured)!, requireTrusted: false);
                    using var capturedKey = SetupPayload.OpenSource(captured);
                    using var stagingKey = SetupPayload.OpenSource(key, allowDelete: true);
                    using var savedBytes = new MemoryStream(); capturedKey.CopyTo(savedBytes);
                    using var stagedBytes = new MemoryStream(); stagingKey.CopyTo(stagedBytes);
                    if (savedBytes.ToArray().AsSpan().SequenceEqual(stagedBytes.ToArray())) File.Delete(key);
                }
            }
            // The refreshed, verified capture supersedes this older session; leave runtime and unrelated files alone.
            DeleteFile(Path.Combine(source, "account.dat"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Reconciled staging snapshot cleanup skipped; remaining files retained. " + ex.Message);
        }
    }
    public void Activate(NativeStore store)
    {
        RequireIdle();
        ActivateCore(store, restoreSnapshot: false);
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
        ActivateCore(store, restoreSnapshot: true);
    }
    private void ActivateCore(NativeStore store, bool restoreSnapshot)
    {
        if (!Catalog.Stores.Contains(store) && !IsPending(store)) throw new IOException("Unknown native store.");
        // A normal restart keeps Hytale's latest refreshed session, not an older snapshot.
        if (!restoreSnapshot && Catalog.Active == store.Folder) return;
        var storage = Storage(store); RegularPath(storage);
        using var nativeDirectories = new SetupPayload.PinnedDirectories(HytaleRoot, requireTrusted: false, forMutation: true);
        using var sourceDirectories = new SetupPayload.PinnedDirectories(StorageSafety.DirectoryExists(storage) ? storage : Folder(store.Folder), requireTrusted: false, forMutation: true);
        CopyKeys(Path.Combine(storage, ".keys"), Path.Combine(HytaleRoot, ".keys"));
        var source = Path.Combine(storage, "account.dat"); RegularPath(source);
        var target = Path.Combine(HytaleRoot, "account.dat"); RegularPath(target);
        byte[]? previousBytes = null;
        FileSecurity? previousPermissions = null;
        if (StorageSafety.FileExists(target))
        {
            using var file = SetupPayload.OpenSource(target);
            using var buffer = new MemoryStream(); file.CopyTo(buffer); previousBytes = buffer.ToArray();
            previousPermissions = file.GetAccessControl();
        }
        var nextBytes = StorageSafety.FileExists(source) && !store.SignedOut ? ReadFile(source) : null;
        var previous = Catalog.Active;
        try
        {
            if (nextBytes is not null) AtomicWrite(target, nextBytes); else DeleteFile(target);
            Catalog.Active = store.Folder; Save();
        }
        catch
        {
            Catalog.Active = previous;
            if (previousBytes is not null) AtomicWrite(target, previousBytes, previousPermissions); else DeleteFile(target);
            throw;
        }
    }
    public async Task<(ProcessStartInfo Start, string Hash)> PrepareIntegrated(NativeStore store, LocalAccountBridge bridge)
    {
        RequireIdle();
        var original = IntegrationSafety.OfficialPath;
        var updated = Path.Combine(Root, "launcher-update.exe");
        RegularPath(updated);
        if (StorageSafety.FileExists(updated) && NewerBuild(updated, original)) original = updated;
        async Task<byte[]> ReadOriginal(string path)
        {
            RegularPath(path);
            using var directories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(path)!, requireTrusted: false);
            using var source = SetupPayload.OpenSource(path);
            await IntegrationSafety.VerifyPublisher(path);
            using var buffer = new MemoryStream(); await source.CopyToAsync(buffer);
            return buffer.ToArray();
        }
        byte[] originalBytes;
        try { originalBytes = await ReadOriginal(original); }
        catch (InvalidOperationException)
        {
            // Only unsigned earlier in-place patches need their restoration journal; updates ignore stale state.
            var legacy = new PatchInstaller();
            var record = legacy.Read(); legacy.VerifyInstalled(record);
            originalBytes = await ReadOriginal(legacy.BackupPath);
        }
        var bytes = PatchInstaller.PatchCurrent(originalBytes, bridge.Port, bridge.Capability);
        var run = Path.Combine(Folder(store.Folder), "run-" + Guid.NewGuid().ToString("N"));
        PrivateFiles.CreateDirectory(run);
        using var runDirectories = new SetupPayload.PinnedDirectories(run, requireTrusted: false, forMutation: true);
        var executable = Path.Combine(run, "hytale-launcher.exe"); AtomicWrite(executable, bytes, privateFile: true);
        var start = IntegrationSafety.StartInfo(executable);
        start.Environment["HYTALE_LAUNCHER_NO_SELF_UPDATE"] = "1";
        Activate(store);
        return (start, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
    }
    internal async Task<bool> PreserveLauncherUpdate(string path, string previousHash)
    {
        RegularPath(path);
        if (!StorageSafety.FileExists(path)) return false;
        using var directories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(path)!, requireTrusted: false, forMutation: true);
        using var source = SetupPayload.OpenSource(path);
        using var buffer = new MemoryStream(); await source.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Equals(previousHash, StringComparison.OrdinalIgnoreCase)) return false;
        await IntegrationSafety.VerifyPublisher(path);
        _ = PatchInstaller.PatchCurrent(bytes, 49152, new string('a', 43));
        var updated = Path.Combine(Root, "launcher-update.exe"); RegularPath(updated);
        if (StorageSafety.FileExists(updated) && !NewerBuild(path, updated)) return false;
        AtomicWrite(updated, bytes, privateFile: true);
        return true;
    }
    internal static bool NewerBuild(string candidate, string previous)
    {
        static string? Version(string path)
        {
            using var directories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(Path.GetFullPath(path))!, requireTrusted: false);
            using var file = SetupPayload.OpenSource(path);
            return FileVersionInfo.GetVersionInfo(path).ProductVersion;
        }
        var version = Version(candidate);
        var old = Version(previous);
        // Commit hashes aren't ordered; Hytale's updater determines which same-day build supersedes another.
        return version != old && string.CompareOrdinal(version?.Split('-')[0], old?.Split('-')[0]) >= 0;
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
    internal static byte[] ReadFile(string path)
    {
        using var directories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(Path.GetFullPath(path))!, requireTrusted: false);
        using var source = SetupPayload.OpenSource(path);
        using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();
    }

    private static void DeleteFile(string path)
    {
        RegularPath(path);
        if (!StorageSafety.FileExists(path)) return;
        using var directories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(path)!, requireTrusted: false);
        using var file = SetupPayload.OpenSource(path, allowDelete: true);
        try { File.Delete(path); }
        catch (IOException ex) { throw StorageSafety.Error(path, ex); }
    }

    public static void AtomicWrite(string path, byte[] bytes, FileSecurity? restorePermissions = null, bool privateFile = false)
    {
        RegularPath(path);
        using var directories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(Path.GetFullPath(path))!, requireTrusted: false, forMutation: true);
        using var destination = StorageSafety.FileExists(path) ? SetupPayload.OpenSource(path, allowDelete: true) : null;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = PrivateFiles.CreateFile(temp))
            {
                file.Write(bytes); file.Flush(true);
                if (privateFile || destination is null)
                {
                    // A failed sign-out transition may restore Hytale's deleted session and its original DACL.
                    if (!privateFile && restorePermissions is not null)
                    {
                        var acl = new FileSecurity();
                        acl.SetSecurityDescriptorBinaryForm(restorePermissions.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                        file.SetAccessControl(acl);
                    }
                    directories.Rename(file.SafeFileHandle, path, replace: privateFile && destination is not null);
                    return;
                }
            }
            // ReplaceFile preserves Hytale's DACL; never fall back to an ACL-losing overwrite.
            File.Replace(temp, path, null);
        }
        catch (IOException ex) { throw StorageSafety.Error(path, ex); }
        finally { if (StorageSafety.FileExists(temp)) File.Delete(temp); }
    }
}
