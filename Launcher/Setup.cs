using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;

namespace HytaleAccounts;

public static class Setup
{
    internal const string ElevatedMode = "--install-elevated";
    private static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    internal static ProcessStartInfo ElevationStart(string executable, string programs, string desktop)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory };
        foreach (var arg in new[] { ElevatedMode, programs, desktop, WindowsIdentity.GetCurrent().User!.Value }) start.ArgumentList.Add(arg);
        return start;
    }

    internal static ProcessStartInfo InstallStart(string package, string programs, string desktop, string? userSid = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = package };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(package, "Install.ps1"), "-ProgramsDirectory", programs, "-DesktopDirectory", desktop }) start.ArgumentList.Add(arg);
        if (userSid != null) { start.ArgumentList.Add("-UserSid"); start.ArgumentList.Add(userSid); }
        // Elevated PowerShell must not auto-load modules or resolve system paths from the caller's environment.
        start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
        start.Environment["ProgramFiles"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return start;
    }

    internal static ProcessStartInfo LauncherStart(string destination) =>
        new(Path.Combine(destination, "HytaleAccounts.exe")) { UseShellExecute = false, CreateNoWindow = true };

    internal static async Task RunElevated(string[] args)
    {
        if (args.Length != 4 || args[0] != ElevatedMode || !IsAdministrator)
            throw new IOException("The installer worker requires administrator elevation and two shortcut locations and a user SID.");
        foreach (var path in args.Skip(1).Take(2))
        {
            if (!Path.IsPathFullyQualified(path)) throw new IOException("Invalid shortcut location.");
            NativeStores.RegularPath(path);
        }
        if (!new SecurityIdentifier(args[3]).IsAccountSid()) throw new IOException("Invalid shortcut user SID.");
        var executable = Environment.ProcessPath!;
        using var sourceDirectories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(executable)!, requireTrusted: false);
        using var source = SetupPayload.OpenSource(executable);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        using var parent = new SetupPayload.PinnedDirectories(programs, requireTrusted: true);
        var launcherDirectory = Path.GetDirectoryName(IntegrationSafety.OfficialPath)!;
        using var installation = new SetupPayload.PinnedDirectories(launcherDirectory, requireTrusted: true);
        var destination = Path.Combine(launcherDirectory, ".hytale-accounts-addon");
        NativeStores.RegularPath(destination);
        using var existing = Directory.Exists(destination) ? new SetupPayload.PinnedDirectories(destination, requireTrusted: true, forMutation: true) : null;
        if (existing != null)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(destination))
            {
                using var file = SetupPayload.OpenSource(path);
                SetupPayload.RequireTrusted(file.GetAccessControl());
            }
        }
        using var package = SetupPayload.Extract(Path.Combine(programs, "HytaleAccounts-setup-" + Guid.NewGuid().ToString("N")), source,
            name => Assembly.GetExecutingAssembly().GetManifestResourceStream("HytaleAccounts." + name) ?? throw new IOException("Setup resource is missing: " + name),
            SetupPayload.Permissions());
        using var install = Process.Start(InstallStart(package.DirectoryPath, args[1], args[2], args[3])) ?? throw new IOException("Could not start setup.");
        await install.WaitForExitAsync();
        if (install.ExitCode != 0)
        {
            var error = Path.Combine(package.DirectoryPath, "install-error.txt");
            NativeStores.RegularPath(error);
            throw new IOException("Installation failed. " + (File.Exists(error) ? File.ReadAllText(error) : $"Installer exited with code {install.ExitCode}."));
        }
    }

    internal static ProcessStartInfo UserIntegrationStart(string destination, bool uninstall)
    {
        var start = InstallStart(destination, Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (uninstall) start.ArgumentList[start.ArgumentList.IndexOf(Path.Combine(destination, "Install.ps1"))] = Path.Combine(destination, "Uninstall.ps1");
        start.ArgumentList.Add("-UserOnly");
        start.RedirectStandardError = true;
        return start;
    }

    private static async Task IntegrateUser(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("Could not update your shortcuts.");
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("Could not update your shortcuts. " + await error);
    }

    internal static async Task Remove(string destination, Func<ProcessStartInfo, Task> integrateUser, Action<ProcessStartInfo> startRemoval)
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" }) start.ArgumentList.Add(arg);
        var script = Path.Combine(destination, "Uninstall.ps1");
        NativeStores.RegularPath(script);
        if (!File.Exists(script)) throw new IOException("The addon is not installed.");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-UserSid");
        start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        start.ArgumentList.Add("-WaitForPid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add("-Interactive");
        // Older installations have only the machine journal; their guarded removal handles migration.
        var userIntegration = File.Exists(Path.Combine(destination, "ShortcutIntegration.ps1"));
        try
        {
            if (userIntegration)
            {
                var prepare = UserIntegrationStart(destination, uninstall: true);
                prepare.ArgumentList.Add("-KeepJournal");
                await integrateUser(prepare);
            }
            // The separate hidden process waits for this executable to exit before removing it.
            startRemoval(start);
        }
        catch (Exception failure)
        {
            if (userIntegration)
            {
                try { await integrateUser(UserIntegrationStart(destination, uninstall: false)); }
                catch (Exception recovery) { throw new AggregateException("Uninstall did not start and shortcut recovery failed. The restoration journal was retained for repair.", failure, recovery); }
            }
            throw;
        }
        // Repair can run while UAC is open; restore again under the journal lock before deleting state.
        if (userIntegration) await integrateUser(UserIntegrationStart(destination, uninstall: true));
    }

    public static async Task<int> Run(bool uninstall)
    {
        var destination = Path.Combine(Path.GetDirectoryName(IntegrationSafety.OfficialPath)!, ".hytale-accounts-addon");
        if (uninstall)
        {
            if (IsAdministrator) throw new IOException("Remove the addon normally from Windows Settings so your shortcuts are restored as your Windows user.");
            await Remove(destination, IntegrateUser, start =>
            {
                using var removal = Process.Start(start) ?? throw new IOException("Could not start uninstall.");
            });
            return 0;
        }
        if (IsAdministrator) throw new IOException("Open HytaleAccounts-Setup.exe normally so Hytale starts as your Windows user.");
        // Pin the approved image and its ancestors across UAC and the worker's copy; no loose payload crosses elevation.
        using var sourceDirectories = new SetupPayload.PinnedDirectories(Path.GetDirectoryName(Environment.ProcessPath!)!, requireTrusted: false);
        using var source = SetupPayload.OpenSource(Environment.ProcessPath!);
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
        using var worker = Process.Start(ElevationStart(Environment.ProcessPath!,
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)))
            ?? throw new IOException("Could not start setup.");
        await worker.WaitForExitAsync();
        if (worker.ExitCode != 0) return worker.ExitCode; // The worker already displayed the setup error.
        await IntegrateUser(UserIntegrationStart(destination, uninstall: false));
        using var launcher = Process.Start(LauncherStart(destination))
            ?? throw new IOException("Installed successfully, but the launcher could not open. Use your usual Hytale Launcher shortcut.");
        return 0;
    }
}
