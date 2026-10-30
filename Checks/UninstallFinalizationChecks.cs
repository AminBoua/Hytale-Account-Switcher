using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using HytaleAccounts;

internal static class UninstallFinalizationChecks
{
    public static async Task Run(string[] args)
    {
        var root = args[1]; var repository = args[2]; var scenario = args[3];
        var destination = Path.Combine(root, "program-files", "Hypixel Studios", "Hytale Launcher", ".hytale-accounts-addon");
        var phases = new List<string>();
        ProcessStartInfo Start(string mode, ProcessStartInfo? production = null)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(repository, "Checks", "UninstallFinalizationFixture.ps1"), "-Root", root, "-Repository", repository, "-Mode", mode }) start.ArgumentList.Add(arg);
            if (production != null && mode == "Worker")
                foreach (var name in new[] { "-WaitForPid", "-RemovalAttempt" })
                { start.ArgumentList.Add(name); start.ArgumentList.Add(production.ArgumentList[production.ArgumentList.IndexOf(name) + 1]); }
            return start;
        }
        async Task RunScript(string mode)
        {
            using var process = Process.Start(Start(mode))!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException(mode + ": " + await error + await output);
        }
        FileStream? locked = null;
        try
        {
            await Setup.Remove(destination, async start =>
            {
                var mode = start.ArgumentList.Contains("-KeepJournal") ? "Prepare" :
                    start.ArgumentList.Contains(Path.Combine(destination, "Install.ps1")) ? "Recover" : "Finalize";
                phases.Add(mode);
                await RunScript(mode);
            }, start =>
            {
                phases.Add("Start");
                if (scenario == "Cancelled") throw new Win32Exception(1223);
                if (scenario == "StartupFailure") throw new IOException("Synthetic worker startup failure");
                RunScript("Repair").GetAwaiter().GetResult();
                Console.WriteLine("PASS production repair retargets the shortcut during the approval window");
                if (scenario == "Locked") locked = new FileStream(Path.Combine(root, "programs", "Hytale Launcher.lnk"), FileMode.Open, FileAccess.Read, FileShare.Read);
                using var worker = Process.Start(Start("Worker", start))!;
                // Drain output independently; the worker must survive this initiating process's exit.
                _ = worker.StandardOutput.ReadToEndAsync(); _ = worker.StandardError.ReadToEndAsync();
                if (scenario == "NoConfirmation") throw new IOException("Synthetic lost startup response");
            });
            if (scenario != "Success") throw new Exception("Expected removal failure was ignored.");
        }
        catch (Exception ex) when (scenario != "Success" && ex is IOException or Win32Exception)
        {
            if (scenario == "Locked" && !File.ReadAllText(Path.Combine(root, "Finalize.result")).StartsWith("failure|32|"))
                throw new Exception("Finalization did not encounter a real sharing violation.", ex);
            if (scenario == "Locked") Console.WriteLine("PASS final per-user restoration fails with native sharing violation 32");
        }
        finally { locked?.Dispose(); }
        var expected = scenario is "Cancelled" or "StartupFailure" or "NoConfirmation"
            ? new[] { "Prepare", "Start", "Recover" } : new[] { "Prepare", "Start", "Finalize" };
        if (!phases.SequenceEqual(expected)) throw new Exception("Unexpected production phases: " + string.Join(", ", phases));
        Console.WriteLine("PASS production removal orchestration: " + scenario + " (" + string.Join(" -> ", phases) + ")");
    }
}
