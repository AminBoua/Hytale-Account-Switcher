using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// Harmless production-shaped apphost: persistent native sidecars; WPF creates no window.
internal static class Program
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int size, out int returned);
    [STAThread]
    private static int Main(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken, 18, out var type, 4, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        string[] Modules() => Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.FileName).ToArray();
        var libraries = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var cache = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")!;
        var before = Directory.Exists(cache) ? Directory.GetFiles(cache, "*.dll", SearchOption.AllDirectories) : [];
        var application = new System.Windows.Application();
        var files = Directory.GetFiles(libraries, "*_cor3.dll").Select(path => new {
            Path = path, Sddl = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access),
            Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        }).ToArray();
        var result = new {
            Contract = "runtime-sidecars-v2", RunId = args[1], Mode = args[2], SourceFingerprint = args[3],
            Utc = DateTime.UtcNow.ToString("o"),
            Identity = new { Sid = identity.User!.Value, Administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), ElevationType = type, Pid = Environment.ProcessId },
            Cache = new { Path = cache, Sddl = Directory.Exists(cache) ? new DirectoryInfo(cache).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) : "" },
            LibraryRoot = new { Path = libraries, Sddl = new DirectoryInfo(libraries).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) },
            Files = files, BeforeApplication = before,
            Directories = Array.Empty<object>(),
            Modules = Modules().Where(p => files.Any(f => Path.GetFileName(f.Path).Equals(Path.GetFileName(p), StringComparison.OrdinalIgnoreCase))).ToArray(),
            Profile = new[] {"APPDATA","LOCALAPPDATA","USERPROFILE","TEMP","TMP"}.ToDictionary(n => n, Environment.GetEnvironmentVariable)
        };
        using (var output = new FileStream(args[0] + ".pending", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(output, result);
        File.Move(args[0] + ".pending", args[0]);
        var watch = Stopwatch.StartNew();
        while (!File.Exists(args[4]) && watch.Elapsed < TimeSpan.FromMinutes(4)) Thread.Sleep(50);
        application.Shutdown();
        return File.Exists(args[4]) ? 23 : 124;
    }
}
