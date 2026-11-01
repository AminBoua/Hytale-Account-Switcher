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
    private static string commonPrograms = "", commonDesktop = "", programFiles = "";
    private static readonly string registryName = "Software\\HytaleAccounts-SetupPreflight-" + Guid.NewGuid().ToString("N");
    private static string repository = "", root = "";
    internal static DirectorySecurity Permissions() => new DirectoryInfo(root).GetAccessControl();
    internal static Stream Resource(string name)
    {
        if (name != "Install.ps1") return File.OpenRead(Path.Combine(repository, name));
        if (!preflight) return new MemoryStream(Encoding.UTF8.GetBytes(Installer));
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
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
