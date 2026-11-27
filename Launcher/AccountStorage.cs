using System.Diagnostics;
using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Win32;

namespace HytaleAccounts;

public sealed record NativeStore(string Folder, string Label, Guid? Profile = null, bool SignedOut = false);

public sealed class NativeCatalog
{
    public List<NativeStore> Stores { get; set; } = [];
    public string? Selected { get; set; }
    public string? Active { get; set; }
}

public sealed class AccountStorage
{
    private const string PendingPrefix = "pending-";
    private const string SessionName = "account.dat";
    private const string KeysName = ".keys";
    private static readonly StringComparer Compare = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Dictionary<string, HashSet<string>> pending = new(Compare);
    private readonly HashSet<string> recovery = new(Compare);

    public string HytaleRoot { get; }
    public string Root => Path.Combine(HytaleRoot, "Accounts");
    public NativeCatalog Catalog { get; }
    private string Metadata => Path.Combine(Root, "native-stores.json");

    public AccountStorage(string? root = null)
    {
        HytaleRoot = Path.GetFullPath(root ?? OfficialRoot());
        WindowsPermissions.SecureTree(Root);
        using var pin = new WindowsFiles.DirectoryPin(Root, forMutation: true);
        Catalog = ReadCatalog();
        foreach (var store in Catalog.Stores) Folder(store.Folder);
        if (Catalog.Stores.Select(store => store.Folder).Distinct(Compare).Count() != Catalog.Stores.Count)
            throw new IOException("Account metadata has duplicate folders. Files were preserved.");
        if (Catalog.Active is not null) Folder(Catalog.Active);

        foreach (var directory in Directory.EnumerateDirectories(Root).Order(Compare))
        {
            var id = Path.GetFileName(directory);
            if (id.StartsWith(PendingPrefix, StringComparison.OrdinalIgnoreCase) || Find(id) >= 0) continue;
            var store = new NativeStore(id, id);
            if (WindowsFiles.FileExists(Path.Combine(Snapshot(store), SessionName))) Catalog.Stores.Add(store);
        }
        if (Catalog.Selected is not null)
        {
            var selected = Find(Catalog.Selected);
            if (selected < 0) throw new IOException("The selected account is missing from metadata. Files were preserved.");
            Catalog.Selected = Catalog.Stores[selected].Folder;
        }
        if (Catalog.Active is not null)
        {
            var active = Find(Catalog.Active);
            if (active >= 0) Catalog.Active = Catalog.Stores[active].Folder;
        }
        Save();
    }

    private NativeCatalog ReadCatalog()
    {
        if (!WindowsFiles.FileExists(Metadata)) return new();
        try
        {
            var catalog = JsonSerializer.Deserialize<NativeCatalog>(WindowsFiles.Read(Metadata), Json);
            if (catalog?.Stores is null || catalog.Stores.Any(store => store is null))
                throw new IOException("Invalid account metadata. Files were preserved.");
            return catalog;
        }
        catch (JsonException ex) { throw new IOException("Invalid account metadata. Files were preserved.", ex); }
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
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || !id.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            throw new IOException("Invalid native account folder.");
        var path = Path.Combine(Root, id);
        WindowsFiles.CheckPath(path);
        return path;
    }

    private string Snapshot(NativeStore store) => Path.Combine(Folder(store.Folder), "AppData", "Hytale");
    private int Find(string folder) => Catalog.Stores.FindIndex(store => Compare.Equals(store.Folder, folder));
    private void Save() => WindowsFiles.Write(Metadata, JsonSerializer.SerializeToUtf8Bytes(Catalog, Json), privateFile: true);

    public NativeStore CreatePending()
    {
        var store = new NativeStore(PendingPrefix + Guid.NewGuid().ToString("N"), "New native account");
        WindowsPermissions.CreatePrivateDirectory(Folder(store.Folder));
        pending.Add(store.Folder, new(Compare));
        return store;
    }

    public bool IsPending(NativeStore store) => pending.ContainsKey(store.Folder);

    public NativeStore CommitPending(NativeStore store, Guid profile, string? username)
    {
        if (!IsPending(store) || profile == Guid.Empty) throw new IOException("Pending native login is unverified.");
        var existing = Catalog.Stores.Find(item => item.Profile == profile);
        if (existing is not null)
        {
            VerifyProfile(existing, profile, select: true, username);
            pending.Remove(store.Folder);
            recovery.Remove(store.Folder);
            return Catalog.Stores[Find(existing.Folder)];
        }

        Catalog.Stores.Add(store);
        try { VerifyProfile(store, profile, select: true, username); }
        catch { Catalog.Stores.Remove(store); throw; }
        pending.Remove(store.Folder);
        recovery.Remove(store.Folder);
        return Catalog.Stores[Find(store.Folder)];
    }

