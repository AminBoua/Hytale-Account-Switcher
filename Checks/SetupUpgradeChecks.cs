using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace HytaleAccounts;

public static class SetupUpgradeChecks
{
    internal static string Executable = "", Programs = "", OfficialPath = "";
    private static string repository = "", root = "";
    internal static DirectorySecurity Permissions() => new DirectoryInfo(root).GetAccessControl();
    internal static Stream Resource(string name) => name == "Install.ps1"
        ? new MemoryStream(Encoding.UTF8.GetBytes(Installer)) : File.OpenRead(Path.Combine(repository, name));

    public static async Task Run(string source)
    {
        repository = source;
        root = SecurityFixture.Create("setup-upgrade-");
        try
        {
            Executable = Path.Combine(root, "download.exe");
            File.WriteAllText(Executable, "non-executed installer fixture");
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
        }
        finally
        {
            var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).TrimEnd('\\') + '\\';
            if (!Path.GetFullPath(root).StartsWith(local, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("setup-upgrade-"))
                throw new Exception("Cleanup escaped setup fixture.");
            Directory.Delete(root, recursive: true);
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
