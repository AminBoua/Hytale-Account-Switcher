using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Checks")]
namespace HytaleAccounts;

public sealed record PatchRecord(int Schema, string Target, string OriginalHash, string PatchedHash,
    string HelperHash, int Port, string Capability, string Phase, string? OriginalPermissions = null);

// State and backup inherit Program Files permissions. Never store official bytes in a release.
public sealed class PatchInstaller
{
    public string Target { get; }
    public string DirectoryPath { get; }
    public string HelperPath => Path.Combine(DirectoryPath, "HytaleAccounts.exe");
    public string BackupPath => Path.Combine(DirectoryPath, IntegrationSafety.SupportedHash + ".original.exe");
    private string Journal => Path.Combine(DirectoryPath, "install.json");
    private string Stage => Path.Combine(DirectoryPath, "launcher.stage");
    private readonly Func<string, Task> publisher;
    internal Action<string>? Checkpoint;
    public PatchInstaller(string? target = null) : this(target ?? IntegrationSafety.OfficialPath, IntegrationSafety.VerifyPublisher) { }
    internal PatchInstaller(string target, Func<string, Task> publisher)
    {
        Target = Path.GetFullPath(target);
        DirectoryPath = Path.Combine(Path.GetDirectoryName(Target)!, ".hytale-accounts");
        this.publisher = publisher;
        SafePaths();
    }
    private void SafePaths()
    {
        // Reject junctions/symlinks in every ancestor and every owned file before writing.
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(Target)!); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Launcher path contains a reparse point.");
        if (Directory.Exists(DirectoryPath) && (File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Installer state is a reparse point.");
        foreach (var path in new[] { Target, BackupPath, Journal, Stage, HelperPath, Path.Combine(DirectoryPath, "operation.lock") })
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installer file is a reparse point.");
    }
    public static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string Permissions(string path) => new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
        .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
    private void RequirePermissions(string expected)
    {
        if (Permissions(Target) != expected) throw new IOException("Launcher permissions changed; operation stopped. Files were preserved.");
    }
    private static void RequireHash(string path, string hash)
    { if (!File.Exists(path) || Hash(path) != hash) throw new IOException("File verification failed: " + path + ". Files were preserved."); }
    private FileStream Lock()
    {
        SafePaths(); Directory.CreateDirectory(DirectoryPath);
        return new FileStream(Path.Combine(DirectoryPath, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public PatchRecord Read()
    {
        SafePaths();
        if (!File.Exists(Journal)) throw new IOException("Account patch installation state is missing. Patches are disabled. Run Install.ps1 to reinstall; if the launcher is already modified, reinstall the official launcher first. Files were preserved.");
        var record = JsonSerializer.Deserialize<PatchRecord>(File.ReadAllBytes(Journal)) ?? throw new IOException("Invalid installer journal.");
        if (record.Schema is not (1 or 2) || (record.Schema == 2 && string.IsNullOrWhiteSpace(record.OriginalPermissions)) || record.Target != Target || record.OriginalHash != IntegrationSafety.SupportedHash ||
            !Regex.IsMatch(record.PatchedHash, "^[a-f0-9]{64}$") || !Regex.IsMatch(record.HelperHash, "^[a-f0-9]{64}$") ||
            record.Port is < 1024 or > 65535 || !Regex.IsMatch(record.Capability, "^[A-Za-z0-9_-]{43}$") ||
            record.Phase is not ("prepared" or "installed" or "restoring" or "uninstalled"))
            throw new IOException("Installer journal does not match this launcher. Files were preserved.");
        return record;
    }
    private void Save(PatchRecord record)
    {
        NativeStores.AtomicWrite(Journal, JsonSerializer.SerializeToUtf8Bytes(record));
        Checkpoint?.Invoke(record.Phase);
    }
    public static byte[] Patch(byte[] original, int port, string capability)
    {
        if (Hash(original) != IntegrationSafety.SupportedHash) throw new IOException($"Unsupported original launcher build. Patches are disabled. Install a patcher supporting the current build; this release supports only {IntegrationSafety.SupportedVersion}.");
        if (port is < 1024 or > 65535 || !Regex.IsMatch(capability, "^[A-Za-z0-9_-]{43}$")) throw new IOException("Invalid bridge bootstrap.");
        var text = Encoding.Latin1.GetString(original);
        const string title = "<title>Hytale Launcher</title>";
        var titleAt = text.IndexOf(title, StringComparison.Ordinal);
        if (titleAt < 0 || text.LastIndexOf(title, StringComparison.Ordinal) != titleAt) throw new IOException("Ambiguous embedded frontend.");
        var start = text.LastIndexOf("<html", titleAt, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf("</html>", start, StringComparison.Ordinal);
        if (end < titleAt) throw new IOException("Missing embedded frontend boundary.");
        end += 7;
        var html = Encoding.ASCII.GetBytes("<html><head><script type=\"module\" src=\"/assets/index-C4Qf_qVT.js\"></script>" +
            "<link rel=\"stylesheet\" href=\"/assets/index-B7RSYP87.css\"></head><body><div id=\"app\"></div>" +
            $"<script src=\"http://127.0.0.1:{port}/{capability}/ui.js\"></script></body></html>");
        if (html.Length > end - start) throw new IOException("Bootstrap exceeds frontend allocation.");
        var patched = (byte[])original.Clone();
        patched.AsSpan(start, end - start).Fill((byte)' '); html.CopyTo(patched, start);
        // Exact-build adapter for CheckNetworkMode(false, "hya:..."). Other calls
        // tail-call the original network check. Hytale still owns every session operation.
        // Go ABIInternal: AX=service receiver, CX=source string, DI=string length.
        // stop -> native Shutdown (no logout/revocation); load -> native auth.Init;
        // run -> native StartUserTasks. No injected stack frame or Go allocation.
        // CheckNetworkMode(true, "hya:stop") calls native Controller.Init to create
        // an empty native auth controller after logout; it does not revoke another account.
        var reload = Convert.FromHexString("488B004883FF08752D48BA6879613A73746F70483911742348BA6879613A6C6F6164483911741D48BA6879613A72756E214839117417E9C5E7FFFF84DB7513E99CD5FFFF488B4008E9D30EFFFFE9CEEDFFFFE9C9D3FFFF");
        patched.AsSpan(0x43fca0, 0x60).Fill(0xcc); reload.CopyTo(patched, 0x43fca0);
        // The adapter is a leaf with SP unchanged and no asynchronous safe points.
        // Reuse native constant pc tables: SP=0 and UnsafePointUnsafe for 96 bytes.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(0x1536730, 4), 0x124bd5a - 0x12042e8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(0x153674c, 4), 0x124bd6e - 0x12042e8);
        return patched;
    }
    internal static byte[] PatchCurrent(byte[] original, int port, string capability)
    {
        if (port is < 1024 or > 65535 || !Regex.IsMatch(capability, "^[A-Za-z0-9_-]{43}$")) throw new IOException("Invalid bridge bootstrap.");
        var text = Encoding.Latin1.GetString(original);
        foreach (var hook in new[] { "window.go.account.Service.CheckNetworkMode", "window.go.account.Service.GenerateOauthURL",
            "window.go.account.Service.IsUserLoggedIn", "window.go.account.Service.LogoutUser", "window.go.account.Service.HasValidSession",
            "launch-game__profile-dropdown", "hy-dropdown__list", "login__container", "settings__action-button", "getUserProfile", "hybutton-split__main" })
            if (!text.Contains(hook, StringComparison.Ordinal)) throw new IOException("Updated launcher frontend account interface changed; files and accounts preserved.");
        const string title = "<title>Hytale Launcher</title>";
        var titleAt = text.IndexOf(title, StringComparison.Ordinal);
        if (titleAt < 0 || text.LastIndexOf(title, StringComparison.Ordinal) != titleAt) throw new IOException("Ambiguous embedded frontend.");
        var start = text.LastIndexOf("<html", titleAt, StringComparison.Ordinal);
        var end = start < 0 ? -1 : text.IndexOf("</html>", titleAt, StringComparison.Ordinal);
        if (end < titleAt) throw new IOException("Missing embedded frontend boundary.");
        end += 7;
        // Drop preload hints and formatting to make room, preserving the updated launcher's own assets.
        var html = Regex.Replace(text[start..end], "<link\\b[^>]*rel=\"modulepreload\"[^>]*>", "", RegexOptions.CultureInvariant);
        html = Regex.Replace(html, @">\s+<", "><", RegexOptions.CultureInvariant);
        html = html.Replace("</body>", $"<script src=\"http://127.0.0.1:{port}/{capability}/ui.js\"></script></body>", StringComparison.Ordinal);
        if (!html.Contains($"/{capability}/ui.js", StringComparison.Ordinal) || html.Length > end - start)
            throw new IOException("Updated launcher frontend has no room for the addon bootstrap.");
        var patched = (byte[])original.Clone();
        NativeAdapter.Apply(patched);
        patched.AsSpan(start, end - start).Fill((byte)' ');
        Encoding.Latin1.GetBytes(html).CopyTo(patched, start);
        return patched;
    }
    private async Task VerifyBackup(PatchRecord record)
    {
        RequireHash(BackupPath, record.OriginalHash); await publisher(BackupPath);
        RequireHash(BackupPath, record.OriginalHash);
        if (Hash(Patch(File.ReadAllBytes(BackupPath), record.Port, record.Capability)) != record.PatchedHash)
            throw new IOException("Patch manifest is inconsistent with the verified original.");
    }
    private void Swap(byte[] bytes, string expectedCurrent, string expectedResult, string? originalPermissions)
    {
        SafePaths();
        var permissions = originalPermissions ?? Permissions(Target); // Legacy journals have no historical ACL baseline.
        RequirePermissions(permissions);
        NativeStores.AtomicWrite(Stage, bytes); RequireHash(Stage, expectedResult);
        Checkpoint?.Invoke("staged");
        SafePaths();
        RequireHash(Target, expectedCurrent);
        RequirePermissions(permissions);
        // ReplaceFile preserves the original target ACL; both files are on the same volume.
        File.Replace(Stage, Target, null);
        Checkpoint?.Invoke("replaced"); RequireHash(Target, expectedResult); RequirePermissions(permissions);
    }
    public async Task CheckOriginal()
    {
        SafePaths();
        if (!File.Exists(Target) || !IntegrationSafety.Compatible(FileVersionInfo.GetVersionInfo(Target).ProductVersion ?? "", Hash(Target)))
            throw new IOException(IntegrationSafety.CompatibilityMessage(Target));
        await publisher(Target); RequireHash(Target, IntegrationSafety.SupportedHash);
        _ = Patch(File.ReadAllBytes(Target), 49152, new string('a', 43));
    }
    public async Task Install(string helper)
    {
        using var gate = Lock();
        if (File.Exists(Journal))
        {
            var old = await RecoverCore();
            if (old.Phase == "installed")
            {
                // Reinstall can repair/upgrade the helper without changing the launcher or bootstrap.
                RequireHash(Target, old.PatchedHash);
                var replacement = File.ReadAllBytes(helper);
                var helperHash = Hash(replacement);
                if (!File.Exists(HelperPath) || Hash(HelperPath) != helperHash)
                {
                    NativeStores.AtomicWrite(HelperPath, replacement); RequireHash(HelperPath, helperHash);
                    Checkpoint?.Invoke("helper");
                }
                if (old.HelperHash != helperHash) Save(old = old with { HelperHash = helperHash });
                VerifyInstalled(old); return;
            }
        }
        await CheckOriginal();
        var permissions = Permissions(Target);
        var original = File.ReadAllBytes(Target);
        if (Hash(original) != IntegrationSafety.SupportedHash) throw new IOException("Official launcher changed before installation; files preserved.");
        if (File.Exists(BackupPath)) RequireHash(BackupPath, IntegrationSafety.SupportedHash);
        else NativeStores.AtomicWrite(BackupPath, original);
        Checkpoint?.Invoke("backup");
        RequireHash(BackupPath, IntegrationSafety.SupportedHash); await publisher(BackupPath);
        var helperBytes = File.ReadAllBytes(helper);
        NativeStores.AtomicWrite(HelperPath, helperBytes); RequireHash(HelperPath, Hash(helperBytes));
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var capability = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var patched = Patch(original, port, capability);
        var record = new PatchRecord(2, Target, IntegrationSafety.SupportedHash, Hash(patched), Hash(helperBytes), port, capability, "prepared", permissions);
        Save(record); await VerifyBackup(record);
        Swap(patched, record.OriginalHash, record.PatchedHash, record.OriginalPermissions);
        Save(record with { Phase = "installed" });
    }
    public async Task<PatchRecord> Recover() { using var gate = Lock(); return await RecoverCore(); }
    private async Task<PatchRecord> RecoverCore()
    {
        if (!File.Exists(Journal))
        {
            // A crash before the first journal cannot have replaced the launcher.
            await CheckOriginal();
            if (File.Exists(BackupPath)) { RequireHash(BackupPath, IntegrationSafety.SupportedHash); await publisher(BackupPath); }
            else NativeStores.AtomicWrite(BackupPath, File.ReadAllBytes(Target));
            var capability = new string('a', 43);
            var initial = new PatchRecord(2, Target, IntegrationSafety.SupportedHash,
                Hash(Patch(File.ReadAllBytes(BackupPath), 49152, capability)),
                File.Exists(HelperPath) ? Hash(HelperPath) : Hash(Array.Empty<byte>()), 49152, capability, "uninstalled", Permissions(Target));
            Save(initial); return initial;
        }
        var record = Read();
        if (!File.Exists(Target)) throw new IOException(IntegrationSafety.CompatibilityMessage(Target));
        var current = Hash(Target);
        if (current != record.PatchedHash && current != record.OriginalHash)
        {
            // Retire an obsolete patch even if its old backup is damaged. Never touch the new image or ACL.
            var permissions = Permissions(Target);
            await publisher(Target); RequireHash(Target, current); RequirePermissions(permissions);
            Save(record = record with { Phase = "uninstalled" }); return record;
        }
        await VerifyBackup(record);
        if (record.OriginalPermissions is not null) RequirePermissions(record.OriginalPermissions);
        if (record.Phase == "installed" && current == record.OriginalHash)
            Save(record = record with { Phase = "uninstalled" });
        if (record.Phase is "prepared" or "restoring")
        {
            // An interrupted install rolls back, whether the swap happened or not.
            if (current == record.PatchedHash) Swap(File.ReadAllBytes(BackupPath), current, record.OriginalHash, record.OriginalPermissions);
            else RequireHash(Target, record.OriginalHash);
            Save(record = record with { Phase = "uninstalled" });
        }
        return record;
    }
    public async Task Uninstall()
    {
        using var gate = Lock();
        var record = await RecoverCore();
        var current = Hash(Target);
        if (current != record.PatchedHash)
        {
            // A publisher-verified update is the current original. Never downgrade it.
            if (current != record.OriginalHash) { await publisher(Target); RequireHash(Target, current); }
            Save(record with { Phase = "uninstalled" }); return;
        }
        Save(record = record with { Phase = "restoring" });
        Swap(File.ReadAllBytes(BackupPath), current, record.OriginalHash, record.OriginalPermissions);
        await publisher(Target); Save(record with { Phase = "uninstalled" });
    }
    public void VerifyInstalled(PatchRecord record)
    {
        SafePaths();
        var current = File.Exists(Target) ? Hash(Target) : null;
        if (current is null || (current != record.PatchedHash && current != record.OriginalHash))
            throw new IOException(IntegrationSafety.CompatibilityMessage(Target));
        if (record.Phase != "installed" || current != record.PatchedHash)
            throw new IOException("The account patch is not installed or installation was interrupted. Patches are disabled. Run Install.ps1 -Recover, then Install.ps1 to reinstall. An unsupported updated launcher requires a compatible patcher release. Files were preserved.");
        if (record.OriginalPermissions is not null) RequirePermissions(record.OriginalPermissions);
        if (!File.Exists(HelperPath) || Hash(HelperPath) != record.HelperHash)
            throw new IOException("The account helper is missing, changed, or its update was interrupted. Patches are disabled. Close Hytale with Accounts and run Install.ps1 from the complete patcher archive to reinstall the helper. Launcher files were preserved.");
        RequireHash(BackupPath, record.OriginalHash);
        if (Hash(Patch(File.ReadAllBytes(BackupPath), record.Port, record.Capability)) != record.PatchedHash)
            throw new IOException("Installed patch does not match its verified original and bootstrap.");
    }
}
