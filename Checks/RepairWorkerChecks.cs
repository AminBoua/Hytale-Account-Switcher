using System.IO;
using System.Diagnostics;
using HytaleAccounts;

internal static class RepairWorkerChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "repair fixture ' " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var user in new[] { false, true })
            {
                var start = RepairWorker.StartInfo(root, user);
                check(!start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardError, "repair suppresses console allocation and captures failures");
                check(start.ArgumentList.Contains("-UserOnly") == user && start.ArgumentList.Contains(Path.Combine(root, "Repair-Integration.ps1")), "repair scope and quoted script path preserved");
                check(start.Environment["PSModulePath"] == Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules"), "repair resolves protected modules");
                File.WriteAllText(Path.Combine(root, "Repair-Integration.ps1"), "param([switch]$UserOnly)\nAdd-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class ConsoleProbe { [DllImport(\"kernel32.dll\")] public static extern IntPtr GetConsoleWindow(); }'\nif ([ConsoleProbe]::GetConsoleWindow() -ne [IntPtr]::Zero) { throw 'Console allocated' }\n");
                await RepairWorker.RunProcess(start, TimeSpan.FromSeconds(30));
                check(true, "real PowerShell child has no console window");
            }
            File.WriteAllText(Path.Combine(root, "Repair-Integration.ps1"), "throw 'fixture failure'");
            var calls = 0; var delays = 0;
            try
            {
                await RepairWorker.Repeat(() => { calls++; return RepairWorker.RunProcess(RepairWorker.StartInfo(root, false), TimeSpan.FromSeconds(30)); }, () => { delays++; return Task.CompletedTask; });
                throw new Exception("Failure retried");
            }
            catch (IOException ex) { check(calls == 1 && delays == 0 && ex.Message.Contains("fixture failure"), "failed repair exits once with useful stderr and no retry"); }
            calls = 0; delays = 0;
            try { await RepairWorker.Repeat(() => { if (++calls == 3) throw new IOException(); return Task.CompletedTask; }, () => { delays++; return Task.CompletedTask; }); }
            catch (IOException) { check(calls == 3 && delays == 2, "successful repairs recur until first failure"); }
            var pidFile = Path.Combine(root, "child.pid");
            File.WriteAllText(Path.Combine(root, "Repair-Integration.ps1"), """
                $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'powershell.exe'))
                $start.UseShellExecute = $false; $start.CreateNoWindow = $true
                $start.Arguments = '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 60'
                $child = [Diagnostics.Process]::Start($start)
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'child.pid'), [string]$child.Id)
                Start-Sleep -Seconds 60
                """);
            try { await RepairWorker.RunProcess(RepairWorker.StartInfo(root, false), TimeSpan.FromSeconds(3)); throw new Exception("Timeout ignored"); }
            catch (OperationCanceledException)
            {
                check(File.Exists(pidFile), "timeout fixture starts a descendant process");
                var childId = int.Parse(File.ReadAllText(pidFile));
                try
                {
                    using var child = Process.GetProcessById(childId);
                    var stopped = child.WaitForExit(5000);
                    if (!stopped) child.Kill();
                    check(stopped, "timeout terminates the repair descendant too");
                }
                catch (ArgumentException) { check(true, "timeout terminates the repair descendant too"); }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
