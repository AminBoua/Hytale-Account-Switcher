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
    private sealed record LauncherUpdate(string ImageHash, string BaseHash);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string HytaleRoot { get; }
    public string Root => Path.Combine(HytaleRoot, "Accounts");
    private string Metadata => Path.Combine(Root, "native-stores.json");
    public NativeCatalog Catalog { get; }
    private readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> pendingFiles = new(StringComparer.OrdinalIgnoreCase);
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
        pendingFiles.Add(store.Folder, new(StringComparer.OrdinalIgnoreCase));
        return store;
    }
    private bool RedundantPending(string folder) => folder.StartsWith("pending-", StringComparison.OrdinalIgnoreCase) &&
        !pending.Contains(folder) && !Catalog.Stores.Any(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) &&
        !string.Equals(Catalog.Active, folder, StringComparison.OrdinalIgnoreCase);
    internal void PrunePending(string folder)
    {
        if (!RedundantPending(folder)) return;
        var root = Folder(folder);
        foreach (var path in new[] { Path.Combine(root, "AppData", "Hytale", ".keys"), Path.Combine(root, "AppData", "Hytale"), Path.Combine(root, "AppData"), root })
        {
            RegularPath(path);
            if (!StorageSafety.DirectoryExists(path)) continue;
            using var parent = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(path)!, requireTrusted: false);
            using (var directory = new SetupPayload.PinnedDirectories(path, requireTrusted: false))
                if (Directory.EnumerateFileSystemEntries(path).Any()) continue;
            Directory.Delete(path); // Nonrecursive: runtime images and unexpected files keep their directories alive.
        }
    }
    public void DiscardPending(NativeStore store)
    {
        pending.Remove(store.Folder);
        CleanupPending();
    }
    internal void ReleasePending(NativeStore store)
    {
        if (!IsPending(store) || Catalog.Stores.Any(s => s.Folder.Equals(store.Folder, StringComparison.OrdinalIgnoreCase))) return;
        var previous = Catalog.Active;
        if (string.Equals(previous, store.Folder, StringComparison.OrdinalIgnoreCase))
        {
            Catalog.Active = null;
            try { Save(); } catch { Catalog.Active = previous; throw; }
        }
        pending.Remove(store.Folder);
        // Shutdown releases staging without making its recovery snapshots or keys disposable.
        pendingFiles.Remove(store.Folder);
        PrunePending(store.Folder);
    }
    internal void CleanupPending()
    {
        foreach (var (folder, files) in pendingFiles.ToArray())
        {
            if (Catalog.Stores.Any(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))) { pendingFiles.Remove(folder); continue; }
            if (pending.Contains(folder)) continue;
            foreach (var file in files.ToArray())
            {
                try { DeleteFile(file); files.Remove(file); }
                // Keep blocked files queued for another attempt after the runtime image is released.
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.TraceWarning(ex.Message); }
            }
            PrunePending(folder);
            if (files.Count == 0) pendingFiles.Remove(folder);
        }
    }
    public NativeStore CommitPending(NativeStore store, Guid profile, string? username)
    {
        if (!IsPending(store) || profile == Guid.Empty) throw new IOException("Pending native login is not verified.");
        var existing = Catalog.Stores.FirstOrDefault(s => s.Profile == profile);
        if (existing is not null)
        {
            VerifyProfile(existing, profile, true, username); pending.Remove(store.Folder);
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
    private static void CopyKeys(string source, string destination, bool privateFiles = false, HashSet<string>? capturedFiles = null)
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
            capturedFiles?.Add(target);
        }
    }
    public void Capture(NativeStore store, bool requireSession = true)
    {
        var storage = Storage(store); PrivateFiles.CreateDirectory(storage);
        using var directories = new SetupPayload.PinnedDirectories(storage, requireTrusted: false, forMutation: true);
        using var nativeDirectories = new SetupPayload.PinnedDirectories(HytaleRoot, requireTrusted: false, forMutation: true);
        pendingFiles.TryGetValue(store.Folder, out var capturedFiles);
        var source = Path.Combine(HytaleRoot, "account.dat"); RegularPath(source);
        var target = Path.Combine(storage, "account.dat"); RegularPath(target);
        var previousBytes = StorageSafety.FileExists(target) ? ReadFile(target) : null;
        var nextBytes = StorageSafety.FileExists(source) ? ReadFile(source) : null;
        if (requireSession && nextBytes is not { Length: > 0 })
            throw new IOException("Native session is missing or empty; previous snapshot preserved.");
        CopyKeys(Path.Combine(HytaleRoot, ".keys"), Path.Combine(storage, ".keys"), privateFiles: true, capturedFiles);
        var previous = Catalog.Active;
        var written = false;
        try
        {
            if (nextBytes is not null) AtomicWrite(target, nextBytes, privateFile: true); else DeleteFile(target);
            capturedFiles?.Add(target);
            written = true;
            Catalog.Active = store.Folder;
            Save();
        }
        catch
        {
            Catalog.Active = previous;
            if (written)
            {
                if (previousBytes is not null) AtomicWrite(target, previousBytes, privateFile: true); else DeleteFile(target);
            }
            throw;
        }
    }
    // Only called after native verification reconciles staging and Capture saves the current session.
    internal void RemoveRedundantPending(NativeStore staging, NativeStore saved)
    {
        if (!RedundantPending(staging.Folder) ||
            string.Equals(Catalog.Selected, staging.Folder, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Catalog.Active, staging.Folder, StringComparison.OrdinalIgnoreCase) ||
            !Catalog.Stores.Contains(saved) || saved.Profile is null || Catalog.Active != saved.Folder) return;
        // Reconciliation must reclassify disposable files; missing captures keep the staging recovery data.
        pendingFiles.Remove(staging.Folder);
        try
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            {
                var source = Storage(staging); var destination = Storage(saved);
                if (!StorageSafety.DirectoryExists(source)) { PrunePending(staging.Folder); return; }
                if (!StorageSafety.FileExists(Path.Combine(destination, "account.dat"))) return;
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
                files.Add(Path.Combine(source, "account.dat"));
            }
            pendingFiles[staging.Folder] = files;
            CleanupPending();
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
        var target = Path.Combine(HytaleRoot, "account.dat"); RegularPath(target);
        if (!restoreSnapshot && Catalog.Active == store.Folder && StorageSafety.FileExists(target))
        {
            using var directories = new SetupPayload.PinnedDirectories(HytaleRoot, requireTrusted: false);
            using var session = SetupPayload.OpenSource(target);
            return;
        }
        var storage = Storage(store); RegularPath(storage);
        using var nativeDirectories = new SetupPayload.PinnedDirectories(HytaleRoot, requireTrusted: false, forMutation: true);
        using var sourceDirectories = new SetupPayload.PinnedDirectories(StorageSafety.DirectoryExists(storage) ? storage : Folder(store.Folder), requireTrusted: false, forMutation: true);
        CopyKeys(Path.Combine(storage, ".keys"), Path.Combine(HytaleRoot, ".keys"));
        var source = Path.Combine(storage, "account.dat"); RegularPath(source);
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
    public async Task<(ProcessStartInfo Start, string Hash, string UpdateBase)> PrepareIntegrated(NativeStore store, LocalAccountBridge bridge)
    {
        RequireIdle();
        var original = IntegrationSafety.OfficialPath;
        using var installedDirectories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(original)!, requireTrusted: false);
        using var installed = SetupPayload.OpenSource(original);
        var updateBase = Hash(ReadFile(original));
        var updated = Path.Combine(Root, "launcher-update.exe");
        RegularPath(updated);
        if (StorageSafety.FileExists(updated) && UseCachedUpdate(updated, original, updateBase)) original = updated;
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
        return (start, Hash(bytes), updateBase);
    }
    internal async Task<bool> PreserveLauncherUpdate(string path, string previousHash, string updateBase)
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
        if (StorageSafety.FileExists(updated) && NewerBuild(updated, path)) return false;
        return SaveLauncherUpdate(bytes, updateBase);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    private LauncherUpdate? ReadLauncherUpdate()
    {
        var path = Path.Combine(Root, "launcher-update.json"); RegularPath(path);
        if (!StorageSafety.FileExists(path)) return null;
        try { return JsonSerializer.Deserialize<LauncherUpdate>(ReadFile(path)); }
        catch (JsonException) { return null; }
    }
    internal bool SaveLauncherUpdate(byte[] bytes, string updateBase)
    {
        var updated = Path.Combine(Root, "launcher-update.exe"); RegularPath(updated);
        var record = new LauncherUpdate(Hash(bytes), updateBase);
        var identical = StorageSafety.FileExists(updated) && Hash(ReadFile(updated)) == record.ImageHash;
        if (identical && ReadLauncherUpdate() == record) return false;
        if (!identical) AtomicWrite(updated, bytes, privateFile: true);
        AtomicWrite(Path.Combine(Root, "launcher-update.json"), JsonSerializer.SerializeToUtf8Bytes(record, Json), privateFile: true);
        return true;
    }
    internal bool UseCachedUpdate(string candidate, string official, string officialHash)
    {
        if (NewerBuild(candidate, official)) return true;
        // Hytale's updater supplies same-day ordering only for the exact installed build it replaced.
        var record = ReadLauncherUpdate();
        return record?.BaseHash == officialHash && record.ImageHash == Hash(ReadFile(candidate)) && record.ImageHash != officialHash;
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
        return NewerVersion(version, old);
    }
    internal static bool NewerVersion(string? candidate, string? previous) =>
        DateOnly.TryParseExact(candidate?.Split('-')[0], "yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) &&
        DateOnly.TryParseExact(previous?.Split('-')[0], "yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var old) && date > old;
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
