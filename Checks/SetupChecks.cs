using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using HytaleAccounts;

internal static class SetupChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { check(true, message); return; }
            throw new Exception(message);
        }
        var acl = SetupPayload.Permissions();
        SetupPayload.RequireTrusted(acl);
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        check(acl.AreAccessRulesProtected && acl.GetOwner(typeof(SecurityIdentifier))!.Value == "S-1-5-32-544" &&
            rules.Length == 2 && rules.All(r => (r.IdentityReference.Value is "S-1-5-18" or "S-1-5-32-544") &&
                r.FileSystemRights == FileSystemRights.FullControl && r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)),
            "production staging grants only Administrators/SYSTEM access and inheritance, with Administrators ownership");
        foreach (var rights in new[] { FileSystemRights.WriteData, FileSystemRights.DeleteSubdirectoriesAndFiles, FileSystemRights.Delete,
            FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership, FileSystemRights.WriteAttributes })
        {
            var broad = SetupPayload.Permissions();
            broad.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), rights, AccessControlType.Allow));
            Reject(() => SetupPayload.RequireTrusted(broad), "unprivileged installer mutation permissions rejected: " + rights);
        }
        var genericWrite = new DirectorySecurity(); genericWrite.SetSecurityDescriptorSddlForm("O:BAG:BAD:(A;;GW;;;BU)");
        Reject(() => SetupPayload.RequireTrusted(genericWrite), "generic write permission rejected");
        var append = new FileSecurity(); append.SetSecurityDescriptorSddlForm("O:BAG:BAD:(A;;0x4;;;BU)");
        Reject(() => SetupPayload.RequireTrusted(append), "append-only mutation of installed payload rejected");
        var user = WindowsIdentity.GetCurrent().User!;
        var foreignOwner = SetupPayload.Permissions(); foreignOwner.SetOwner(user);
        Reject(() => SetupPayload.RequireTrusted(foreignOwner), "user-owned installer paths rejected despite restrictive DACL");
        var nullDacl = new DirectorySecurity(); nullDacl.SetSecurityDescriptorSddlForm("O:BAG:BAD:NO_ACCESS_CONTROL");
        Reject(() => SetupPayload.RequireTrusted(nullDacl), "null DACL rejected");
        try { await Setup.RunElevated([Setup.ElevatedMode]); throw new Exception("Malformed worker accepted"); }
        catch (IOException) { check(true, "malformed worker rejected before staging"); }
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            try { await Setup.RunElevated([Setup.ElevatedMode, @"C:\", @"C:\", user.Value]); throw new Exception("Unelevated worker accepted"); }
            catch (IOException) { check(true, "unelevated worker refused without requesting elevation"); }
        }

        var root = SecurityFixture.Create("setup check-");
        // Fixtures use the current user; no admin token or protected system files are needed.
        var fixtureAcl = new DirectorySecurity(); fixtureAcl.SetOwner(user); fixtureAcl.SetAccessRuleProtection(true, false);
        fixtureAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var stage = Path.Combine(root, "stage");
        var executable = Path.Combine(root, "download.exe");
        File.WriteAllText(executable, "synthetic installer image");
        const string script = "param([string]$ProgramsDirectory,[string]$DesktopDirectory)\n$ErrorActionPreference='Stop'\n" +
            "Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'HytaleAccounts.exe') -Destination (Join-Path $ProgramsDirectory 'installed.bin')\n" +
            "Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'InstallerSafety.ps1') -Destination (Join-Path $DesktopDirectory 'installed-script.bin')\n" +
            "[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'install-error.txt'), 'synthetic script ran')\n";
        Stream Resource(string name) => new MemoryStream(Encoding.UTF8.GetBytes(name == "Install.ps1" ? script : "embedded synthetic " + name));
        void Cleanup(string path)
        {
            var full = Path.GetFullPath(path);
            if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture cleanup escaped root.");
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.Directory) == 0) { File.Delete(full); return; }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                foreach (var child in Directory.EnumerateFileSystemEntries(full)) Cleanup(child);
            Directory.Delete(full);
        }
        try
        {
            var attempt = Guid.NewGuid().ToString("N");
            using (var signal = Setup.RemovalEvent(attempt, "confirmed"))
            {
                Reject(() => EventWaitHandleAcl.OpenExisting("Local\\HytaleAccounts-Uninstall-" + attempt + "-confirmed", EventWaitHandleRights.Modify).Dispose(),
                    "another handle cannot forge finalization, even as the event owner");
                Reject(() => EventWaitHandleAcl.OpenExisting("Local\\HytaleAccounts-Uninstall-" + attempt + "-confirmed", EventWaitHandleRights.ChangePermissions).Dispose(),
                    "event owner cannot grant itself confirmation rights");
                using var reader = EventWaitHandleAcl.OpenExisting("Local\\HytaleAccounts-Uninstall-" + attempt + "-confirmed", EventWaitHandleRights.Synchronize);
                check(!reader.WaitOne(0), "confirmation starts unsignaled");
                signal.Set();
                check(reader.WaitOne(0), "only the original confirmation handle signals success");
                Reject(() => Setup.RemovalEvent(attempt, "confirmed").Dispose(), "an existing removal attempt cannot be adopted");
            }
            File.WriteAllText(Path.Combine(root, "Uninstall.ps1"), "non-executed removal fixture");
            File.WriteAllText(Path.Combine(root, "ShortcutIntegration.ps1"), "non-executed integration fixture");
            foreach (var failure in new Exception[] { new Win32Exception(1223), new IOException("Could not start uninstall.") })
            {
                var phases = new List<string>();
                Task Integrate(ProcessStartInfo start)
                {
                    check(!start.UseShellExecute && start.CreateNoWindow && start.ArgumentList.Contains("-UserOnly"), "uninstall recovery remains unelevated and hidden");
                    if (start.ArgumentList.Contains(Path.Combine(root, "Uninstall.ps1")))
                    {
                        check(start.ArgumentList.Contains("-KeepJournal"), "pre-elevation removal retains restoration records");
                        phases.Add("prepare");
                    }
                    else { check(start.ArgumentList.Contains(Path.Combine(root, "Install.ps1")), "failed elevation restores existing user integration"); phases.Add("recover"); }
                    return Task.CompletedTask;
                }
                try
                {
                    await Setup.Remove(root, Integrate, start =>
                    {
                        check(phases.SequenceEqual(["prepare"]), "synthetic start failure occurs after the per-user phase");
                        check(start.UseShellExecute && start.Verb == "runas" && start.WindowStyle == ProcessWindowStyle.Hidden &&
                            start.ArgumentList.Contains(Environment.ProcessId.ToString()) && start.ArgumentList.Contains(user.Value),
                            "removal uses hidden elevation and waits for the initiating process");
                        phases.Add("start"); throw failure;
                    });
                    throw new Exception("Synthetic removal failure was ignored.");
                }
                catch (Exception ex) when (ReferenceEquals(ex, failure)) { }
                check(phases.SequenceEqual(["prepare", "start", "recover"]), "cancellation/start failure recovers before returning the original error");
            }
            var cancelled = new Win32Exception(1223);
            var recoveryFailure = new IOException("Synthetic recovery failure.");
            try
            {
                await Setup.Remove(root, start => start.ArgumentList.Contains("-KeepJournal") ? Task.CompletedTask : Task.FromException(recoveryFailure), _ => throw cancelled);
                throw new Exception("Recovery failure was ignored.");
            }
            catch (AggregateException ex)
            {
                check(ex.InnerExceptions.SequenceEqual(new Exception[] { cancelled, recoveryFailure }), "failed immediate recovery reports both errors and leaves retained state for background repair");
            }
            var successfulPhases = new List<string>();
            await Setup.Remove(root, start =>
            {
                check(start.ArgumentList.Contains(Path.Combine(root, "Uninstall.ps1")), "successful removal only invokes user uninstall");
                successfulPhases.Add(start.ArgumentList.Contains("-KeepJournal") ? "prepare" : "finalize");
                return Task.CompletedTask;
            }, start =>
            {
                successfulPhases.Add("start");
                var removalAttempt = start.ArgumentList[start.ArgumentList.IndexOf("-RemovalAttempt") + 1];
                _ = Task.Run(() =>
                {
                    using var confirmed = EventWaitHandleAcl.OpenExisting("Local\\HytaleAccounts-Uninstall-" + removalAttempt + "-confirmed", EventWaitHandleRights.Synchronize);
                    using var received = EventWaitHandleAcl.OpenExisting("Local\\HytaleAccounts-Uninstall-" + removalAttempt + "-received", EventWaitHandleRights.Modify);
                    if (confirmed.WaitOne(TimeSpan.FromSeconds(10))) received.Set();
                });
            });
            check(successfulPhases.SequenceEqual(["prepare", "start", "finalize"]), "successful start finalizes user restoration before the parent exits");

            using (var directories = new SetupPayload.PinnedDirectories(root, false))
            using (var source = SetupPayload.OpenSource(executable))
            {
                using (var directoryWriter = OpenDirectory(root, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
                    check(directoryWriter.IsInvalid && Marshal.GetLastWin32Error() == 32,
                        "pinned directory denies write handles needed for in-place reparse conversion");
                Reject(() => File.WriteAllText(executable, "substituted"), "source overwrite blocked during elevation/copy");
                Reject(() => File.Move(executable, executable + ".moved"), "source replacement blocked");
                Reject(() => Directory.Move(root, root + "-moved"), "source ancestor rename blocked");
                File.WriteAllText(Path.Combine(root, "Install.ps1"), "throw 'untrusted loose script'");
                using (var package = SetupPayload.Extract(stage, source, Resource, fixtureAcl))
                {
                    foreach (var name in SetupPayload.Scripts.Concat(["HytaleAccounts.exe"]))
                    {
                        var payload = Path.Combine(stage, name);
                        Reject(() => File.WriteAllText(payload, "substituted"), "payload overwrite blocked: " + name);
                        Reject(() => File.Delete(payload), "payload replacement blocked: " + name);
                    }
                    var elevation = Setup.ElevationStart(executable, root, root);
                    check(elevation.FileName == executable && elevation.Verb == "runas" && elevation.UseShellExecute &&
                        elevation.WindowStyle == ProcessWindowStyle.Hidden && elevation.ArgumentList.SequenceEqual(new[] { Setup.ElevatedMode, root, root, user.Value }),
                        "UAC targets installer executable, with shortcut paths as separate arguments");
                    var start = Setup.InstallStart(stage, root, root);
                    check(!start.UseShellExecute && start.CreateNoWindow && start.Verb == "" && start.WindowStyle == ProcessWindowStyle.Hidden &&
                        start.WorkingDirectory == stage && start.ArgumentList.Contains(Path.Combine(stage, "Install.ps1")) &&
                        start.Environment["PSModulePath"] == Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules"),
                        "PowerShell runs hidden with staged scripts and system modules, without runas");
                    using var child = Process.Start(start)!;
                    await child.WaitForExitAsync();
                    check(child.ExitCode == 0 && File.ReadAllText(Path.Combine(root, "installed.bin")) == "synthetic installer image" &&
                        File.ReadAllText(Path.Combine(root, "installed-script.bin")) == "embedded synthetic InstallerSafety.ps1" &&
                        File.ReadAllText(Path.Combine(stage, "install-error.txt")) == "synthetic script ran",
                        "synthetic execution/installation consumes locked payloads, ignoring loose scripts");
                    foreach (var remove in new[] { false, true })
                    {
                        var userStart = Setup.UserIntegrationStart(root, remove);
                        check(!userStart.UseShellExecute && userStart.Verb == "" && userStart.CreateNoWindow &&
                            userStart.ArgumentList.Contains("-UserOnly") && userStart.ArgumentList.Contains(Path.Combine(root, remove ? "Uninstall.ps1" : "Install.ps1")),
                            "per-user shortcut phase inherits unelevated token: " + (remove ? "removal" : "installation"));
                    }
                    var launcher = Setup.LauncherStart(root);
                    check(!launcher.UseShellExecute && launcher.Verb == "" && launcher.CreateNoWindow &&
                        new[] { "APPDATA", "LOCALAPPDATA", "USERPROFILE", "TEMP", "TMP" }.All(k => launcher.Environment[k] == Environment.GetEnvironmentVariable(k)),
                        "launcher inherits parent token/profile without environment overrides");
                }
                check(!Directory.Exists(stage), "staging and diagnostics cleaned after success");
            }
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "foreign.txt"), "preserve");
            using (var source = SetupPayload.OpenSource(executable))
                Reject(() => SetupPayload.Extract(stage, source, Resource, fixtureAcl), "pre-existing staging rejected without adoption/cleanup");
            check(File.ReadAllText(Path.Combine(stage, "foreign.txt")) == "preserve", "pre-existing contents preserved");
            Cleanup(stage);
            using (var source = SetupPayload.OpenSource(executable))
                Reject(() => SetupPayload.Extract(stage, source, _ => throw new IOException("missing resource fixture"), fixtureAcl), "missing embedded resource fails closed");
            check(!Directory.Exists(stage), "partial extraction cleaned without execution");
            using (var source = new ProbeStream(() =>
            {
                Reject(() => Directory.Move(stage, stage + "-moved"), "empty staging directory is pinned before the first payload bytes");
                using var writer = OpenDirectory(stage, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                check(writer.IsInvalid && Marshal.GetLastWin32Error() == 32, "empty staging cannot acquire a reparse writer during extraction");
            }))
            using (var package = SetupPayload.Extract(stage, source, Resource, fixtureAcl))
                check(File.ReadAllText(Path.Combine(stage, "HytaleAccounts.exe")) == "synthetic probe", "pinned extraction completes normally");
            using (var source = SetupPayload.OpenSource(executable))
            using (var package = SetupPayload.Extract(stage, source,
                name => new MemoryStream(Encoding.UTF8.GetBytes(name == "Install.ps1" ? "exit 23" : "unused synthetic resource")), fixtureAcl))
            {
                using var child = Process.Start(Setup.InstallStart(stage, root, root))!;
                await child.WaitForExitAsync();
                check(child.ExitCode == 23, "synthetic script failure preserves the nonzero exit status");
            }
            check(!Directory.Exists(stage), "staging cleaned after synthetic script failure");
            Directory.CreateDirectory(stage);
            var link = Path.Combine(root, "link");
            var junction = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true };
            junction.Arguments = $"/c mklink /J \"{link}\" \"{stage}\"";
            using (var child = Process.Start(junction)!) { await child.WaitForExitAsync(); check(child.ExitCode == 0, "synthetic junction created"); }
            Reject(() => { using var pinned = new SetupPayload.PinnedDirectories(link, false); }, "linked source/staging ancestors rejected");
            using (var source = SetupPayload.OpenSource(executable))
                Reject(() => SetupPayload.Extract(link, source, Resource, fixtureAcl), "preplanted staging junction rejected");
            var hardlink = Path.Combine(root, "hardlink.exe");
            if (!CreateHardLink(hardlink, executable, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Reject(() => { using var input = SetupPayload.OpenSource(executable); }, "hard-linked executable payload rejected");
        }
        finally { Cleanup(root); }
    }
    private sealed class ProbeStream(Action probe) : MemoryStream(Encoding.UTF8.GetBytes("synthetic probe"))
    {
        public override void CopyTo(Stream destination, int bufferSize) { probe(); base.CopyTo(destination, bufferSize); }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existing, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path, uint access, uint share,
        IntPtr security, uint mode, uint flags, IntPtr template);
}
