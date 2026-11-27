using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace HytaleAccounts.Checks;

internal static class SetupChecks
{
    internal static void Run()
    {
        var hash = new string('a', 64);
        var files = new[] { new Deployment.PayloadFile(Deployment.Executable, hash), new("LICENSE", hash), new("THIRD-PARTY-NOTICES.txt", hash) };
        var payload = new Deployment.Payload("0.1.5", files);
        if (Deployment.ParsePayload(JsonSerializer.SerializeToUtf8Bytes(payload)).Files.Length != 3)
            throw new IOException("Setup did not read the complete package manifest.");
        Reject(payload with { Files = [.. files.Take(2), new("../hytale-launcher.exe", hash)] });
        Reject(payload with { Files = [.. files.Take(2), files[0]] });
        Reject(payload with { Files = [.. files.Take(2), files[2] with { Sha256 = "0" }] });

        var prefix = ".upgrade-" + Guid.NewGuid().ToString("N") + "-";
        if (!Deployment.IsUpgradeArtifact(prefix + Deployment.Executable + ".bak", [Deployment.Executable]) ||
            Deployment.IsUpgradeArtifact(prefix + "hytale-launcher.exe.bak", [Deployment.Executable]))
            throw new IOException("Setup did not restrict upgrade cleanup to owned bundle files.");
        Console.WriteLine("PASS setup package validation and upgrade cleanup ownership");
        CheckShortcutPermissions();
        CheckInheritedShortcutDelete();
        CheckMachineShortcutBackupPermissions();
        CheckRegistration();
        CheckRemovalRetry();
        if (Environment.GetEnvironmentVariable("HYTALE_MACHINE_SETUP_CHECKS") == "1") CheckMachineUpgrade();
    }

    private static void CheckRegistration()
    {
        const string directory = @"C:\Program Files\Hytale Setup Fixture\.hytale-accounts-addon";
        var executable = Path.Combine(directory, Deployment.Executable);
        var command = $"\"{executable}\" --uninstall";
        Require(Deployment.OwnsRegistration(directory, executable, directory.ToUpperInvariant(), command),
            "Setup rejected the owned registration.");
        Require(!Deployment.OwnsRegistration(directory, executable, directory + "-foreign", command) &&
            !Deployment.OwnsRegistration(directory, executable, directory, command + " --extra") &&
            !Deployment.OwnsRegistration(directory, executable, directory, "%" + command) &&
            !Deployment.OwnsRegistration(directory, executable, directory, null), "Setup accepted an unproven registration.");
        Console.WriteLine("PASS setup registration ownership");
    }

    private static void CheckRemovalRetry()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hytale-removal-check-" + Guid.NewGuid().ToString("N"));
        WindowsPermissions.CreatePrivateDirectory(root);
        var names = new[] { Deployment.Manifest, Deployment.Executable };
        var paths = names.Select(name => Path.Combine(root, name)).ToArray();
        try
        {
            foreach (var path in paths) File.WriteAllBytes(path, [1, 2]);
            var denied = new Win32Exception(5);
            var failure = WindowsFiles.Error(root, denied);
            Expect(failure, () => Deployment.DeleteFiles(root, names, () => throw failure));
            Require(paths.All(path => File.ReadAllBytes(path).AsSpan().SequenceEqual(new byte[] { 1, 2 })),
                "Failed removal lost the uninstall target or manifest.");
            Deployment.DeleteFiles(root, names);
            Require(paths.All(path => !File.Exists(path)), "Removal retry left payload files.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS removal retains files until commit and permits retry");
    }

