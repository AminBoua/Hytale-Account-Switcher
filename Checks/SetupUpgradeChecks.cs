using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace HytaleAccounts;

public static class SetupUpgradeChecks
{
    internal static string Executable = "", Programs = "", OfficialPath = "";
    internal static string LocalApplicationData = "", DesktopDirectory = "";
    internal static bool Administrator = true;
    private static bool preflight;
    private static bool lifecycle;
    private static int payloadVersion = 1;
    private static string commonPrograms = "", commonDesktop = "", programFiles = "";
    private static readonly string registryName = "Software\\HytaleAccounts-SetupPreflight-" + Guid.NewGuid().ToString("N");
    private static string repository = "", root = "";
    internal static DirectorySecurity Permissions() => new DirectoryInfo(root).GetAccessControl();
    internal static Stream Resource(string name)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        if (lifecycle)
        {
            var bytes = File.ReadAllText(Path.Combine(repository, name));
            if (name == "Install.ps1")
            {
                const string version = "DisplayVersion = '0.1.5'";
                if (!bytes.Contains(version)) throw new Exception("Lifecycle DisplayVersion fixture boundary changed.");
                bytes = bytes.Replace(version, "DisplayVersion = '0.1." + payloadVersion + "'");
                bytes = bytes.Replace("[Environment]::GetFolderPath('CommonPrograms')", Quote(commonPrograms))
                    .Replace("[Environment]::GetFolderPath('CommonDesktopDirectory')", Quote(commonDesktop))
                    .Replace("'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\HytaleAccounts'", Quote("HKCU:\\" + registryName))
                    .Replace("$ErrorActionPreference = 'Stop'", "$ErrorActionPreference = 'Stop'\n$env:ProgramFiles = " + Quote(programFiles) +
                        "\n$env:LOCALAPPDATA = " + Quote(LocalApplicationData) + "\n" + FixtureBoundaries);
            }
            return new MemoryStream(Encoding.UTF8.GetBytes(bytes + (name.EndsWith(".cs") ? "\n//" : "\n#") + " fixture payload " + payloadVersion));
        }
        if (name != "Install.ps1") return File.OpenRead(Path.Combine(repository, name));
        if (!preflight) return new MemoryStream(Encoding.UTF8.GetBytes(Installer));
        var script = File.ReadAllText(Path.Combine(repository, name))
            .Replace("[Environment]::GetFolderPath('CommonPrograms')", Quote(commonPrograms))
            .Replace("[Environment]::GetFolderPath('CommonDesktopDirectory')", Quote(commonDesktop))
            .Replace("'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\HytaleAccounts'", Quote("HKCU:\\" + registryName))
            .Replace("$ErrorActionPreference = 'Stop'", "$ErrorActionPreference = 'Stop'\n$env:ProgramFiles = " + Quote(programFiles) +
                "\n$env:LOCALAPPDATA = " + Quote(LocalApplicationData) + "\nfunction Get-CimInstance { throw 'Preflight attempted process access.' }\n" +
                "function Get-ScheduledTask { throw 'Preflight attempted task access.' }\nfunction Register-ScheduledTask { throw 'Preflight attempted task registration.' }");
        return new MemoryStream(Encoding.UTF8.GetBytes(script));
    }
    internal static Process StartElevation(ProcessStartInfo start) => throw new IOException("fixture elevation boundary");

    public static async Task Run(string source)
    {
        repository = source;
        root = SecurityFixture.Create("setup-upgrade-");
        try
        {
            Executable = Path.Combine(root, "download.exe");
            File.WriteAllText(Executable, "non-executed installer fixture");
            foreach (var name in SetupPayload.RuntimeFiles) File.WriteAllText(Path.Combine(root, name), "non-executed runtime fixture");
            Programs = Path.Combine(root, "program-files");
            OfficialPath = Path.Combine(Programs, "Hypixel Studios", "Hytale Launcher", "hytale-launcher.exe");
            var destination = Path.Combine(Path.GetDirectoryName(OfficialPath)!, ".hytale-accounts-addon");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "existing.bin"), "old payload");
            File.WriteAllText(Path.Combine(destination, "existing.json"), "{}");
            var args = new[] { Setup.ElevatedMode, destination, root, WindowsIdentity.GetCurrent().User!.Value };
            using (var writer = OpenDirectory(destination, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                if (writer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { await Setup.RunElevated(args); throw new Exception("Worker accepted an existing directory writer."); }
                catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 32 } && ex.Message.Contains(destination))
                { Console.WriteLine("PASS setup pin excludes existing directory writers before validation"); }
            }
            await Setup.RunElevated(args);
            foreach (var name in new[] { "existing", "missing" })
                if (File.ReadAllText(Path.Combine(destination, name + ".bin")) != "new " + name)
                    throw new Exception("Worker did not publish " + name);
            if (Directory.EnumerateDirectories(Programs, "HytaleAccounts-setup-*").Any())
                throw new Exception("Worker left staging behind.");
            Console.WriteLine("PASS setup worker retains its destination pin across existing/missing payload and journal publication, then cleans staging");
            await CheckPreflight();
            await CheckLifecycle();
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registryName, throwOnMissingSubKey: false);
            var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).TrimEnd('\\') + '\\';
            if (!Path.GetFullPath(root).StartsWith(local, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("setup-upgrade-"))
                throw new Exception("Cleanup escaped setup fixture.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckLifecycle()
    {
        lifecycle = true;
        Administrator = true;
        programFiles = Programs = Path.Combine(root, "lifecycle-program-files");
        OfficialPath = Path.Combine(Programs, "Hypixel Studios", "Hytale Launcher", "hytale-launcher.exe");
        LocalApplicationData = Path.Combine(root, "lifecycle-local");
        var userPrograms = Path.Combine(root, "lifecycle-user-programs");
        var userDesktop = Path.Combine(root, "lifecycle-user-desktop");
        commonPrograms = Path.Combine(root, "lifecycle-common-programs");
        commonDesktop = Path.Combine(root, "lifecycle-common-desktop");
        foreach (var directory in new[] { Path.GetDirectoryName(OfficialPath)!, LocalApplicationData, userPrograms, userDesktop, commonPrograms, commonDesktop })
            Directory.CreateDirectory(directory);
        // Match InstallerScriptChecks: initialize automatic inheritance before creating fixture payloads.
        var launcherDirectory = new DirectoryInfo(Path.GetDirectoryName(OfficialPath)!);
        var launcherPermissions = launcherDirectory.GetAccessControl();
        launcherPermissions.SetAccessRuleProtection(true, true);
        launcherDirectory.SetAccessControl(launcherPermissions);
        File.WriteAllText(OfficialPath, "official fixture bytes");
        var officialAcl = new FileInfo(OfficialPath).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        var sentinels = new[] { "Accounts/one/account.dat", "Accounts/one/.keys/key", "install/game", "UserData/world", "credentials.dpapi" }
            .Select(name => Path.Combine(LocalApplicationData, "Hytale", name)).ToArray();
        foreach (var path in sentinels) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "opaque sentinel"); }
        var sentinelAcls = Directory.EnumerateFileSystemEntries(Path.Combine(LocalApplicationData, "Hytale"), "*", SearchOption.AllDirectories)
            .Append(Path.Combine(LocalApplicationData, "Hytale")).ToDictionary(path => path, Descriptor);
        static string Descriptor(string path) => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.All);
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var links = new[] { Path.Combine(commonPrograms, "Hytale.lnk"), Path.Combine(userPrograms, "Hytale.lnk") };
        var create = Setup.InstallStart(root, userPrograms, userDesktop);
        create.ArgumentList.Clear();
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command" }) create.ArgumentList.Add(arg);
        create.ArgumentList.Add("$s = New-Object -ComObject WScript.Shell; " + string.Join("; ", links.Select(path =>
            "$l = $s.CreateShortcut(" + Quote(path) + "); $l.TargetPath = " + Quote(OfficialPath) + "; $l.Save()")));
        using (var child = Process.Start(create)!) { await child.WaitForExitAsync(); if (child.ExitCode != 0) throw new Exception("Lifecycle shortcut creation failed."); }
        var originals = links.Select(File.ReadAllBytes).ToArray();
        var destination = Path.Combine(Path.GetDirectoryName(OfficialPath)!, ".hytale-accounts-addon");
        var args = new[] { Setup.ElevatedMode, userPrograms, userDesktop, WindowsIdentity.GetCurrent().User!.Value };
        async Task Install()
        {
            // RunElevated retains all production source, installation and destination pins across the real PS 5.1 child.
            await Setup.RunElevated(args);
            var start = Setup.InstallStart(destination, userPrograms, userDesktop);
            start.ArgumentList.Add("-UserOnly");
            using var child = Process.Start(start)!;
            await child.WaitForExitAsync();
            if (child.ExitCode != 0) throw new Exception("Lifecycle user integration failed.");
        }
        void WritePayload()
        {
            File.WriteAllText(Executable, "non-executed helper version " + payloadVersion);
            foreach (var name in SetupPayload.RuntimeFiles) File.WriteAllText(Path.Combine(root, name), "runtime version " + payloadVersion + ": " + name);
        }
        void CheckPreserved(Dictionary<string, byte[]> recovery)
        {
            foreach (var (path, bytes) in recovery)
                if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new Exception("Lifecycle changed recovery bytes: " + path);
            if (File.ReadAllText(OfficialPath) != "official fixture bytes" ||
                new FileInfo(OfficialPath).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All) != officialAcl ||
                sentinels.Any(path => File.ReadAllText(path) != "opaque sentinel") ||
                sentinelAcls.Any(item => Descriptor(item.Key) != item.Value)) throw new Exception("Lifecycle changed official launcher or sentinel bytes/ACLs.");
            using var entry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryName);
            if ((string?)entry?.GetValue("InstallLocation") != destination ||
                (string?)entry.GetValue("UninstallString") != "\"" + Path.Combine(destination, "HytaleAccounts.exe") + "\" --uninstall")
                throw new Exception("Lifecycle lost Windows Apps removal registration.");
        }
        WritePayload();
        await Install();
        var states = new[] { destination, Path.Combine(LocalApplicationData, "HytaleAccounts", "ShortcutIntegration") };
        if (states.Any(path => !File.Exists(Path.Combine(path, "shortcuts.json")))) throw new Exception("Lifecycle requires both existing journals.");
        var recovery = states.SelectMany(path => Directory.EnumerateFiles(path).Where(file => Path.GetFileName(file) == "shortcuts.json" || Path.GetFileName(file).StartsWith("shortcut-")))
            .ToDictionary(path => path, File.ReadAllBytes);
        foreach (var path in links) recovery.Add(path, File.ReadAllBytes(path));
        var recoveryAcls = recovery.Keys.ToDictionary(path => path, Descriptor);
        void CheckRecoveryAcls()
        {
            foreach (var (path, acl) in recoveryAcls)
                if (Descriptor(path) != acl) throw new Exception("Lifecycle changed exact recovery ACL: " + path);
        }
        string Registration()
        {
            using var entry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryName)!;
            return string.Join("\n", entry.GetValueNames().Order(StringComparer.Ordinal).Select(name =>
                name + "=" + entry.GetValueKind(name) + ":" + entry.GetValue(name)));
        }
        var oldRegistration = Registration();
        for (var index = 0; index < states.Length; index++)
            if (!Directory.EnumerateFiles(states[index], "shortcut-*.lnk").Any(path => File.ReadAllBytes(path).SequenceEqual(originals[index])))
                throw new Exception("Lifecycle did not retain original shortcut backup bytes.");
        CheckPreserved(recovery);
        using (var entry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryName))
            if ((string?)entry!.GetValue("DisplayVersion") != "0.1.1") throw new Exception("Fresh setup did not register its version.");
        Console.WriteLine("PASS real setup fresh installation creates both journals, exact shortcut backups and Windows Apps registration");
        payloadVersion++;
        WritePayload();
        var payload = SetupPayload.Scripts.Concat(SetupPayload.RuntimeFiles).Append("HytaleAccounts.exe").ToArray();
        var oldPayload = payload.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(destination, name)));
        var protectedFile = new FileInfo(Path.Combine(destination, "Repair-Integration.ps1"));
        var protectedAcl = protectedFile.GetAccessControl();
        protectedAcl.SetAccessRuleProtection(true, true);
        protectedFile.SetAccessControl(protectedAcl);
        var payloadAcls = payload.ToDictionary(name => name, name => new FileInfo(Path.Combine(destination, name)).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        byte[] ExpectedPayload(string name)
        {
            using var source = SetupPayload.Scripts.Contains(name) ? Resource(name) : File.OpenRead(name == "HytaleAccounts.exe" ? Executable : Path.Combine(root, name));
            using var expected = new MemoryStream(); source.CopyTo(expected); return expected.ToArray();
        }
        foreach (var name in payload)
            if (oldPayload[name].SequenceEqual(ExpectedPayload(name))) throw new Exception("Lifecycle requires genuinely different upgrade bytes: " + name);
        // An exclusive reader is a real obstruction; setup must reject before publishing anything.
        using (var locked = File.Open(Path.Combine(destination, "Repair-Integration.ps1"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { await Setup.RunElevated(args); throw new Exception("Lifecycle accepted locked destination."); }
            catch (IOException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 32 }) { }
        }
        foreach (var (name, bytes) in oldPayload)
            if (!File.ReadAllBytes(Path.Combine(destination, name)).SequenceEqual(bytes)) throw new Exception("Locked upgrade changed " + name);
        CheckPreserved(recovery);
        using (var entry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryName))
            if ((string?)entry!.GetValue("DisplayVersion") != "0.1.1") throw new Exception("Locked upgrade changed the registered version.");
        Console.WriteLine("PASS real setup locked destination preserves old payload and recovery state");
        CheckRecoveryAcls();
        if (Registration() != oldRegistration) throw new Exception("Locked upgrade changed Windows Apps values or types.");
        var lockedPath = Path.Combine(destination, "Repair-Integration.ps1");
        async Task CheckConstructorConflict(string owner)
        {
            try { await Setup.RunElevated(args); throw new Exception("Setup accepted a read-pinned payload."); }
            catch (IOException ex) when (ex.Message.StartsWith("Installation failed.") && ex.Message.Contains("\".ctor\" with \"2\" argument(s)"))
            {
                if (!ex.Message.Contains(lockedPath) || !ex.Message.Contains("Windows error 32") ||
                    !ex.Message.Contains("Win32Exception") || !ex.Message.Contains("Open-ShortcutFile") || !ex.Message.Contains("Install.ps1") ||
                    !ex.Message.Contains("Installer PowerShell PID "))
                    throw new Exception("Constructor failure lost its path, native cause or installer caller: " + ex.Message, ex);
            }
            foreach (var (name, bytes) in oldPayload)
            {
                if (!File.ReadAllBytes(Path.Combine(destination, name)).SequenceEqual(bytes) ||
                    new FileInfo(Path.Combine(destination, name)).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All) != payloadAcls[name])
                    throw new Exception("Constructor obstruction changed payload bytes or ACL: " + name);
            }
            CheckPreserved(recovery);
            CheckRecoveryAcls();
            if (Registration() != oldRegistration) throw new Exception("Constructor obstruction changed Windows Apps values or types.");
            using var entry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryName);
            if ((string?)entry!.GetValue("DisplayVersion") != "0.1.1" || Directory.EnumerateFiles(destination, ".upgrade-*").Any() ||
                Directory.EnumerateDirectories(Programs, "HytaleAccounts-setup-*").Any())
                throw new Exception("Constructor obstruction changed registration or leaked staging.");
            Console.WriteLine("PASS real worker constructor conflict with " + owner + " identifies path/native cause/caller and preserves installation");
        }
        // Seed a setup-owned source pin deliberately; do not mislabel this control as a naturally occurring leak.
        using (var reader = SetupPayload.OpenSource(lockedPath))
            await CheckConstructorConflict("seeded setup source reader PID " + Environment.ProcessId);
        var obstruction = Setup.InstallStart(root, userPrograms, userDesktop);
        obstruction.ArgumentList.Clear();
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command" }) obstruction.ArgumentList.Add(arg);
        obstruction.ArgumentList.Add("$ErrorActionPreference = 'Stop'; $f = [IO.File]::Open(" + Quote(lockedPath) +
            ", 'Open', 'Read', 'Read'); try { [Console]::WriteLine('locked'); $null = [Console]::ReadLine() } finally { $f.Dispose() }");
        obstruction.RedirectStandardInput = true;
        obstruction.RedirectStandardOutput = true;
        using (var holder = Process.Start(obstruction)!)
        {
            try
            {
                if (await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) != "locked")
                    throw new Exception("External obstruction did not acquire its fixture handle.");
                await CheckConstructorConflict("external PowerShell reader PID " + holder.Id);
            }
            finally
            {
                holder.StandardInput.Close();
                await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            if (holder.ExitCode != 0) throw new Exception("External obstruction fixture failed.");
        }
        try { await Install(); }
        finally { CheckPreserved(recovery); CheckRecoveryAcls(); }
        foreach (var name in payload)
        {
            var installed = File.ReadAllBytes(Path.Combine(destination, name));
            if (!installed.SequenceEqual(ExpectedPayload(name))) throw new Exception("Upgrade did not publish complete bytes: " + name);
        }
        using (var entry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryName))
            if ((string?)entry!.GetValue("DisplayVersion") != "0.1.2") throw new Exception("Upgrade did not register the newer version.");
        Console.WriteLine("PASS real setup retry upgrades every payload with both existing journals and destination pin retained");
        var upgradedRegistration = Registration();
        await Install();
        CheckPreserved(recovery);
        CheckRecoveryAcls();
        if (Registration() != upgradedRegistration) throw new Exception("Same-package reinstall changed Windows Apps values or types.");
        using (var entry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryName))
            if ((string?)entry!.GetValue("DisplayVersion") != "0.1.2") throw new Exception("Upgrade/reinstall did not register the newer version.");
        if (Directory.EnumerateFiles(destination, ".upgrade-*").Any() || Directory.EnumerateDirectories(Programs, "HytaleAccounts-setup-*").Any())
            throw new Exception("Lifecycle left transaction or setup staging files.");
        foreach (var name in payload)
        {
            if (!File.ReadAllBytes(Path.Combine(destination, name)).SequenceEqual(ExpectedPayload(name)))
                throw new Exception("Lifecycle reinstall changed payload bytes: " + name);
        }
        Console.WriteLine("PASS real setup same-version reinstall preserves payload bytes, recovery state, Apps registration and cleans staging");
        foreach (var name in payload)
        {
            var actualAcl = new FileInfo(Path.Combine(destination, name)).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            if (actualAcl != payloadAcls[name])
                throw new Exception("Lifecycle upgrade/reinstall changed payload ACL: " + name + "; expected " + payloadAcls[name] + "; actual " + actualAcl);
        }
        Console.WriteLine("PASS real setup upgrade/reinstall preserves exact payload security descriptors");
    }

    private const string FixtureBoundaries = """
        if ($PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Fixture requires Windows PowerShell 5.1.' }
        Import-Module ScheduledTasks
        function Get-CimInstance { param($ClassName,$Filter) }
        function Get-ScheduledTask { param($TaskName,$ErrorAction) }
        $script:fixtureTaskNames = @()
        function Start-ScheduledTask { param($TaskName) if ($TaskName -notin $script:fixtureTaskNames) { throw 'Cannot start an unregistered fixture task.' } }
        function Register-ScheduledTask {
            param($TaskName,$Action,$Trigger,$Settings,$User,$RunLevel,$Principal,[switch]$Force)
            if (!$Settings.Hidden -or $Action.Execute -cne $installed -or $Action.Arguments -cne $(if ($Principal) { '--repair-integration-user' } else { '--repair-integration' })) { throw 'Visible or incorrect fixture repair task.' }
            if ($Trigger.Repetition.Interval -or $Settings.RestartCount -ne 0 -or $Settings.ExecutionTimeLimit -ne 'PT0S') { throw 'Fixture repair task can relaunch after failure.' }
            $script:fixtureTaskNames += $TaskName
        }
        """;

    private static async Task CheckPreflight()
    {
        Administrator = false;
        preflight = true;
        Programs = Path.Combine(root, "user-programs");
        DesktopDirectory = Path.Combine(root, "user-desktop");
        commonPrograms = Path.Combine(root, "common-programs");
        commonDesktop = Path.Combine(root, "common-desktop");
        LocalApplicationData = Path.Combine(root, "local");
        programFiles = Path.Combine(root, "preflight-program-files");
        OfficialPath = Path.Combine(programFiles, "Hypixel Studios", "Hytale Launcher", "hytale-launcher.exe");
        foreach (var path in new[] { Programs, DesktopDirectory, commonPrograms, commonDesktop, LocalApplicationData, Path.GetDirectoryName(OfficialPath)! }) Directory.CreateDirectory(path);
        File.WriteAllText(OfficialPath, "non-executed official fixture");
        var legacy = Path.Combine(LocalApplicationData, "HytaleAccounts", "NativeLauncher", "HytaleAccounts.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "legacy cleanup must not run");
        var unrelated = Path.Combine(Programs, "Hytale Launcher.lnk");
        foreach (var scenario in new[] { "no shortcuts", "unrelated shortcuts only", "common-only", "user-only", "ordinary installation" })
        {
            // Creation itself stays in a hidden PowerShell child; no launcher or UAC is ever started.
            var links = scenario switch
            {
                "unrelated shortcuts only" => new[] { unrelated },
                "common-only" => new[] { Path.Combine(commonPrograms, "renamed.lnk") },
                "user-only" => new[] { Path.Combine(DesktopDirectory, "renamed.lnk") },
                "ordinary installation" => new[] { Path.Combine(Programs, "launcher.lnk"), Path.Combine(commonDesktop, "launcher.lnk") },
                _ => Array.Empty<string>()
            };
            var reject = scenario is "no shortcuts" or "unrelated shortcuts only";
            if (!reject && File.Exists(legacy)) File.Delete(legacy);
            foreach (var path in links)
            {
                var create = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
                create.ArgumentList.Add("-NoProfile"); create.ArgumentList.Add("-NonInteractive"); create.ArgumentList.Add("-Command");
                static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
                create.ArgumentList.Add("$s = New-Object -ComObject WScript.Shell; $l = $s.CreateShortcut(" + Quote(path) + "); $l.TargetPath = " + Quote(reject ? Executable : OfficialPath) + "; $l.Save()");
                using var child = Process.Start(create)!; await child.WaitForExitAsync();
                if (child.ExitCode != 0) throw new Exception("Shortcut fixture creation failed.");
            }
            try { await Setup.Run(uninstall: false); throw new Exception("Setup passed the intercepted elevation boundary."); }
            catch (IOException ex) when (reject ? ex.Message.Contains("No Hytale launcher shortcut was found. Reinstall the official launcher first.") : ex.Message == "fixture elevation boundary") { }
            if (Directory.EnumerateDirectories(LocalApplicationData, "HytaleAccounts-preflight-*").Any() ||
                Directory.Exists(Path.Combine(Path.GetDirectoryName(OfficialPath)!, ".hytale-accounts-addon")) ||
                (reject && File.ReadAllText(legacy) != "legacy cleanup must not run"))
                throw new Exception("Preflight left state or ran legacy cleanup.");
            using (var entry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryName))
                if (entry != null) throw new Exception("Preflight created an Apps entry.");
            Console.WriteLine("PASS real setup parent " + scenario + (reject ? " rejects before legacy cleanup and UAC, and cleans staging" : " reaches intercepted UAC after read-only preflight and cleans staging"));
            foreach (var path in links) File.Delete(path);
        }
    }

    private const string Installer = """
        param([string]$ProgramsDirectory, [string]$DesktopDirectory)
        $ErrorActionPreference = 'Stop'
        try {
            . (Join-Path $PSScriptRoot 'InstallerSafety.ps1')
            . (Join-Path $PSScriptRoot 'ShortcutIntegration.ps1')
            $blocked = $false
            try { [IO.Directory]::Move($ProgramsDirectory, ($ProgramsDirectory + '-moved')) } catch [IO.IOException] { $blocked = $true }
            if (!$blocked) { throw 'Setup destination directory replacement was allowed.' }
            Write-Host 'PASS setup destination directory replacement remains blocked during child publication'
            $failures = @()
            foreach ($name in @('existing','missing')) {
                $file = $null; $stage = $null
                try {
                    $file = Open-ShortcutFile (Join-Path $ProgramsDirectory ($name + '.bin'))
                    if ($file.Exists -ne ($name -eq 'existing')) { throw 'Unexpected fixture destination state.' }
                    $stage = Open-ShortcutFile (Join-Path $ProgramsDirectory ($name + '.new')) -Create
                    $stage.Write([Text.Encoding]::UTF8.GetBytes('new ' + $name))
                    $file.ReplaceFrom($stage)
                    if ([Text.Encoding]::UTF8.GetString($file.Read()) -cne ('new ' + $name)) { throw 'Published bytes differ.' }
                    Write-Host "PASS setup pin $name publication through ShortcutFile.ReplaceFrom"
                } catch { $failures += "FAIL setup pin $name publication: $_" }
                finally { if ($stage) { $stage.Dispose() }; if ($file) { $file.Dispose() } }
            }
            $state = $ProgramsDirectory
            $roots = @($DesktopDirectory); $repairRoots = $roots; $ownerSid = ''; $script:records = @()
            $journalLock = Open-ShortcutJournal $state
            try {
                foreach ($name in @('existing','missing')) {
                    try {
                        $manifestPath = Join-Path $state ($name + '.json')
                        if ((Test-Path -LiteralPath $manifestPath) -ne ($name -eq 'existing')) { throw 'Unexpected fixture journal state.' }
                        Save-ShortcutJournal
                        $null = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
                        Write-Host "PASS setup pin $name journal publication through File.Move/File.Replace"
                    } catch { $failures += "FAIL setup pin $name journal publication: $_" }
                }
            } finally { $journalLock.Dispose() }
            if ($failures.Count) { throw ($failures -join '; ') }
        } catch {
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'install-error.txt'), $_.ToString())
            throw
        }
        """;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenDirectory(string path, uint access, uint share,
        IntPtr security, uint mode, uint flags, IntPtr template);
}
