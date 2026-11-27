using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HytaleAccounts;

internal static class Deployment
{
    internal const string Executable = "HytaleAccounts.exe";
    internal const string SetupName = "HytaleAccounts-Setup.exe";
    internal const string Manifest = "addon-payload.json";
    internal const string StagePrefix = "HytaleAccounts-stage-";
    internal const string Repair = "--repair-integration";
    internal const string UserRepair = "--repair-integration-user";
    internal static string Destination => Path.Combine(Path.GetDirectoryName(LauncherImage.OfficialPath)!, ".hytale-accounts-addon");
    internal static string Installed => Path.Combine(Destination, Executable);
    internal static string PowerShell => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HytaleAccounts";
    private const string TaskName = "HytaleAccounts-Repair";
    private static readonly string[] Required = [Executable, "LICENSE", "THIRD-PARTY-NOTICES.txt"];
    private static readonly string[] Legacy = ["D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll",
        "Install.ps1", "Repair-Integration.ps1", "launcher-baseline.json", "Uninstall.ps1", "InstallerSafety.ps1", "ShortcutIntegration.ps1", "ShortcutFile.cs"];

    internal sealed record PayloadFile(string Name, string Sha256);
    internal sealed record Payload(string Version, PayloadFile[] Files);

    internal sealed class PackageSources : IDisposable
    {
        private readonly WindowsFiles.DirectoryPin directory;
        private readonly List<FileStream> handles = [];
        internal string ManifestHash { get; }
        internal PackageSources(string source, bool download)
        {
            directory = new WindowsFiles.DirectoryPin(source, machine: !download, forMutation: !download);
            try
            {
                var manifest = Open(Manifest);
                using var buffer = new MemoryStream();
                manifest.CopyTo(buffer);
                var bytes = buffer.ToArray();
                ManifestHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                foreach (var file in ParsePayload(bytes).Files)
                {
                    var name = download && file.Name == Executable ? SetupName : file.Name;
                    if (Convert.ToHexStringLower(SHA256.HashData(Open(name))) != file.Sha256)
                        throw new IOException($"Package integrity check failed for {name}.");
                }
            }
            catch { Dispose(); throw; }

            FileStream Open(string name)
            {
                var handle = WindowsFiles.Open(Path.Combine(source, name));
                handles.Add(handle);
                return handle;
            }
        }

        public void Dispose() { foreach (var handle in handles) handle.Dispose(); directory.Dispose(); }
    }

    internal static Payload ReadPayload(string directory)
    {
        var payload = ParsePayload(WindowsFiles.Read(Path.Combine(directory, Manifest)));
        foreach (var file in payload.Files)
            if (Digest(Path.Combine(directory, file.Name)) != file.Sha256) throw new IOException($"Package integrity check failed for {file.Name}.");
        return payload;
    }