    private static void CheckMachineUpgrade()
    {
        using var identity = WindowsIdentity.GetCurrent();
        Require(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            "Machine setup checks require an elevated disposable Windows fixture.");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "HytaleAccounts-check-" + Guid.NewGuid().ToString("N"));
        var acl = new DirectorySecurity();
        acl.SetSecurityDescriptorSddlForm("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)");
        FileSystemAclExtensions.CreateDirectory(acl, root);
        try
        {
            var live = Directory.CreateDirectory(Path.Combine(root, "live")).FullName;
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            using var pin = new WindowsFiles.DirectoryPin(live, machine: true, forMutation: true);
            var names = WritePayload(live, "0.1.5", 5, "Retired.dll");
            var incoming = WritePayload(source, "0.1.6", 6, "New.dll");
            var prefix = ".upgrade-" + Guid.NewGuid().ToString("N") + "-";
            var empty = Path.Combine(live, prefix + Deployment.Executable + ".new");
            File.WriteAllBytes(empty, [9]);
            var emptyAcl = new FileSecurity();
            emptyAcl.SetSecurityDescriptorSddlForm("O:BAD:P");
            new FileInfo(empty).SetAccessControl(emptyAcl);
            try
            {
                using var file = WindowsFiles.OpenOwned(empty);
                throw new IOException("The historical artifact allowed a generic open.");
            }
            catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 5 }) { }
            Require(Deployment.RecoverPayload(pin, "0.1.5").GetAwaiter().GetResult()?.Version == "0.1.5",
                "Setup rejected the valid payload.");
            Deployment.CleanUpgradeArtifacts(pin, names);
            Require(!File.Exists(empty), "Same-version repair left the historical artifact.");

            var linked = Path.Combine(live, prefix + "LICENSE.new");
            var license = Path.Combine(live, "LICENSE");
            if (!CreateHardLink(linked, license, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Deployment.CleanUpgradeArtifacts(pin, names);
                throw new IOException("Setup removed a hard-linked artifact.");
            }
            catch (IOException error) when (error.Message.Contains("unexpected file type", StringComparison.Ordinal)) { }
            Require(File.Exists(linked) && File.Exists(license), "Setup changed a hard-linked artifact.");
            File.Delete(linked);

            var failure = WindowsFiles.Error(live, new Win32Exception(5));
            Expect(failure, () => Deployment.Publish(pin, incoming, ["Retired.dll"], name => WindowsFiles.Open(Path.Combine(source, name)),
                () => Task.FromException(failure)).GetAwaiter().GetResult());
            Require(Deployment.ReadPayload(live).Version == "0.1.5" && !File.Exists(Path.Combine(live, "New.dll")),
                "A failed setup commit did not recover the previous payload.");

            var backup = Path.Combine(live, prefix + "LICENSE.bak");
            File.Copy(license, backup);
            File.Copy(Path.Combine(source, "LICENSE"), license, overwrite: true);
            Require(Deployment.RecoverPayload(pin, "0.1.5").GetAwaiter().GetResult()?.Version == "0.1.5" &&
                Deployment.ReadPayload(live).Version == "0.1.5" && File.Exists(backup),
                "Setup did not recover the interrupted payload.");
            Deployment.CleanUpgradeArtifacts(pin, names);

            foreach (var name in names) File.Move(Path.Combine(live, name), Path.Combine(live, prefix + name + ".bak"));
            foreach (var name in incoming) File.Copy(Path.Combine(source, name), Path.Combine(live, name));
            Require(Deployment.RecoverPayload(pin, "0.1.5").GetAwaiter().GetResult()?.Version == "0.1.5" &&
                Deployment.ReadPayload(live).Version == "0.1.5" && !File.Exists(Path.Combine(live, "New.dll")),
                "Setup did not recover the registered payload after an interruption.");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS checked historical cleanup, interrupted setup and rollback");
    }

    private static string[] WritePayload(string root, string version, byte content, string sidecar)
    {
        var files = new[] { Deployment.Executable, "LICENSE", "THIRD-PARTY-NOTICES.txt", sidecar }.Select(name =>
        {
            byte[] bytes = [content];
            File.WriteAllBytes(Path.Combine(root, name), bytes);
            return new Deployment.PayloadFile(name, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }).ToArray();
        File.WriteAllBytes(Path.Combine(root, Deployment.Manifest), JsonSerializer.SerializeToUtf8Bytes(new Deployment.Payload(version, files)));
        return files.Select(file => file.Name).Where(name => name != Deployment.Executable).Append(Deployment.Manifest).Append(Deployment.Executable).ToArray();
    }

    private static void Expect(Exception expected, Action action)
    {
        try { action(); }
        catch (Exception error) when (error == expected) { return; }
        throw new IOException("Setup did not report the original failure.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string path, string target, IntPtr reserved);

    private static void CheckMachineShortcutBackupPermissions()
    {
        const string fixture = "machine shortcut backup fixture";
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = WindowsPermissions.MachineShortcutBackupAcl();
        WindowsPermissions.RequireMachine(acl, fixture);
        if (!Equals(acl.GetOwner(typeof(SecurityIdentifier)), administrators) || !acl.AreAccessRulesProtected)
            throw new IOException("The backup needs an Administrators owner and a protected ACL.");

        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().ToArray();
        bool Allows(SecurityIdentifier sid, FileSystemRights rights) => rules.Any(rule =>
            Equals(rule.IdentityReference, sid) && rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights == rights);
        if (rules.Length != 3 || !Allows(system, FileSystemRights.FullControl) || !Allows(administrators, FileSystemRights.FullControl) ||
            !Allows(users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize))
            throw new IOException("The backup ACL must give standard users read access and reserve writes for SYSTEM and Administrators.");

        var oldAcl = WindowsPermissions.PrivateAcl<FileSecurity>();
        oldAcl.SetOwner(administrators);
        RejectMutation(fixture, () => WindowsPermissions.RequireMachine(oldAcl, fixture));
        Console.WriteLine("PASS machine shortcut backup owner and access rules");
    }

    private static void CheckInheritedShortcutDelete()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hytale-shortcut-delete-check-" + Guid.NewGuid().ToString("N"));
        WindowsPermissions.CreatePrivateDirectory(root);
        try
        {
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var parent = new DirectoryInfo(root);
            var acl = parent.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            parent.SetAccessControl(acl);

            var shortcut = Path.Combine(root, "Hytale Launcher.lnk");
            File.WriteAllBytes(shortcut, [1]);
            RejectMutation(shortcut, () => { using var file = WindowsFiles.OpenOwned(shortcut); });
            using (var file = WindowsFiles.OpenMachineShortcut(shortcut)) file.Write([2]);
            using (var file = WindowsFiles.OpenMachineShortcut(shortcut))
            {
                if (!file.Read().AsSpan().SequenceEqual(new byte[] { 2 }))
                    throw new IOException("Setup did not update the machine shortcut.");
                file.Write([1]);
            }
            if (!File.ReadAllBytes(shortcut).AsSpan().SequenceEqual(new byte[] { 1 }))
                throw new IOException("Setup did not restore the machine shortcut.");

            var info = new FileInfo(shortcut);
            var fileAcl = info.GetAccessControl();
            var explicitDelete = new FileSystemAccessRule(users, FileSystemRights.Delete, AccessControlType.Allow);
            fileAcl.AddAccessRule(explicitDelete);
            info.SetAccessControl(fileAcl);
            RejectMutation(shortcut, () => { using var file = WindowsFiles.OpenMachineShortcut(shortcut); });
            fileAcl.RemoveAccessRuleSpecific(explicitDelete);
            info.SetAccessControl(fileAcl);

            foreach (var right in new[] { FileSystemRights.CreateFiles, FileSystemRights.DeleteSubdirectoriesAndFiles })
            {
                var replacement = new FileSystemAccessRule(users, right, AccessControlType.Allow);
                acl.AddAccessRule(replacement);
                parent.SetAccessControl(acl);
                RejectMutation(root, () => { using var file = WindowsFiles.OpenMachineShortcut(shortcut); });
                acl.RemoveAccessRuleSpecific(replacement);
                parent.SetAccessControl(acl);
            }

            acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.WriteAttributes,
                InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            parent.SetAccessControl(acl);
            RejectMutation(shortcut, () => { using var file = WindowsFiles.OpenMachineShortcut(shortcut); });
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS machine shortcut update and restoration with inherited delete rights");
    }

    private static void CheckShortcutPermissions()
    {
        const string programDataAcl = "O:SYG:SYD:PAI(A;OICIIO;GA;;;CO)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;CI;0x116;;;BU)";
        var descriptor = new DirectorySecurity();
        descriptor.SetSecurityDescriptorSddlForm(programDataAcl);
        WindowsPermissions.RequireMachine(descriptor, "ProgramData fixture", ancestor: true);

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "hytale-shortcut-acl-check-" + Guid.NewGuid().ToString("N"));
        WindowsPermissions.CreatePrivateDirectory(root);
        try
        {
            var child = Path.Combine(root, "protected");
            WindowsPermissions.CreatePrivateDirectory(child);
            var shortcut = Path.Combine(child, "Hytale Launcher.lnk");
            File.WriteAllBytes(shortcut, [1]);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var directory = new DirectoryInfo(root);
            var acl = directory.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(users, (FileSystemRights)0x116,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);

            using (var file = WindowsFiles.OpenOwned(shortcut)) file.Write([2]);
            if (!File.ReadAllBytes(shortcut).AsSpan().SequenceEqual(new byte[] { 2 }))
                throw new IOException("Setup could not update a protected shortcut.");
            RejectMutation(root, () => { using var pin = new WindowsFiles.DirectoryPin(root); });

            var shortcutInfo = new FileInfo(shortcut);
            var fileAcl = shortcutInfo.GetAccessControl();
            var fileRule = new FileSystemAccessRule(users, FileSystemRights.WriteAttributes, AccessControlType.Allow);
            fileAcl.AddAccessRule(fileRule);
            shortcutInfo.SetAccessControl(fileAcl);
            RejectMutation(shortcut, () => { using var file = WindowsFiles.OpenOwned(shortcut); });
            fileAcl.RemoveAccessRuleSpecific(fileRule);
            shortcutInfo.SetAccessControl(fileAcl);

            acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
            directory.SetAccessControl(acl);
            RejectMutation(root, () => { using var file = WindowsFiles.OpenOwned(shortcut); });
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS setup shortcut permissions for ancestors, leaves and files");
    }

    private static void RejectMutation(string path, Action open)
    {
        try { open(); }
        catch (IOException error) when (error.Message.StartsWith($"Unsafe mutation permissions on '{path}'", StringComparison.Ordinal)) { return; }
        throw new IOException($"Setup accepted unsafe permissions on '{path}'.");
    }

    private static void Reject(Deployment.Payload payload)
    {
        try { _ = Deployment.ParsePayload(JsonSerializer.SerializeToUtf8Bytes(payload)); }
        catch (IOException) { return; }
        throw new IOException("Setup accepted an invalid package manifest.");
    }
}
