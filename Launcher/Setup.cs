using System.Diagnostics;
using System.Reflection;

namespace HytaleAccounts;

public static class Setup
{
    public static async Task Run(bool uninstall)
    {
        var destination = Path.Combine(Path.GetDirectoryName(IntegrationSafety.OfficialPath)!, ".hytale-accounts-addon");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        // Only installation/removal is elevated; the launcher is started by this normal user process.
        var start = new ProcessStartInfo(powershell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" }) start.ArgumentList.Add(arg);
        if (uninstall)
        {
            var script = Path.Combine(destination, "Uninstall.ps1");
            NativeStores.RegularPath(script);
            if (!File.Exists(script)) throw new IOException("The addon is not installed.");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-WaitForPid");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add("-Interactive");
            // The separate hidden process waits for this executable to exit before removing it.
            using var removal = Process.Start(start) ?? throw new IOException("Could not start uninstall.");
            return;
        }
        if (!await IntegrationSafety.VerifyExecutable(IntegrationSafety.OfficialPath))
            throw new IOException(IntegrationSafety.CompatibilityMessage(IntegrationSafety.OfficialPath));
        // Remove the previous per-user package without elevating user-writable legacy code.
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HytaleAccounts", "NativeLauncher", "HytaleAccounts.exe");
        NativeStores.RegularPath(legacy);
        if (File.Exists(legacy))
        {
            var cleanup = new ProcessStartInfo(legacy) { UseShellExecute = false, CreateNoWindow = true };
            cleanup.ArgumentList.Add("--uninstall");
            using var old = Process.Start(cleanup) ?? throw new IOException("Could not remove the previous addon package.");
            await old.WaitForExitAsync();
            for (var attempt = 0; File.Exists(legacy) && attempt < 150; attempt++) await Task.Delay(200);
            if (File.Exists(legacy)) throw new IOException("The previous addon could not be removed. Close Hytale and try again.");
        }
        var package = Path.Combine(Path.GetTempPath(), "HytaleAccounts-setup-" + Guid.NewGuid().ToString("N"));
        NativeStores.RegularPath(package);
        Directory.CreateDirectory(package);
        try
        {
            File.Copy(Environment.ProcessPath!, Path.Combine(package, "HytaleAccounts.exe"));
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in new[] { "Install.ps1", "Uninstall.ps1", "InstallerSafety.ps1" })
            {
                using var resource = assembly.GetManifestResourceStream("HytaleAccounts." + name) ?? throw new IOException("Setup resource is missing: " + name);
                using var file = File.Create(Path.Combine(package, name));
                await resource.CopyToAsync(file);
            }
            start.ArgumentList.Add(Path.Combine(package, "Install.ps1"));
            // Capture the invoking user's shortcut locations before Windows elevation.
            foreach (var (option, folder) in new[] {
                ("-ProgramsDirectory", Environment.SpecialFolder.Programs),
                ("-DesktopDirectory", Environment.SpecialFolder.DesktopDirectory) })
            {
                start.ArgumentList.Add(option);
                start.ArgumentList.Add(Environment.GetFolderPath(folder));
            }
            using var install = Process.Start(start) ?? throw new IOException("Could not start setup.");
            await install.WaitForExitAsync();
            if (install.ExitCode != 0) throw new IOException("Installation failed. Close Hytale and check that your administrator account can update its installation and shortcuts, then try again.");
        }
        finally
        {
            // Exact files created above; never recurse through somebody else's data.
            foreach (var name in new[] { "HytaleAccounts.exe", "Install.ps1", "Uninstall.ps1", "InstallerSafety.ps1" })
            {
                var path = Path.Combine(package, name);
                NativeStores.RegularPath(path);
                File.Delete(path);
            }
            Directory.Delete(package);
        }
        using var launcher = Process.Start(new ProcessStartInfo(Path.Combine(destination, "HytaleAccounts.exe")) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new IOException("Installed successfully, but the launcher could not open. Use your usual Hytale Launcher shortcut.");
    }
}