    internal static Payload ParsePayload(byte[] bytes)
    {
        var payload = JsonSerializer.Deserialize<Payload>(bytes) ?? throw new IOException("The package manifest is empty.");
        if (!Version.TryParse(payload.Version, out _) || payload.Files is null || payload.Files.Length > 100 ||
            payload.Files.Any(file => file is null || file.Name is null || (!Required.Contains(file.Name, StringComparer.Ordinal) && !IsSidecar(file.Name)) ||
                file.Sha256 is null || file.Sha256.Length != 64 || file.Sha256.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9'))) ||
            payload.Files.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Files.Length ||
            Required.Except(payload.Files.Select(file => file.Name), StringComparer.Ordinal).Any())
            throw new IOException("The package manifest contains an unsupported file list or hash.");
        return payload;
    }

    private static bool IsSidecar(string name) => name.Length is > 4 and <= 100 && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        char.IsAsciiLetterOrDigit(name[0]) && name[..^4].All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') &&
        !name.Contains("..", StringComparison.Ordinal) &&
        !System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static string Digest(string path)
    {
        using var source = WindowsFiles.Open(path);
        return Convert.ToHexStringLower(SHA256.HashData(source));
    }

    private static string[] PayloadNames(Payload payload) =>
        payload.Files.Select(file => file.Name).Where(name => name != Executable).Append(Manifest).Append(Executable).ToArray();

    internal static async Task<Payload?> RecoverPayload(WindowsFiles.DirectoryPin directory, string? registeredVersion,
        bool allowRepair = false)
    {
        var backups = Directory.EnumerateFileSystemEntries(directory.PathName, ".upgrade-*.bak")
            .Select(Path.GetFileName).OfType<string>().Where(name => IsUpgradeArtifact(name, null)).ToHashSet(StringComparer.Ordinal);
        Payload? live = null;
        Exception? liveError = null;
        bool liveVerified = false;
        try
        {
            live = ReadManifest(Manifest);
            CheckFiles(live, name => name);
            liveVerified = true;
            if (registeredVersion is null || registeredVersion == live.Version || backups.Count == 0) return live;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { liveError = error; }

        var candidates = new List<(Payload Payload, string Prefix)>();
        foreach (var prefix in backups.Select(name => name[..42]).Distinct(StringComparer.Ordinal))
        {
            string Select(string name) => BackupName(prefix, name);
            try
            {
                var candidate = ReadManifest(Select(Manifest));
                if ((registeredVersion is not null && candidate.Version != registeredVersion) ||
                    !PayloadNames(candidate).Any(name => Select(name) != name)) continue;
                CheckFiles(candidate, Select);
                candidates.Add((candidate, prefix));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { liveError ??= error; }
        }

        if (candidates.Count == 1)
        {
            var (candidate, prefix) = candidates[0];
            var names = PayloadNames(candidate).Where(name => BackupName(prefix, name) != name).ToArray();
            var retired = liveVerified ? PayloadNames(live!).Except(PayloadNames(candidate), StringComparer.OrdinalIgnoreCase).ToArray() : [];
            await Publish(directory, names, retired, name => OpenRequired(BackupName(prefix, name)), () => Task.CompletedTask);
            return candidate;
        }

        if (backups.Count == 0 && allowRepair && live is not null) return live;
        if (backups.Count == 0 && live is null && liveError is FileNotFoundException)
        {
            using var installed = WindowsFiles.OpenPayloadFile(directory, Executable);
            using var legacy = WindowsFiles.OpenPayloadFile(directory, "Repair-Integration.ps1");
            if (installed is null || legacy is not null || (allowRepair && registeredVersion is not null)) return null;
        }

        throw new IOException("Setup could not verify the previous addon payload. Its recovery files were preserved.", liveError);

        string BackupName(string prefix, string name) => backups.Contains(prefix + name + ".bak") ? prefix + name + ".bak" : name;

        FileStream OpenRequired(string name) => WindowsFiles.OpenPayloadFile(directory, name) ??
            throw new FileNotFoundException("An addon file is missing.", Path.Combine(directory.PathName, name));

        Payload ReadManifest(string name)
        {
            using var stream = OpenRequired(name);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return ParsePayload(buffer.ToArray());
        }

        void CheckFiles(Payload payload, Func<string, string> select)
        {
            foreach (var file in payload.Files)
            {
                using var stream = OpenRequired(select(file.Name));
                if (Convert.ToHexStringLower(SHA256.HashData(stream)) != file.Sha256)
                    throw new IOException($"The addon file {file.Name} did not pass its integrity check.");
            }
        }
    }

    internal static void CleanUpgradeArtifacts(WindowsFiles.DirectoryPin directory, string[] names)
    {
        var handles = new List<(string Name, SafeFileHandle Handle)>();
        try
        {
            foreach (var name in Directory.EnumerateFileSystemEntries(directory.PathName, ".upgrade-*").Select(Path.GetFileName).OfType<string>())
            {
                if (!IsUpgradeArtifact(name, names)) continue;
                var handle = WindowsFiles.OpenUpgradeArtifact(directory, name);
                if (handle is not null) handles.Add((name, handle));
            }
            foreach (var (name, handle) in handles) WindowsFiles.DeleteUpgradeArtifact(directory, name, handle);
        }
        finally { foreach (var (_, handle) in handles) handle.Dispose(); }
    }

    private static void DeleteArtifact(WindowsFiles.DirectoryPin directory, string name)
    {
        using var handle = WindowsFiles.OpenUpgradeArtifact(directory, name);
        if (handle is not null) WindowsFiles.DeleteUpgradeArtifact(directory, name, handle);
    }

    internal static WindowsFiles.DirectoryPin PinStage()
    {
        var path = Path.GetDirectoryName(Environment.ProcessPath!)!;
        var parent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var name = Path.GetFileName(path);
        if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(StagePrefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.AsSpan(StagePrefix.Length), "N", out _))
            throw new IOException("Setup must run from the protected package stage.");
        return new WindowsFiles.DirectoryPin(path, machine: true);
    }

    internal static async Task InstallAsync(string sid)
    {
        using var stage = PinStage();
        using var maintenance = new Semaphore(1, 1, @"Global\HytaleAccounts-Deployment");
        if (!maintenance.WaitOne(0)) throw new IOException("Another setup or removal is already running.");
        try { await InstallCoreAsync(sid); }
        finally { maintenance.Release(); }
    }

    private static async Task InstallCoreAsync(string sid)
    {
        var source = Path.GetDirectoryName(Environment.ProcessPath!)!;
        var payload = ReadPayload(source);
        await LauncherImage.ValidateOfficialAsync();
        string parent = Path.GetDirectoryName(Destination)!;
        using var installation = new WindowsFiles.DirectoryPin(parent, machine: true, forMutation: true);
        var registeredVersion = ReadRegistration();
        CreateDestination();
        using var directory = new WindowsFiles.DirectoryPin(Destination, machine: true, forMutation: true);
        var journal = Shortcuts.ReadJournal(Destination, machine: true);
        if (journal?.UserSid is { Length: > 0 } owner && owner != sid) throw new IOException("Use the Windows account that installed the addon to upgrade it.");
        using var tasks = new Tasks(sid);
        await tasks.StopAsync();
        bool restoreShortcuts = false;
        try
        {
            var previous = await RecoverPayload(directory, registeredVersion, allowRepair: true);
            var names = PayloadNames(payload);
            CleanUpgradeArtifacts(directory, names.Concat(previous is null ? [] : PayloadNames(previous)).Concat(Legacy).ToArray());
            var retired = previous?.Files.Select(file => file.Name).Except(names, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            if (retired.Length != 0 || !names.All(name => WindowsFiles.FileExists(Path.Combine(Destination, name)) &&
                Digest(Path.Combine(source, name)) == Digest(Path.Combine(Destination, name))))
                await Publish(directory, names, retired, name => WindowsFiles.Open(Path.Combine(source, name)), CompleteAsync);
            else await CompleteAsync();

            async Task CompleteAsync()
            {
                restoreShortcuts = journal is null && previous is null && registeredVersion is null;
                using (var shortcuts = new Shortcuts(Destination, Shortcuts.Roots(machine: true), sid, machine: true, writable: true))
                {
                    shortcuts.Initialize(sid);
                    shortcuts.Apply(sid, restore: false);
                }
                await Setup.RunOnDesktopAsync(Environment.ProcessPath!, Setup.ApplyUser, sid);
                tasks.Register();
                Register(payload.Version);
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            TryRecovery(tasks.Restore, failures);
            if (restoreShortcuts)
            {
                TryRecovery(() =>
                {
                    using var shortcuts = new Shortcuts(Destination, Shortcuts.Roots(machine: true), sid, machine: true, writable: true);
                    shortcuts.Apply(sid, restore: true);
                }, failures);
                try { await Setup.RunOnDesktopAsync(Environment.ProcessPath!, Setup.RestoreUser, sid); }
                catch (Exception recovery) { failures.Add(recovery); }
            }
            if (failures.Count != 0) throw RecoveryError(error, failures);
            throw;
        }
    }

    private static void CreateDestination()
    {
        WindowsFiles.CheckPath(Destination);
        if (!Directory.Exists(Destination))
        {
            var acl = new DirectorySecurity();
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            acl.SetAccessRuleProtection(true, false);
            foreach (var (sid, rights) in new[] { (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
                (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl), (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute) })
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            FileSystemAclExtensions.CreateDirectory(acl, Destination);
        }
        WindowsPermissions.RequireMachine(new DirectoryInfo(Destination).GetAccessControl(), Destination);
    }

    internal static async Task Publish(WindowsFiles.DirectoryPin directory, string[] names, string[] retired,
        Func<string, FileStream> openSource, Func<Task> complete)
    {
        RequireFileInheritance((DirectorySecurity)WindowsPermissions.Read(directory.Leaf, directory: true));
        var nonce = Guid.NewGuid().ToString("N");
        var changes = new List<(string Name, string Backup, string? Incoming, bool Existed)>();
        var applied = 0;
        try
        {
            foreach (var name in names.Concat(retired))
            {
                using var existing = WindowsFiles.OpenPayloadFile(directory, name);
                var backup = $".upgrade-{nonce}-{name}.bak";
                if (retired.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (existing is not null) changes.Add((name, backup, null, true));
                    continue;
                }
                var incoming = $".upgrade-{nonce}-{name}.new";
                using var from = openSource(name);
                using var handle = CreateStagedFile(Path.Combine(directory.PathName, incoming));
                changes.Add((name, backup, incoming, existing is not null));
                using var to = new FileStream(handle, FileAccess.Write, 65536);
                from.CopyTo(to);
                to.Flush(flushToDisk: true);
            }
            foreach (var change in changes)
            {
                applied++;
                var path = Path.Combine(directory.PathName, change.Name);
                var backup = Path.Combine(directory.PathName, change.Backup);
                if (change.Incoming is null) File.Move(path, backup);
                else
                {
                    var incoming = Path.Combine(directory.PathName, change.Incoming);
                    if (change.Existed) File.Replace(incoming, path, backup);
                    else File.Move(incoming, path);
                }
            }
            await complete();
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            foreach (var change in changes.Take(applied).Reverse())
            {
                TryRecovery(() =>
                {
                    bool hasBackup;
                    using (var backup = WindowsFiles.OpenPayloadFile(directory, change.Backup)) hasBackup = backup is not null;
                    var path = Path.Combine(directory.PathName, change.Name);
                    if (hasBackup) File.Move(Path.Combine(directory.PathName, change.Backup), path, overwrite: true);
                    else if (!change.Existed && change.Incoming is not null && !WindowsFiles.FileExists(Path.Combine(directory.PathName, change.Incoming)))
                        WindowsFiles.Delete(path);
                }, failures);
            }
            if (failures.Count == 0)
                foreach (var name in changes.Select(change => change.Incoming).OfType<string>())
                    TryRecovery(() => DeleteArtifact(directory, name), failures);
            if (failures.Count != 0) throw RecoveryError(error, failures);
            throw;
        }
        foreach (var change in changes)
        {
            try { DeleteArtifact(directory, change.Backup); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.TraceWarning(error.Message); }
        }
    }

    private static void TryRecovery(Action action, List<Exception> failures)
    {
        try { action(); }
        catch (Exception error) { failures.Add(error); }
    }

    private static IOException RecoveryError(Exception error, List<Exception> failures) =>
        new($"{error.Message} Setup could not complete rollback. Retry setup to recover the addon.",
            new AggregateException(new[] { error }.Concat(failures)));

    private static void RequireFileInheritance(DirectorySecurity parent)
    {
        var security = new RawSecurityDescriptor(parent.GetSecurityDescriptorBinaryForm(), 0);
        var parentAcl = security.DiscretionaryAcl;
        var fileAcl = new RawAcl(GenericAcl.AclRevision, parentAcl?.Count ?? 0);
        if (parentAcl is not null)
        {
            var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
            var fileOwner = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            foreach (GenericAce rule in parentAcl)
            {
                if ((rule.AceFlags & AceFlags.ObjectInherit) == 0) continue;
                var inherited = rule.Copy();
                inherited.AceFlags = AceFlags.Inherited;
                if (inherited is KnownAce known && known.SecurityIdentifier == creatorOwner)
                    known.SecurityIdentifier = fileOwner;
                fileAcl.InsertAce(fileAcl.Count, inherited);
            }
            security.Owner = fileOwner;
        }
        if (fileAcl.Count == 0) throw new IOException("The addon directory has no permissions to inherit on staged files.");
        security.DiscretionaryAcl = fileAcl;
        var descriptor = new byte[security.BinaryLength];
        security.GetBinaryForm(descriptor, 0);
        var file = new FileSecurity();
        file.SetSecurityDescriptorBinaryForm(descriptor);
        WindowsPermissions.RequireMachine(file, "staged addon file");
    }

    private static SafeFileHandle CreateStagedFile(string path)
    {
        // Set the owner at creation and omit the DACL so Windows inherits it from the parent.
        var security = new RawSecurityDescriptor("O:BA");
        var descriptor = new byte[security.BinaryLength];
        security.GetBinaryForm(descriptor, 0);
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            const uint GenericWrite = 0x40000000;
            var handle = CreateFile(@"\\?\" + path, GenericWrite, FileShare.None, ref attributes, FileMode.CreateNew, FileOptions.WriteThrough, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not create a staged addon file (Windows error {error}).", new Win32Exception(error));
        }
        finally { pinned.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, ref SecurityAttributes attributes,
        FileMode mode, FileOptions options, IntPtr template);

    internal static void DeleteFiles(string directory, IEnumerable<string> names, Action? complete = null)
    {
        var files = new List<WindowsFiles.OwnedFile>();
        var marked = new List<WindowsFiles.OwnedFile>();
        try
        {
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase)) files.Add(WindowsFiles.OpenOwned(Path.Combine(directory, name)));
            foreach (var file in files.Where(file => file.Exists))
            {
                file.SetDelete(marked: true);
                marked.Add(file);
            }
            complete?.Invoke();
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            foreach (var file in marked) TryRecovery(() => file.SetDelete(marked: false), failures);
            if (failures.Count != 0) throw RecoveryError(error, failures);
            throw;
        }
        finally { foreach (var file in files) file.Dispose(); }
    }

    internal static void ValidateRemoval(string sid)
    {
        _ = ReadRegistration();
        using var pin = new WindowsFiles.DirectoryPin(Destination, machine: true);
        var journal = Shortcuts.ReadJournal(Destination, machine: true) ?? throw new IOException("The machine shortcut journal is missing. Installation files were preserved.");
        if (journal.UserSid is { Length: > 0 } owner && owner != sid) throw new IOException("Use the Windows account that installed the addon to remove it.");
    }

    internal static async Task RemoveAsync(string sid)
    {
        using var stage = PinStage();
        using var maintenance = new Semaphore(1, 1, @"Global\HytaleAccounts-Deployment");
        if (!maintenance.WaitOne(0)) throw new IOException("Another setup or removal is already running.");
        try { await RemoveCoreAsync(sid); }
        finally { maintenance.Release(); }
    }

    private static async Task RemoveCoreAsync(string sid)
    {
        ValidateRemoval(sid);
        var package = ReadPayload(Path.GetDirectoryName(Environment.ProcessPath!)!);
        using var tasks = new Tasks(sid);
        await tasks.StopAsync();
        try
        {
            await WaitForInstalledExitAsync();
            var registeredVersion = ReadRegistration();
            var names = PayloadNames(package).Concat(Legacy).ToArray();
            using (var directory = new WindowsFiles.DirectoryPin(Destination, machine: true, forMutation: true))
            {
                var previous = await RecoverPayload(directory, registeredVersion);
                if (previous is not null) names = names.Concat(PayloadNames(previous)).ToArray();
                CleanUpgradeArtifacts(directory, names);
            }
            await Setup.RunOnDesktopAsync(Environment.ProcessPath!, Setup.RestoreUser, sid);
            using (var shortcuts = new Shortcuts(Destination, Shortcuts.Roots(machine: true), sid, machine: true, writable: true))
            {
                shortcuts.Apply(sid, restore: true);
                tasks.Delete();
                shortcuts.DeleteLock();
                DeleteFiles(Destination, names.Where(name => name != Executable).Concat(shortcuts.RecoveryFiles()).Append(Executable), DeleteRegistration);
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            TryRecovery(tasks.Restore, failures);
            if (failures.Count != 0) throw RecoveryError(error, failures);
            throw;
        }

        try { WindowsFiles.DeleteEmptyDirectory(Destination); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.TraceWarning(error.Message); }
    }

    private static async Task WaitForInstalledExitAsync()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var found = false;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Executable)))
            {
                using (process)
                {
                    try { found |= string.Equals(process.MainModule?.FileName, Installed, StringComparison.OrdinalIgnoreCase); }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                }
            }
            if (!found) return;
            await Task.Delay(100);
        }
        throw new IOException("Close Hytale Launcher and retry removal. The shortcut recovery journals were preserved.");
    }

    internal static bool IsUpgradeArtifact(string name, string[]? names) => name.Length > 46 && name.StartsWith(".upgrade-", StringComparison.Ordinal) &&
        name[41] == '-' && Guid.TryParseExact(name.AsSpan(9, 32), "N", out _) &&
        (name.EndsWith(".new", StringComparison.Ordinal) || name.EndsWith(".bak", StringComparison.Ordinal)) &&
        (names?.Contains(name[42..^4], StringComparer.Ordinal) ??
            (Required.Contains(name[42..^4], StringComparer.Ordinal) || name[42..^4] == Manifest ||
                Legacy.Contains(name[42..^4], StringComparer.Ordinal) || IsSidecar(name[42..^4])));

    private static string? ReadRegistration()
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var entry = registry.OpenSubKey(RegistryPath);
        if (entry is null) return null;
        ValidateRegistration(entry);
        return entry.GetValue("DisplayVersion") as string ?? "";
    }

    internal static bool OwnsRegistration(string destination, string executable, object? location, object? uninstall) =>
        string.Equals(location as string, destination, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uninstall as string, $"\"{executable}\" --uninstall", StringComparison.OrdinalIgnoreCase);

    private static void ValidateRegistration(RegistryKey entry)
    {
        if (!OwnsRegistration(Destination, Installed,
            entry.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames),
            entry.GetValue("UninstallString", null, RegistryValueOptions.DoNotExpandEnvironmentNames)))
            throw new IOException("Another installation owns the Windows Settings entry.");
    }

    private static void DeleteRegistration()
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using (var entry = registry.OpenSubKey(RegistryPath))
        {
            if (entry is null) return;
            ValidateRegistration(entry);
        }
        registry.DeleteSubKey(RegistryPath);
    }

    private static void Register(string version)
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var existing = registry.OpenSubKey(RegistryPath, writable: true);
        if (existing is not null) ValidateRegistration(existing);
        using var created = existing is null ? registry.CreateSubKey(RegistryPath) : null;
        var entry = existing ?? created!;
        var names = new[] { "DisplayName", "DisplayIcon", "DisplayVersion" };
        var previous = names.Select(name => (Name: name,
            Value: entry.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames))).ToArray();
        var values = previous.Select(item => (item.Name, item.Value,
            Kind: item.Value is null ? RegistryValueKind.Unknown : entry.GetValueKind(item.Name))).ToArray();
        try
        {
            if (created is not null)
            {
                entry.SetValue("InstallLocation", Destination);
                entry.SetValue("UninstallString", $"\"{Installed}\" --uninstall");
            }
            entry.SetValue("DisplayName", "Hytale Accounts addon");
            entry.SetValue("DisplayIcon", LauncherImage.OfficialPath + ",0");
            entry.SetValue("DisplayVersion", version);
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            if (created is not null)
            {
                created.Dispose();
                TryRecovery(() => registry.DeleteSubKey(RegistryPath, throwOnMissingSubKey: false), failures);
            }
            else
            {
                foreach (var (name, value, kind) in values)
                {
                    TryRecovery(() =>
                    {
                        if (value is null) entry.DeleteValue(name, throwOnMissingValue: false);
                        else entry.SetValue(name, value, kind);
                    }, failures);
                }
            }
            if (failures.Count != 0) throw RecoveryError(error, failures);
            throw;
        }
    }

    private sealed class Tasks : IDisposable
    {
        private readonly string sid;
        private readonly dynamic service;
        private readonly dynamic folder;
        private readonly List<(string Name, dynamic Task, dynamic Definition, bool Enabled)> owned = [];
        private readonly List<(string Name, string User, int Logon)> changed = [];

        internal Tasks(string sid)
        {
            this.sid = sid;
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            service.Connect();
            folder = service.GetFolder("\\");
            try { Inspect(false); Inspect(true); }
            catch { Dispose(); throw; }
        }

        private void Inspect(bool limited)
        {
            string name = limited ? TaskName + "-" + sid : TaskName;
            dynamic task;
            try { task = folder.GetTask(name); }
            catch (Exception ex) when ((ex is COMException or FileNotFoundException) && ex.HResult == unchecked((int)0x80070002)) { return; }
            catch (Exception ex) { throw new IOException($"Task Scheduler GetTask failed (0x{ex.HResult:X8}).", ex); }
            dynamic actions = task.Definition.Actions;
            var legacyArgs = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{Path.Combine(Destination, "Repair-Integration.ps1")}\"";
            var repair = limited ? UserRepair : Repair;
            if (limited) legacyArgs += " -UserOnly";
            string owner = task.Definition.Principal.UserId ?? "";
            string ownerSid;
            try { ownerSid = owner.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? owner : ((SecurityIdentifier)new NTAccount(owner).Translate(typeof(SecurityIdentifier))).Value; }
            catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException) { ownerSid = ""; }
            if ((int)actions.Count != 1 || (int)actions.Item(1).Type != 0 ||
                !ownerSid.Equals(limited ? sid : "S-1-5-18", StringComparison.OrdinalIgnoreCase) ||
                !((string.Equals((string)actions.Item(1).Path, Installed, StringComparison.OrdinalIgnoreCase) && (string)actions.Item(1).Arguments == repair) ||
                  (string.Equals((string)actions.Item(1).Path, PowerShell, StringComparison.OrdinalIgnoreCase) && (string)actions.Item(1).Arguments == legacyArgs)))
                throw new IOException($"Another process owns the scheduled task {name}.");
            owned.Add((name, task, task.Definition, (bool)task.Enabled));
        }

        internal async Task StopAsync()
        {
            try
            {
                foreach (var (_, task, _, _) in owned)
                {
                    task.Enabled = false;
                    if ((int)task.GetInstances(0).Count != 0) task.Stop(0);
                }
                for (var attempt = 0; owned.Any(item => (int)item.Task.GetInstances(0).Count != 0); attempt++)
                {
                    if (attempt == 50) throw new IOException("Shortcut repair is still stopping. Try again.");
                    await Task.Delay(100);
                }
            }
            catch { EnableExisting(); throw; }
        }

        internal void EnableExisting()
        {
            foreach (var (_, task, _, enabled) in owned)
            {
                try { task.Enabled = enabled; }
                catch (COMException) { }
                catch (FileNotFoundException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
            }
        }

        internal void Register()
        {
            Register(false);
            Register(true);
        }

        private void Register(bool limited)
        {
            var user = limited ? sid : "S-1-5-18";
            var name = limited ? TaskName + "-" + sid : TaskName;
            dynamic definition = service.NewTask(0);
            definition.Principal.UserId = user;
            definition.Principal.LogonType = limited ? 3 : 5;
            definition.Principal.RunLevel = limited ? 0 : 1;
            definition.Settings.Hidden = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = 2;
            dynamic trigger = definition.Triggers.Create(limited ? 9 : 8);
            if (limited) trigger.UserId = user;
            dynamic action = definition.Actions.Create(0);
            action.Path = Installed;
            action.Arguments = limited ? UserRepair : Repair;
            action.WorkingDirectory = Environment.SystemDirectory;
            var logon = limited ? 3 : 5;
            changed.Add((name, user, logon));
            folder.RegisterTaskDefinition(name, definition, 6, user, null, logon, null);
        }

        internal void Restore()
        {
            var failures = new List<Exception>();
            foreach (var (name, user, logon) in changed)
            {
                TryRecovery(() =>
                {
                    var previous = owned.Find(item => item.Name == name);
                    if (previous.Name is null)
                    {
                        try { folder.DeleteTask(name, 0); }
                        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
                    }
                    else
                    {
                        dynamic restored = folder.RegisterTaskDefinition(name, previous.Definition, 6, user, null, logon, null);
                        restored.Enabled = previous.Enabled;
                    }
                }, failures);
            }
            EnableExisting();
            if (failures.Count != 0) throw new AggregateException(failures);
        }

        internal void Delete()
        {
            foreach (var (name, _, _, _) in owned)
            {
                var machine = name == TaskName;
                changed.Add((name, machine ? "S-1-5-18" : sid, machine ? 5 : 3));
                folder.DeleteTask(name, 0);
            }
        }
        public void Dispose() { Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
}