    public void VerifyProfile(NativeStore store, Guid profile, bool select, string? username = null)
    {
        var at = Find(store.Folder);
        if (at < 0) throw new IOException("Unknown native account.");
        var previous = Catalog.Stores[at];
        if (profile == Guid.Empty || (previous.Profile is not null && previous.Profile != profile))
            throw new IOException("Native profile does not match this account. The previous selection was kept.");

        var label = previous.Label;
        if ((label == "New native account" || label == previous.Folder) &&
            username is { Length: > 0 and <= 80 } && !username.Any(char.IsControl))
            label = username;

        var verified = previous with { Profile = profile, Label = label, SignedOut = false };
        var selected = Catalog.Selected;
        if (verified == previous && (!select || Compare.Equals(selected, previous.Folder))) return;
        Catalog.Stores[at] = verified;
        if (select) Catalog.Selected = previous.Folder;
        try { Save(); }
        catch { Catalog.Stores[at] = previous; Catalog.Selected = selected; throw; }
    }

    public void MarkSignedOut(NativeStore store)
    {
        var at = Find(store.Folder);
        if (at < 0) throw new IOException("Unknown native account.");
        var previous = Catalog.Stores[at];
        var selected = Catalog.Selected;
        Catalog.Stores[at] = previous with { SignedOut = true };
        if (Compare.Equals(selected, previous.Folder)) Catalog.Selected = null;
        try { Save(); }
        catch { Catalog.Stores[at] = previous; Catalog.Selected = selected; throw; }
    }

    public void Capture(NativeStore store, bool requireSession = true)
    {
        if (!IsPending(store) && Find(store.Folder) < 0) throw new IOException("Unknown native account.");
        var destination = Snapshot(store);
        using var native = new WindowsFiles.DirectoryPin(HytaleRoot, forMutation: true);
        var bytes = ReadIfPresent(Path.Combine(HytaleRoot, SessionName));
        if (requireSession && bytes is not { Length: > 0 })
            throw new IOException("Native session is missing or empty. The previous snapshot was preserved.");

        WindowsPermissions.CreatePrivateDirectory(destination);
        using var snapshot = new WindowsFiles.DirectoryPin(destination, forMutation: true);
        pending.TryGetValue(store.Folder, out var captured);
        CopyKeys(store, HytaleRoot, destination, privateFiles: true, captured);
        var target = Path.Combine(destination, SessionName);
        var previous = ReadIfPresent(target);
        WriteSession(target, bytes, privateFile: true);
        try
        {
            SetActive(store.Folder);
            captured?.Add(target);
        }
        catch
        {
            WriteSession(target, previous, privateFile: true);
            throw;
        }
    }

    public void Activate(NativeStore store, bool restoreSnapshot = false)
    {
        var at = Find(store.Folder);
        if (at >= 0) store = Catalog.Stores[at];
        else if (!IsPending(store)) throw new IOException("Unknown native account.");

        var target = Path.Combine(HytaleRoot, SessionName);
        using var native = new WindowsFiles.DirectoryPin(HytaleRoot, forMutation: true);
        if (!restoreSnapshot && !store.SignedOut && Compare.Equals(Catalog.Active, store.Folder) && WindowsFiles.FileExists(target))
        {
            // On restart, keep the active session Hytale may have refreshed since its last capture.
            using var validated = WindowsFiles.Open(target);
            return;
        }

        var source = Snapshot(store);
        using var snapshot = new WindowsFiles.DirectoryPin(WindowsFiles.DirectoryExists(source) ? source : Folder(store.Folder), forMutation: true);
        CopyKeys(store, source, HytaleRoot, privateFiles: false);
        var next = store.SignedOut ? null : ReadIfPresent(Path.Combine(source, SessionName));
        byte[]? previous = null;
        FileSecurity? previousAcl = null;
        if (WindowsFiles.FileExists(target))
        {
            using var file = WindowsFiles.Open(target);
            using var buffer = new MemoryStream();
            file.CopyTo(buffer);
            previous = buffer.ToArray();
            previousAcl = file.GetAccessControl();
        }
        WriteSession(target, next, privateFile: false);
        try { SetActive(store.Folder); }
        catch
        {
            WriteSession(target, previous, privateFile: false, previousAcl);
            throw;
        }
    }

    private void SetActive(string? folder)
    {
        var previous = Catalog.Active;
        if (Compare.Equals(previous, folder)) return;
        Catalog.Active = folder;
        try { Save(); }
        catch { Catalog.Active = previous; throw; }
    }

    private static byte[]? ReadIfPresent(string path) => WindowsFiles.FileExists(path) ? WindowsFiles.Read(path) : null;

