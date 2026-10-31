using System.Diagnostics;
using System.IO;
using System.Text;
using HytaleAccounts;

public static class PowerShellModuleChecks
{
    private static string writableDefaultModules = "";
    private static string? publisherModules;

    // The disposable build substitutes only Process.Start; the production configuration runs unchanged.
    internal static Process StartPublisher(ProcessStartInfo start)
    {
        start.Environment.TryGetValue("PSModulePath", out publisherModules);
        // Model writable default discovery without writing into the real user's module directories.
        if (string.IsNullOrEmpty(publisherModules)) start.Environment["PSModulePath"] = writableDefaultModules;
        return Process.Start(start)!;
    }

    public static async Task Run(Action<bool, string> check)
    {
        var root = SecurityFixture.Create("powershell-modules ' check-");
        var modules = Path.Combine(root, "modules");
        var programFiles = Path.Combine(root, "program-files");
        var defaultModules = Path.Combine(programFiles, "WindowsPowerShell", "Modules");
        writableDefaultModules = defaultModules;
        var marker = Path.Combine(root, "substitute-ran");
        var reached = Path.Combine(root, "uninstall-reached");
        var unsigned = Path.Combine(root, "unsigned.exe");
        var systemModules = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
        var programModules = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsPowerShell", "Modules");
        var oldModules = Environment.GetEnvironmentVariable("PSModulePath");
        var oldProgramFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        void Substitute(string location, string name, string command, string body)
        {
            var directory = Path.Combine(location, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".psd1"),
                "@{RootModule='" + name + ".psm1';ModuleVersion='99.0.0';FunctionsToExport=@('" + command + "');CmdletsToExport=@();AliasesToExport=@()}");
            File.WriteAllText(Path.Combine(directory, name + ".psm1"),
                "[IO.File]::AppendAllText(" + Quote(marker) + ", '" + name + " loaded;')\n" + body);
        }
        ProcessStartInfo Command(string script)
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            return start;
        }
        async Task<int> Run(ProcessStartInfo start)
        {
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { if (!child.HasExited) child.Kill(); throw; }
            if (child.ExitCode != 0) Console.WriteLine(await output + await error);
            return child.ExitCode;
        }
        try
        {
            File.WriteAllText(unsigned, "unsigned synthetic executable");
            Substitute(modules, "Microsoft.PowerShell.Management", "Join-Path",
                "function Join-Path { throw 'Substitute Join-Path executed' }\nExport-ModuleMember -Function Join-Path");
            const string falseSignature = """
                class SubstituteCertificate {
                    [string] GetNameInfo([int]$type, [bool]$issuer) { return 'Hypixel Studios Canada Inc' }
                }
                function Get-AuthenticodeSignature {
                    param($LiteralPath)
                    [pscustomobject]@{ Status='Valid'; SignerCertificate=[SubstituteCertificate]::new() }
                }
                Export-ModuleMember -Function Get-AuthenticodeSignature
                """;
            foreach (var location in new[] { modules, defaultModules })
                Substitute(location, "Microsoft.PowerShell.Security", "Get-AuthenticodeSignature", falseSignature);
            Environment.SetEnvironmentVariable("PSModulePath", modules);
            Environment.SetEnvironmentVariable("ProgramFiles", programFiles);

            check(await Run(Command("try { $ErrorActionPreference='Stop'; Join-Path 'a' 'b' } catch { exit 0 }; exit 1")) == 0 && File.Exists(marker),
                "control: NoProfile still executes an inherited substitute module");
            File.Delete(marker);
            var signature = "$s=Get-AuthenticodeSignature -LiteralPath " + Quote(unsigned) +
                "; if ($s.Status -ne 'Valid' -or $s.SignerCertificate.GetNameInfo(0,$false) -ne 'Hypixel Studios Canada Inc') {exit 1}; exit 0";
            var discovery = Command(signature);
            discovery.Environment["PSModulePath"] = defaultModules;
            check(await Run(discovery) == 0 && File.Exists(marker),
                "control: writable default-discovery fixture supplies a false publisher result");
            File.Delete(marker);

            var probe = "$ErrorActionPreference='Stop'; $null=Join-Path 'a' 'b'; " +
                "foreach ($modulePath in $env:PSModulePath.Split(';')) { if ($modulePath -notin @(" + Quote(systemModules) + "," + Quote(programModules) + ")) {throw 'Unexpected module path'} }; " +
                "$s=Get-AuthenticodeSignature -LiteralPath " + Quote(unsigned) + "; if ($s.Status -eq 'Valid') {throw 'False signature accepted'}; ";
            File.WriteAllText(Path.Combine(root, "Install.ps1"),
                "param([string]$ProgramsDirectory,[string]$DesktopDirectory,[switch]$UserOnly)\n" + probe);
            check(await Run(Setup.InstallStart(root, root, root)) == 0 && !File.Exists(marker),
                "production InstallStart rejects inherited substitute modules");
            check(await Run(Setup.UserIntegrationStart(root, uninstall: false)) == 0 && !File.Exists(marker),
                "production per-user installation uses only system modules");

            using (var resource = typeof(Setup).Assembly.GetManifestResourceStream("HytaleAccounts.Uninstall.ps1")!)
            using (var file = File.Create(Path.Combine(root, "Uninstall.ps1"))) resource.CopyTo(file);
            // Stop at the first dot-source, before any real removal, task, registry or process operations.
            File.WriteAllText(Path.Combine(root, "InstallerSafety.ps1"),
                probe + "[IO.File]::WriteAllText(" + Quote(reached) + ", 'reached'); [Environment]::Exit(0)");
            ProcessStartInfo? removal = null;
            var captured = new IOException("Captured removal; never request UAC in a fixture.");
            try { await Setup.Remove(root, _ => Task.CompletedTask, start => { removal = start; throw captured; }); }
            catch (IOException ex) when (ReferenceEquals(ex, captured)) { }
            check(removal is { UseShellExecute: true, Verb: "runas", WindowStyle: ProcessWindowStyle.Hidden } &&
                removal.ArgumentList.Contains(Path.Combine(root, "Uninstall.ps1")),
                "production removal uses the embedded uninstall entry point and hidden shell elevation");
            // Execute the captured production arguments without elevation; only the safety dependency is a stub.
            removal!.UseShellExecute = false;
            removal.Verb = "";
            removal.CreateNoWindow = true;
            removal.ArgumentList.Remove("-Interactive");
            check(await Run(removal) == 0 && File.Exists(reached) && !File.Exists(marker),
                "production uninstall restricts discovery before its first cmdlet without a custom child environment");
            File.Delete(reached);
            check(await Run(Setup.UserIntegrationStart(root, uninstall: true)) == 0 && File.Exists(reached) && !File.Exists(marker),
                "production per-user uninstall uses only system modules");

            var rejected = false;
            try { await IntegrationSafety.VerifyPublisher(unsigned); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("publisher signature could not be verified")) { rejected = true; }
            check(publisherModules == systemModules && rejected && !File.Exists(marker),
                "production VerifyPublisher rejects unsigned bytes and never executes substitute signature modules");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PSModulePath", oldModules);
            Environment.SetEnvironmentVariable("ProgramFiles", oldProgramFiles);
            var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != local || !Path.GetFileName(root).StartsWith("powershell-modules ' check-"))
                throw new IOException("Cleanup escaped module fixture.");
            Directory.Delete(root, recursive: true);
        }
    }
}
