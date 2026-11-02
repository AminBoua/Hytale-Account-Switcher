using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HytaleAccounts;

internal static class RepairWorker
{
    internal static ProcessStartInfo StartInfo(string destination, bool userOnly)
    {
        var start = Setup.InstallStart(destination, "", "");
        start.ArgumentList.Clear();
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(destination, "Repair-Integration.ps1") }) start.ArgumentList.Add(arg);
        if (userOnly) start.ArgumentList.Add("-UserOnly");
        start.RedirectStandardError = true;
        return start;
    }

    internal static async Task RunProcess(ProcessStartInfo start, TimeSpan timeout)
    {
        using var process = Process.Start(start) ?? throw new IOException("Could not start shortcut repair.");
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        var details = await error;
        if (process.ExitCode != 0) throw new IOException($"Shortcut repair exited with code {process.ExitCode}: {details}");
    }

    internal static async Task Repeat(Func<Task> repair, Func<Task> delay)
    {
        while (true) { await repair(); await delay(); }
    }

    internal static async Task<int> Run(bool userOnly)
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            if (userOnly ? new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) : !identity.IsSystem)
                throw new IOException("Shortcut repair token does not match its scope.");
            var destination = Path.Combine(Path.GetDirectoryName(IntegrationSafety.OfficialPath)!, ".hytale-accounts-addon");
            if (!string.Equals(Environment.ProcessPath, Path.Combine(destination, "HytaleAccounts.exe"), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Shortcut repair must run from the installed payload.");
            await Repeat(async () =>
            {
                using var directories = new SetupPayload.PinnedDirectories(destination, requireTrusted: true, forMutation: true);
                var files = new List<FileStream>();
                try
                {
                    foreach (var name in new[] { "HytaleAccounts.exe", "Repair-Integration.ps1", "InstallerSafety.ps1", "ShortcutIntegration.ps1", "ShortcutFile.cs" })
                    {
                        var file = SetupPayload.OpenSource(Path.Combine(destination, name));
                        files.Add(file);
                        SetupPayload.RequireTrusted(file.GetAccessControl());
                    }
                    await RunProcess(StartInfo(destination, userOnly), TimeSpan.FromSeconds(50));
                }
                finally { foreach (var file in files) file.Dispose(); }
            }, () => Task.Delay(TimeSpan.FromMinutes(1)));
            return 0;
        }
        catch (Exception ex)
        {
            // A failed worker exits; only a new login/startup or explicit task start retries it.
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HytaleAccounts");
                PrivateFiles.CreateDirectory(root);
                using var file = PrivateFiles.CreateFile(Path.Combine(root, "repair-error-" + Guid.NewGuid().ToString("N") + ".txt"));
                using var writer = new StreamWriter(file);
                writer.WriteLine($"{DateTimeOffset.Now:O} Shortcut repair stopped (user scope: {userOnly}). Restart the repair task or reinstall after correcting the failure.\n{ex}");
            }
            catch { /* The nonzero task result still reports failure if diagnostics cannot be written. */ }
            return 1;
        }
    }
}
