using System.Diagnostics;
using System.Text.Json;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

// A harmless single-file apphost probe: no installer, Hytale, account data, registry or elevation.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR")!;
        var extractedBeforeApplication = Directory.Exists(root) ? Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories) : [];
        var ownerChecks = new List<string>();
        foreach (var file in extractedBeforeApplication)
        {
            var acl = new FileInfo(file).GetAccessControl();
            if (!acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Any(rule => rule.IdentityReference.Value == "S-1-3-4" && rule.IsInherited &&
                    rule.FileSystemRights == (FileSystemRights.ReadPermissions | FileSystemRights.Synchronize)))
                continue; // The parent asserts complete inheritance after the cache-poison assertion.
            EffectiveAccessChecks.RuntimeOwner(acl.GetSecurityDescriptorBinaryForm(), (ok, message) =>
            {
                if (!ok) throw new Exception(message); ownerChecks.Add(message);
            });
        }
        var application = new System.Windows.Application(); // Initialize WPF without opening a window.
        var environment = new[] { "DOTNET_BUNDLE_EXTRACT_BASE_DIR", "APPDATA", "LOCALAPPDATA", "USERPROFILE", "TEMP", "TMP" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(module => module.FileName.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(module => module.FileName).ToArray();
        var wpfModules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(module => Path.GetFileName(module.FileName).EndsWith("_cor3.dll", StringComparison.OrdinalIgnoreCase))
            .Select(module => module.FileName).ToArray();
        File.WriteAllText(args[0], JsonSerializer.Serialize(new { environment, modules, wpfModules, extractedBeforeApplication, ownerChecks, arguments = args.Skip(1).ToArray() }));
        application.Shutdown();
        return 23;
    }
}
