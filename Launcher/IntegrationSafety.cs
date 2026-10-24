using System.Diagnostics;
using System.Text;

namespace HytaleAccounts;

public static class IntegrationSafety
{
    public const string SupportedVersion = "2026.09.30-326a0c2";
    public const string SupportedHash = "e34e33bf28271cb2647c37624591e6e588ea5d55619deadc18f8e67491f2fafe";
    public static string OfficialPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hypixel Studios", "Hytale Launcher", "hytale-launcher.exe");
    public static bool Compatible(string version, string hash) => version == SupportedVersion && string.Equals(hash, SupportedHash, StringComparison.OrdinalIgnoreCase);
    public static string CompatibilityMessage(string path)
    {
        if (!File.Exists(path)) return "The official Hytale launcher is missing. Account patches are disabled. Reinstall the official launcher, then install a patcher release that supports its build.";
        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
        return $"Launcher build { (string.IsNullOrWhiteSpace(version) ? "unknown" : version) } does not match this patcher (supported build: {SupportedVersion}). " +
            "Account patches are disabled; launcher files were preserved. Use the official launcher and install a patcher release that supports your current build. " +
            "Rerunning this release cannot enable an unsupported build. If the executable was modified or its signature cannot be verified, reinstall the official launcher first. Never restore an older backup over an updated launcher.";
    }
    public static async Task<bool> VerifyExecutable(string path)
    {
        if (!File.Exists(path)) throw new IOException("The official Hytale launcher is not installed in its standard location.");
        NativeStores.RegularPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await VerifyPublisher(path);
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        _ = PatchInstaller.PatchCurrent(buffer.ToArray(), 49152, new string('a', 43));
        return true;
    }
    public static async Task VerifyPublisher(string path)
    {
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        var script = "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedPath + "')); $s=Get-AuthenticodeSignature -LiteralPath $p; if ($s.Status -ne 'Valid' -or $s.SignerCertificate.GetNameInfo(0,$false) -ne 'Hypixel Studios Canada Inc') {exit 1}; exit 0";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment.Remove("PSModulePath");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        using var check = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await check.WaitForExitAsync(timeout.Token); }
        catch { if (!check.HasExited) check.Kill(); throw; }
        if (check.ExitCode != 0) throw new InvalidOperationException("The official launcher's publisher signature could not be verified. Account patches are disabled and files were preserved. Reinstall the official launcher from its official installer before installing a compatible patcher. Do not restore an older backup over an updated launcher.");
    }
    public static ProcessStartInfo StartInfo(string path)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)!, WindowStyle = ProcessWindowStyle.Normal };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)).ToList()) start.Environment.Remove(key);
        return start;
    }
}