    private static void WriteSession(string path, byte[]? bytes, bool privateFile, FileSecurity? permissions = null)
    {
        if (bytes is null) WindowsFiles.Delete(path);
        else WindowsFiles.Write(path, bytes, privateFile, permissions);
    }

    private void CopyKeys(NativeStore store, string source, string destination, bool privateFiles, HashSet<string>? captured = null)
    {
        source = Path.Combine(source, KeysName);
        destination = Path.Combine(destination, KeysName);
        if (!WindowsFiles.DirectoryExists(source)) return;
        using var from = new WindowsFiles.DirectoryPin(source, forMutation: true);
        var copies = new List<(string Target, byte[] Bytes, bool Exists)>();
        var destinationExists = WindowsFiles.DirectoryExists(destination);
        using var to = destinationExists ? new WindowsFiles.DirectoryPin(destination, forMutation: true) : null;
        foreach (var key in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(key));
            var bytes = WindowsFiles.Read(key);
            var previous = destinationExists ? ReadIfPresent(target) : null;
            if (previous is not null && !previous.AsSpan().SequenceEqual(bytes))
            {
                if (IsPending(store)) recovery.Add(store.Folder);
                if (Catalog.Active is { } active && pending.ContainsKey(active)) recovery.Add(active);
                throw new IOException("Native key conflict. The session and recovery files were preserved.");
            }
            copies.Add((target, bytes, previous is not null));
        }
        if (copies.Count == 0) return;
        if (!destinationExists) WindowsPermissions.CreatePrivateDirectory(destination);
        foreach (var (target, bytes, exists) in copies)
        {
            if (!exists || privateFiles) WindowsFiles.Write(target, bytes, privateFile: privateFiles);
            captured?.Add(target);
        }
    }

    public void DiscardPending(NativeStore store)
    {
        if (!IsPending(store) || Find(store.Folder) >= 0) return;
        if (recovery.Contains(store.Folder) || Compare.Equals(Catalog.Active, store.Folder))
        {
            // An unfinished account may still contain the only recoverable snapshot.
            ReleasePending(store);
            return;
        }
        var captured = pending[store.Folder];
        pending.Remove(store.Folder);
        CleanupPending(store, captured);
    }

    public void ReleasePending(NativeStore store)
    {
        if (!IsPending(store) || Find(store.Folder) >= 0) return;
        if (Compare.Equals(Catalog.Active, store.Folder)) SetActive(null);
        pending.Remove(store.Folder);
        recovery.Remove(store.Folder);
        CleanupPending(store, []);
    }

    public void RemoveRedundantPending(NativeStore staging, NativeStore saved)
    {
        if (!staging.Folder.StartsWith(PendingPrefix, StringComparison.OrdinalIgnoreCase) || IsPending(staging) ||
            Find(staging.Folder) >= 0 || saved.Profile is null || Find(saved.Folder) < 0 ||
            !Compare.Equals(Catalog.Active, saved.Folder)) return;

        try
        {
            var source = Snapshot(staging);
            if (!WindowsFiles.DirectoryExists(source))
            {
                PrunePending(staging);
                return;
            }

            var destination = Snapshot(saved);
            var captured = new List<string>();
            using (var stagingPin = new WindowsFiles.DirectoryPin(source))
            using (var savedPin = new WindowsFiles.DirectoryPin(destination))
            {
                if (ReadIfPresent(Path.Combine(destination, SessionName)) is not { Length: > 0 }) return;
                var keys = Path.Combine(source, KeysName);
                if (WindowsFiles.DirectoryExists(keys))
                {
                    using var keyPin = new WindowsFiles.DirectoryPin(keys);
                    foreach (var key in Directory.EnumerateFiles(keys))
                    {
                        var target = Path.Combine(destination, KeysName, Path.GetFileName(key));
                        var kept = ReadIfPresent(target);
                        if (kept is null || !kept.AsSpan().SequenceEqual(WindowsFiles.Read(key))) return;
                        captured.Add(key);
                    }
                }
                captured.Add(Path.Combine(source, SessionName));
            }
            CleanupPending(staging, captured);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Pending recovery files were kept. " + ex.Message);
        }
    }

    private void CleanupPending(NativeStore store, IEnumerable<string> captured)
    {
        try
        {
            foreach (var file in captured) WindowsFiles.Delete(file);
            PrunePending(store);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Remaining pending recovery files were kept. " + ex.Message);
        }
    }

    private void PrunePending(NativeStore store)
    {
        var snapshot = Snapshot(store);
        WindowsFiles.DeleteEmptyDirectory(Path.Combine(snapshot, KeysName));
        WindowsFiles.DeleteEmptyDirectory(snapshot);
        WindowsFiles.DeleteEmptyDirectory(Path.GetDirectoryName(snapshot)!);
        WindowsFiles.DeleteEmptyDirectory(Folder(store.Folder));
    }
}
