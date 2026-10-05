using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using HytaleAccounts;

internal static class LifecycleChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var source = IntegrationSafety.OfficialPath;
        if (PatchInstaller.Hash(source) != IntegrationSafety.SupportedHash) source = new PatchInstaller().BackupPath;
        var original = File.ReadAllBytes(source);
        check(PatchInstaller.Hash(source) == IntegrationSafety.SupportedHash, "lifecycle fixture uses exact supported bytes");
        var update = "publisher-verified official update fixture"u8.ToArray();
        var root = Path.Combine(Path.GetTempPath(), "launcher-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var helper = Path.Combine(root, "helper.exe"); File.WriteAllText(helper, "helper fixture");
        Task Publisher(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (!bytes.SequenceEqual(original) && !bytes.SequenceEqual(update)) throw new IOException("Unsigned fixture rejected");
            return Task.CompletedTask;
        }
        PatchInstaller Fixture(string name)
        {
            var directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "hytale-launcher.exe"); File.WriteAllBytes(target, original);
            foreach (var protectedFile in new[] { "Accounts/one/account.dat", "Accounts/one/.keys/key", "install/game", "UserData/world", "credentials.dpapi" })
            {
                var path = Path.Combine(directory, protectedFile); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "opaque data to preserve");
            }
            return new PatchInstaller(target, Publisher);
        }
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); } catch (IOException) { check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }
        try
        {
            foreach (var phase in new[] { "backup", "prepared", "staged", "replaced", "installed" })
            {
                var installer = Fixture("install-" + phase);
                var acl = PatchInstaller.Permissions(installer.Target);
                var protectedBytes = Directory.GetFiles(Path.GetDirectoryName(installer.Target)!, "*", SearchOption.AllDirectories)
                    .Where(p => p != installer.Target).ToDictionary(p => p, File.ReadAllBytes);
                installer.Checkpoint = point => { if (point == phase) throw new IOException("Interrupted install"); };
                await Reject(() => installer.Install(helper), "install interruption at " + phase);
                installer.Checkpoint = null; await installer.Recover();
                await installer.Install(helper); await installer.Uninstall(); await installer.Uninstall();
                check(File.ReadAllBytes(installer.Target).SequenceEqual(original) && PatchInstaller.Permissions(installer.Target) == acl,
                    "exact launcher bytes and owner/group/DACL restored at " + phase);
                check(protectedBytes.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "stores, keys, vault, installation and UserData preserved at " + phase);
            }
            foreach (var phase in new[] { "restoring", "staged", "replaced", "uninstalled" })
            {
                var installer = Fixture("restore-" + phase); var acl = PatchInstaller.Permissions(installer.Target);
                await installer.Install(helper);
                installer.Checkpoint = point => { if (point == phase) throw new IOException("Interrupted restore"); };
                await Reject(installer.Uninstall, "restore interruption at " + phase);
                installer.Checkpoint = null; await installer.Recover();
                check(File.ReadAllBytes(installer.Target).SequenceEqual(original) && PatchInstaller.Permissions(installer.Target) == acl,
                    "recovery verifies restored bytes and permissions at " + phase);
            }
            foreach (var phase in new[] { "prepared", "installed", "restoring", "uninstalled" })
            {
                var installer = Fixture("update-" + phase); await installer.Install(helper);
                var record = installer.Read() with { Phase = phase };
                File.WriteAllBytes(Path.Combine(installer.DirectoryPath, "install.json"), JsonSerializer.SerializeToUtf8Bytes(record));
                File.WriteAllBytes(installer.Target, update);
                var acl = PatchInstaller.Permissions(installer.Target);
                File.WriteAllText(installer.BackupPath, "corrupted obsolete backup");
                await installer.Recover(); await installer.Uninstall();
                await Reject(() => installer.Install(helper), "unsupported update rejects reinstall at " + phase);
                check(File.ReadAllBytes(installer.Target).SequenceEqual(update) && PatchInstaller.Permissions(installer.Target) == acl,
                    "official update preserved despite damaged old backup at " + phase);
            }
            var race = Fixture("racing-update"); await race.Install(helper);
            race.Checkpoint = point => { if (point == "staged") File.WriteAllBytes(race.Target, update); };
            await Reject(race.Uninstall, "update while staging aborts old restore");
            race.Checkpoint = null; await race.Recover();
            check(File.ReadAllBytes(race.Target).SequenceEqual(update), "racing update remains intact");
            var unknown = Fixture("unknown"); await unknown.Install(helper); File.WriteAllText(unknown.Target, "unsigned replacement");
            await Reject(unknown.Recover, "unsigned current launcher is preserved");
            check(File.ReadAllText(unknown.Target) == "unsigned replacement", "unknown bytes are never replaced by old backup");
            var corrupt = Fixture("corrupt"); await corrupt.Install(helper); var patched = PatchInstaller.Hash(corrupt.Target);
            File.WriteAllText(corrupt.BackupPath, "damaged backup");
            await Reject(corrupt.Uninstall, "damaged backup cannot restore a modified launcher");
            check(PatchInstaller.Hash(corrupt.Target) == patched, "corrupt recovery leaves modified bytes intact");
            var permissions = Fixture("permissions"); await permissions.Install(helper);
            var beforeAcl = new FileInfo(permissions.Target).GetAccessControl();
            var changedAcl = new FileInfo(permissions.Target).GetAccessControl();
            changedAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(permissions.Target).SetAccessControl(changedAcl);
            try { await Reject(permissions.Uninstall, "changed permissions block restoration"); }
            finally
            {
                beforeAcl.SetSecurityDescriptorSddlForm(permissions.Read().OriginalPermissions!, AccessControlSections.Access);
                new FileInfo(permissions.Target).SetAccessControl(beforeAcl);
            }
            await permissions.Uninstall();
            var legacy = Fixture("legacy"); await legacy.Install(helper);
            File.WriteAllBytes(Path.Combine(legacy.DirectoryPath, "install.json"), JsonSerializer.SerializeToUtf8Bytes(legacy.Read() with { Schema = 1, OriginalPermissions = null }));
            var legacyAcl = PatchInstaller.Permissions(legacy.Target); await legacy.Uninstall();
            check(File.ReadAllBytes(legacy.Target).SequenceEqual(original) && PatchInstaller.Permissions(legacy.Target) == legacyAcl,
                "legacy journal restores exact bytes while preserving current permissions");
            var missing = Fixture("missing"); await missing.Install(helper); File.Delete(missing.Target);
            await Reject(missing.Recover, "missing official executable requires official reinstall");
            check(!File.Exists(missing.Target), "recovery does not resurrect an older missing executable");
        }
        finally
        {
            // Fixtures contain no junctions; cleanup stays within this explicitly created temp root.
            Directory.Delete(root, true);
        }
    }
}
