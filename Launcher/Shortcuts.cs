using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace HytaleAccounts;

internal sealed class Shortcuts : IDisposable
{
    internal const string Manifest = "shortcuts.json";
    private const string Description = "Hytale launcher with account switching";
    private readonly string state;
    private readonly string[] roots;
    private readonly bool machine;
    private readonly string official;
    private readonly string installed;
    private readonly string destination;
    private readonly WindowsFiles.DirectoryPin? directory;
    private readonly WindowsFiles.OwnedFile? journalLock;
    private readonly Journal journal;

    internal sealed record Journal(string[] Roots, string[]? RepairRoots, string? UserSid, List<Entry> Shortcuts);
    internal sealed class Entry
    {
        public required string Path { get; set; }
        public string? Backup { get; set; }
        public string? AppliedHash { get; set; }
        public string? FileId { get; set; }
        public string Arguments { get; set; } = "";
    }

    internal Shortcuts(string state, string[] roots, string sid, bool machine, bool writable, bool create = false, string? official = null, string? installed = null)
    {
        this.state = state;
        this.roots = roots;
        this.machine = machine;
        this.official = official ?? LauncherImage.OfficialPath;
        this.installed = installed ?? Deployment.Installed;
        destination = Path.GetDirectoryName(this.installed)!;
        if (create && !Directory.Exists(state))
        {
            WindowsPermissions.CreatePrivateDirectory(state);
        }
        try
        {
            if (Directory.Exists(state))
            {
                directory = new WindowsFiles.DirectoryPin(state, machine, forMutation: writable);
                if (writable) journalLock = WindowsFiles.OpenOwned(Path.Combine(state, "shortcuts.lock"), create: true);
            }
            journal = ReadJournal(state, machine) ?? new(roots, roots, sid, []);
            if (!string.IsNullOrEmpty(journal.UserSid) && journal.UserSid != sid)
                throw new IOException("Use the Windows account that installed the addon to manage its shortcuts.");
            if (journal.Roots is null || journal.Shortcuts is null || journal.Roots.Any(root => !Path.IsPathFullyQualified(root)))
                throw new IOException("The shortcut restoration journal is invalid. Its files were preserved.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in journal.Shortcuts)
            {
                if (!Under(entry.Path, journal.Roots) || !Path.GetExtension(entry.Path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                    entry.Arguments is not ("" or "--uninstall") || (entry.Backup is not null && !BackupName(entry.Backup)) ||
                    (!string.IsNullOrEmpty(entry.FileId) && !ids.Add(entry.FileId)))
                    throw new IOException("The shortcut restoration journal is invalid. Its files were preserved.");
                if (entry.Backup is not null) _ = ReadBackup(entry.Backup);
            }
        }
        catch { Dispose(); throw; }
    }

    internal static string UserState => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HytaleAccounts", "ShortcutIntegration");
    internal static string[] Roots(bool machine) => (machine
        ? new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory }
        : new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
        .Select(folder => Environment.GetFolderPath(folder)).Where(Path.IsPathFullyQualified).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static Journal? ReadJournal(string path, bool machine = false)
    {
        var manifest = Path.Combine(path, Manifest);
        if (!WindowsFiles.FileExists(manifest)) return null;
        if (machine) WindowsPermissions.RequireMachine(new FileInfo(manifest).GetAccessControl(), manifest);
        var bytes = WindowsFiles.Read(manifest).AsSpan();
        if (bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
        return JsonSerializer.Deserialize<Journal>(bytes) ?? throw new IOException("The shortcut journal is empty.");
    }

    internal bool HasLaunchLink()
    {
        foreach (var path in Paths(roots))
        {
            using var link = Link.Read(path);
            if (link is null || link.Arguments != "") continue;
            if (Same(link.Target, official)) return true;
            if (!Same(link.Target, installed)) continue;
            if (journal.Shortcuts.Any(entry => Same(entry.Path, path))) return true;
            using var candidate = WindowsFiles.OpenOwned(path, readOnly: true);
            if (candidate.Exists && journal.Shortcuts.Any(entry => Same(entry.FileId, candidate.Identity))) return true;
            if (!machine && Same(destination, Deployment.Destination) && ReadJournal(destination, machine: true)?.Shortcuts.Any(entry =>
                Same(entry.Path, path) || (candidate.Exists && Same(entry.FileId, candidate.Identity))) == true) return true;
        }
        return false;
    }

    internal void Apply(string sid, bool restore)
    {
        if (journalLock is null) throw new InvalidOperationException("The shortcut journal is read-only.");
        if (!restore) Save(sid);
        var historic = journal.Roots.Except(roots, StringComparer.OrdinalIgnoreCase);
        var scan = machine ? roots.Concat(restore ? HistoricMachineRoots(historic) : []) : roots.Concat(historic);
        foreach (var path in Paths(scan).Distinct(StringComparer.OrdinalIgnoreCase))
            ApplyOne(path, sid, restore);
    }

    internal void Initialize(string sid) => Save(sid);

    private IEnumerable<string> HistoricMachineRoots(IEnumerable<string> historic)
    {
        foreach (var root in historic)
        {
            if (Roots(machine: false).Contains(root, StringComparer.OrdinalIgnoreCase) || !WindowsFiles.DirectoryExists(root)) continue;
            WindowsFiles.DirectoryPin pin;
            try { pin = new WindowsFiles.DirectoryPin(root, machine: true, forMutation: true); }
            catch (IOException) when (journal.RepairRoots?.Contains(root, StringComparer.OrdinalIgnoreCase) != true) { continue; }
            using (pin) yield return root;
        }
    }

    private void ApplyOne(string path, string sid, bool restore)
    {
        var entry = journal.Shortcuts.Find(item => Same(item.Path, path));
        if (entry is null)
        {
            // Windows includes hard-linked unrelated shortcuts. Read them without requesting mutation access.
            using var candidate = Link.Read(path);
            if (candidate is null || (!Same(candidate.Target, official) && !Same(candidate.Target, installed)) ||
                candidate.Arguments is not ("" or "--uninstall")) return;
        }
        using var file = machine ? WindowsFiles.OpenMachineShortcut(path) : WindowsFiles.OpenOwned(path);
        if (!file.Exists) return;
        var bytes = file.Read();
        var hash = Hash(bytes);
        if (entry is not null && entry.FileId is not null && !Same(entry.FileId, file.Identity)) entry = null;
        entry ??= journal.Shortcuts.Find(item => item.FileId is not null && Same(item.FileId, file.Identity));
        if (entry is null && restore)
        {
            var legacy = journal.Shortcuts.Where(item => item.FileId is null && SameHash(item.AppliedHash, hash)).Take(2).ToArray();
            if (legacy.Length == 1) entry = legacy[0];
            else if (!machine) entry = AdoptLegacy(path, sid, file.Identity, hash);
            if (entry is null) return;
        }

        var scratch = Path.Combine(state, Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            WindowsFiles.Write(scratch, bytes);
            using var link = Link.Read(scratch);
            if (link is null)
            {
                if (entry?.Backup is null || !Same(entry.FileId, file.Identity) || !Same(path, entry.Path))
                    throw new IOException($"Cannot verify shortcut ownership for {path}.");
                var backup = ReadBackup(entry.Backup);
                file.Write(backup);
                if (!restore) { file.Dispose(); ApplyOne(path, sid, restore: false); }
                return;
            }
            if (restore)
            {
                if (entry is null || !Same(link.Target, installed) || link.Arguments != entry.Arguments) return;
                var unchanged = entry.AppliedHash is null || SameHash(entry.AppliedHash, hash);
                if (entry.Backup is null) { if (unchanged) file.Delete(); return; }
                var originalBytes = ReadBackup(entry.Backup);
                if (unchanged) { file.Write(originalBytes); return; }
                using var original = Link.Read(Path.Combine(state, entry.Backup)) ?? throw new IOException("The shortcut backup is unreadable.");
                link.Target = original.Target;
                link.Arguments = original.Arguments;
                if (Same(link.WorkingDirectory, destination)) link.WorkingDirectory = original.WorkingDirectory;
                if (Same(link.Icon, official) && link.IconIndex == 0) { link.Icon = original.Icon; link.IconIndex = original.IconIndex; }
                if (link.Description == Description) link.Description = original.Description;
            }
            else
            {
                if (!Same(link.Target, official) || link.Arguments != "")
                {
                    if (!machine && entry is null && Same(link.Target, installed) && link.Arguments == "") AdoptLegacy(path, sid, file.Identity, hash);
                    return;
                }
                if (entry is null)
                {
                    journal.Shortcuts.RemoveAll(item => Same(item.Path, path));
                    entry = new Entry { Path = path };
                    journal.Shortcuts.Add(entry);
                }
                entry.Path = path;
                if (entry.Backup is null)
                {
                    entry.Backup = "shortcut-" + Guid.NewGuid().ToString("N") + ".lnk";
                    var backup = Path.Combine(state, entry.Backup);
                    if (machine) WindowsFiles.WriteMachineShortcutBackup(backup, bytes);
                    else WindowsFiles.Write(backup, bytes);
                }
                link.Target = installed;
                link.WorkingDirectory = destination;
                link.Icon = official;
                link.IconIndex = 0;
                link.Description = Description;
            }
            link.Save(scratch);
            var replacement = WindowsFiles.Read(scratch);
            if (!restore)
            {
                entry!.FileId = file.Identity;
                entry.AppliedHash = Hash(replacement);
                Save(sid);
            }
            file.Write(replacement);
        }
        finally { WindowsFiles.Delete(scratch); }
    }

    private Entry? AdoptLegacy(string path, string sid, string identity, string hash)
    {
        if (!Same(destination, Deployment.Destination)) return null;
        using var machineDirectory = new WindowsFiles.DirectoryPin(destination, machine: true, forMutation: true);
        var legacy = ReadJournal(destination, machine: true);
        if (legacy is null || (!string.IsNullOrEmpty(legacy.UserSid) && legacy.UserSid != sid)) return null;
        var old = legacy.Shortcuts.SingleOrDefault(item => Same(item.Path, path) || Same(item.FileId, identity));
        if (old?.Backup is null || !BackupName(old.Backup) || old.Arguments != "" ||
            (old.FileId is not null ? !Same(old.FileId, identity) : !SameHash(old.AppliedHash, hash))) return null;
        var backup = "shortcut-" + Guid.NewGuid().ToString("N") + ".lnk";
        var source = Path.Combine(destination, old.Backup);
        WindowsPermissions.RequireMachine(new FileInfo(source).GetAccessControl(), source);
        WindowsFiles.Write(Path.Combine(state, backup), WindowsFiles.Read(source));
        var entry = new Entry { Path = path, Backup = backup, FileId = identity, AppliedHash = old.AppliedHash };
        journal.Shortcuts.Add(entry);
        Save(sid);
        return entry;
    }

    internal IEnumerable<string> RecoveryFiles() => journal.Shortcuts.Select(entry => entry.Backup).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).Append("shortcuts.new").Append(Manifest);

    internal void Clean()
    {
        Deployment.DeleteFiles(state, RecoveryFiles());
        DeleteLock();
    }

    internal void DeleteLock() => journalLock!.Delete();

    private void Save(string sid)
    {
        var saved = new Journal(journal.Roots.Concat(roots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), roots, sid, journal.Shortcuts);
        using var temporary = WindowsFiles.OpenOwned(Path.Combine(state, "shortcuts.new"), create: true);
        temporary.Write(JsonSerializer.SerializeToUtf8Bytes(saved));
        using var current = WindowsFiles.OpenOwned(Path.Combine(state, Manifest));
        current.ReplaceFrom(temporary);
    }

    private byte[] ReadBackup(string name)
    {
        var path = Path.Combine(state, name);
        if (machine) WindowsPermissions.RequireMachine(new FileInfo(path).GetAccessControl(), path);
        return WindowsFiles.Read(path);
    }

    public void Dispose() { journalLock?.Dispose(); directory?.Dispose(); }
    internal static bool Under(string path, IEnumerable<string> roots) => Path.IsPathFullyQualified(path) && roots.Any(root =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    internal static bool BackupName(string name) => name.Length == 45 && name.StartsWith("shortcut-", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(name.AsSpan(9, 32), "N", out _);
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool SameHash(string? first, string second) => Same(first?.Replace("-", "", StringComparison.Ordinal), second);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static IEnumerable<string> Paths(IEnumerable<string> roots)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var root in roots)
        {
            WindowsFiles.CheckPath(root);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*.lnk", options)) yield return path;
        }
    }

    private sealed class Link : IDisposable
    {
        private readonly object instance = new ShellLink();
        private IShellLink Native => (IShellLink)instance;
        private System.Runtime.InteropServices.ComTypes.IPersistFile Persist => (System.Runtime.InteropServices.ComTypes.IPersistFile)instance;
        private string Get(Func<StringBuilder, int> read) { var value = new StringBuilder(32768); Marshal.ThrowExceptionForHR(read(value)); return value.ToString(); }
        internal string Target { get => Get(value => Native.GetPath(value, value.Capacity, IntPtr.Zero, 4)); set => Marshal.ThrowExceptionForHR(Native.SetPath(value)); }
        internal string Arguments { get => Get(value => Native.GetArguments(value, value.Capacity)); set => Marshal.ThrowExceptionForHR(Native.SetArguments(value)); }
        internal string WorkingDirectory { get => Get(value => Native.GetWorkingDirectory(value, value.Capacity)); set => Marshal.ThrowExceptionForHR(Native.SetWorkingDirectory(value)); }
        internal string Description { get => Get(value => Native.GetDescription(value, value.Capacity)); set => Marshal.ThrowExceptionForHR(Native.SetDescription(value)); }
        private int iconIndex;
        internal string Icon { get => Get(value => Native.GetIconLocation(value, value.Capacity, out iconIndex)); set => Marshal.ThrowExceptionForHR(Native.SetIconLocation(value, iconIndex)); }
        internal int IconIndex { get { _ = Icon; return iconIndex; } set => Marshal.ThrowExceptionForHR(Native.SetIconLocation(Icon, value)); }
        internal static Link? Read(string path)
        {
            WindowsFiles.CheckPath(path);
            var link = new Link();
            try { link.Persist.Load(path, 0); return link; }
            catch (COMException) { link.Dispose(); return null; }
        }
        internal void Save(string path) => Persist.Save(path, false);
        public void Dispose() => Marshal.FinalReleaseComObject(instance);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr data, uint flags);
        [PreserveSig] int GetIdList(out IntPtr list);
        [PreserveSig] int SetIdList(IntPtr list);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string path);
        [PreserveSig] int GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int GetHotkey(out short value);
        [PreserveSig] int SetHotkey(short value);
        [PreserveSig] int GetShowCommand(out int value);
        [PreserveSig] int SetShowCommand(int value);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
