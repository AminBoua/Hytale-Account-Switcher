using System.Diagnostics;
using HytaleAccounts;

internal static class PreparationFailureChecks
{
    internal static async Task Run(LocalAccountBridge bridge, Action<bool, string> check)
    {
        var root = SecurityFixture.Create("preparation-failure-");
        var warnings = new StringWriter();
        using var listener = new TextWriterTraceListener(warnings);
        Trace.Listeners.Add(listener);
        FileStream? locked = null;
        try
        {
            var stores = new NativeStores(root);
            var store = new NativeStore("saved", "Saved", Guid.NewGuid());
            stores.Catalog.Stores.Add(store); stores.Select(store);
            var folder = stores.Folder(store.Folder);
            var snapshot = Path.Combine(folder, "AppData", "Hytale");
            Directory.CreateDirectory(Path.Combine(snapshot, ".keys"));
            Directory.CreateDirectory(Path.Combine(root, ".keys"));
            var oldRun = Path.Combine(folder, "run-existing");
            Directory.CreateDirectory(oldRun);
            File.WriteAllText(Path.Combine(snapshot, "account.dat"), "opaque saved session");
            File.WriteAllText(Path.Combine(snapshot, ".keys", "key"), "opaque saved key");
            File.WriteAllText(Path.Combine(root, "account.dat"), "opaque active session");
            // Conflicting native keys stop real activation after the runtime image has been written.
            File.WriteAllText(Path.Combine(root, ".keys", "key"), "different active key");
            File.WriteAllText(Path.Combine(folder, "unknown.bin"), "unrelated account file");
            File.WriteAllText(Path.Combine(oldRun, "hytale-launcher.exe"), "pre-existing runtime");
            File.WriteAllText(Path.Combine(oldRun, "unknown.bin"), "unrelated runtime file");
            var preserved = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes);
            var retained = new List<string>();
            foreach (var mode in new[] { "empty", "empty", "empty", "nonempty", "locked" })
            {
                stores = new NativeStores(root); // Failed preparations must also stay clean across startups.
                string? generated = null;
                IntegrationSafety.Preparing = path =>
                {
                    generated = path;
                    check(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(IntegrationSafety.OfficialPath)),
                        "activation failure is injected after the generated executable exists: " + mode);
                    if (mode == "nonempty")
                    {
                        var unknown = Path.Combine(Path.GetDirectoryName(path)!, "unknown.bin");
                        File.WriteAllText(unknown, "unrelated new runtime file");
                        preserved.Add(unknown, File.ReadAllBytes(unknown));
                        retained.Add(Path.GetDirectoryName(path)!);
                    }
                    if (mode == "locked") locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                };
                try
                {
                    await stores.PrepareIntegrated(store, bridge);
                    throw new Exception("Blocked activation succeeded.");
                }
                catch (IOException ex)
                {
                    check(ex.Message == "Native key conflict; files preserved." && ex.StackTrace!.Contains("ActivateCore"),
                        "preparation preserves the original activation failure: " + mode);
                }
                check(generated is not null, "failed preparation reached executable creation: " + mode);
                var run = Path.GetDirectoryName(generated!)!;
                if (mode == "locked")
                {
                    listener.Flush();
                    check(File.Exists(generated) && warnings.ToString().Contains("Native launcher preparation cleanup failed;") &&
                        warnings.ToString().Contains(generated!), "blocked cleanup is reported and respects the conflicting file handle");
                    locked!.Dispose(); locked = null;
                    File.Delete(generated!); Directory.Delete(run);
                }
                else
                    check(!File.Exists(generated) && Directory.Exists(run) == (mode == "nonempty"),
                        "failed preparation removes only its executable and an empty runtime directory: " + mode);
                check(Directory.GetDirectories(folder, "run-*").Order().SequenceEqual(retained.Append(oldRun).Order()) &&
                    Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories).SequenceEqual(new[] { Path.Combine(oldRun, "hytale-launcher.exe") }),
                    "repeated failed preparations do not accumulate runtime executables or empty directories: " + mode);
                check(preserved.All(file => File.ReadAllBytes(file.Key).SequenceEqual(file.Value)),
                    "snapshots, keys, metadata, unrelated files and pre-existing runtimes remain byte-identical: " + mode);
            }
        }
        finally
        {
            IntegrationSafety.Preparing = null;
            locked?.Dispose();
            Trace.Listeners.Remove(listener);
            Directory.Delete(root, true);
        }
    }
}
