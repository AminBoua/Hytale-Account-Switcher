using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace HytaleAccounts.Checks;

internal static class ShortcutChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HytaleShortcutCheck-" + Guid.NewGuid().ToString("N"));
        WindowsPermissions.CreatePrivateDirectory(root);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User!.Value;
            var links = Directory.CreateDirectory(Path.Combine(root, "links")).FullName;
            var state = Path.Combine(root, "state");
            var official = Path.Combine(root, "hytale-launcher.exe");
            var installed = Path.Combine(root, ".hytale-accounts-addon", "HytaleAccounts.exe");
            File.WriteAllText(official, "shortcut fixture");
            Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
            File.WriteAllText(installed, "helper fixture");
            var main = Path.Combine(links, "Hytale Launcher.lnk");
            var renamed = Path.Combine(links, "Renamed launcher.lnk");
            var other = Path.Combine(root, "Windows component.lnk");
            var linked = Path.Combine(links, "Windows component.lnk");
            SaveLink(main, official, "Original description");
            SaveLink(other, Environment.ProcessPath!, "Unrelated");
            Require(CreateHardLink(linked, other, IntPtr.Zero), "Create the unrelated test hard link.");
            var original = File.ReadAllBytes(main);
            var unrelated = File.ReadAllBytes(other);

            Shortcuts Scope(bool create = false) =>
                new(state, [links], sid, machine: false, writable: true, create: create, official: official, installed: installed);

            using (var install = Scope(create: true))
            {
                Require(install.HasLaunchLink(), "Preflight finds an original eligible shortcut.");
                install.Apply(sid, restore: false);
            }
            Require(Target(main) == installed, "Install retargets the existing shortcut.");
            Require(File.ReadAllBytes(other).AsSpan().SequenceEqual(unrelated), "Install leaves unrelated hard links intact.");
            File.Move(main, renamed);

            using (var repair = Scope()) repair.Apply(sid, restore: false);
            using (var removal = Scope()) removal.Apply(sid, restore: true);
            Require(File.ReadAllBytes(renamed).AsSpan().SequenceEqual(original), "Removal restores a renamed owned shortcut byte for byte.");
            Require(!File.Exists(main), "Removal does not recreate the former filename.");

            using (var reinstall = Scope()) reinstall.Apply(sid, restore: false);
            var manifest = Path.Combine(state, Shortcuts.Manifest);
            var journal = Shortcuts.ReadJournal(state)!;
            Require(journal.Shortcuts.Count == 1, "Reinstall reuses the original record.");
            var record = journal.Shortcuts.Single();
            journal.Shortcuts.Add(new() { Path = Path.Combine(links, "Duplicate.lnk"), FileId = record.FileId, Backup = record.Backup });
            File.WriteAllBytes(manifest, JsonSerializer.SerializeToUtf8Bytes(journal));
            var refused = false;
            try { using var duplicate = Scope(); }
            catch (IOException) { refused = true; }
            Require(refused && Target(renamed) == installed, "Duplicate identity causes refusal without a change.");

            journal.Shortcuts.RemoveAt(1);
            File.WriteAllBytes(manifest, JsonSerializer.SerializeToUtf8Bytes(journal));
            using (var removal = Scope())
            {
                removal.Apply(sid, restore: true);
                removal.Clean();
            }
            Require(!File.Exists(manifest) && Target(renamed) == official, "Cleanup removes owned recovery state after restoration.");
            Require(File.ReadAllBytes(other).AsSpan().SequenceEqual(unrelated), "Repair and removal leave unrelated hard links intact.");
            Console.WriteLine("PASS shortcut install, rename, repair, restore, duplicate identity and unrelated hard-link checks");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void SaveLink(string path, string target, string description)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            try { link.TargetPath = target; link.Description = description; link.Save(); }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static string Target(string path)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            try { return link.TargetPath; }
            finally { Marshal.FinalReleaseComObject(link); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string target, IntPtr attributes);
}
