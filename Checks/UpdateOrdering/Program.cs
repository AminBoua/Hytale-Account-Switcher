using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using HytaleAccounts;

if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
    throw new Exception("Update ordering checks must run unelevated.");
void Check(bool ok, string message)
{
    if (!ok) throw new Exception("FAIL " + message);
    Console.WriteLine("PASS " + message);
}
var root = SecurityFixture.Create("update-ordering-");
try
{
    // Copy our inert assembly's version resource; no official executable or account data is used.
    string Build(string name, string version)
    {
        var bytes = File.ReadAllBytes(typeof(IntegrationSafety).Assembly.Location);
        var original = Encoding.Unicode.GetBytes("2026.10.01-aaaaaaa");
        var replacement = Encoding.Unicode.GetBytes(version);
        Check(original.Length == replacement.Length, "fixture version has a fixed resource size");
        var found = false;
        for (var at = 0; at <= bytes.Length - original.Length; at++)
            if (bytes.AsSpan(at).StartsWith(original)) { replacement.CopyTo(bytes, at); found = true; }
        Check(found, "fixture contains a product version resource");
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        Check(FileVersionInfo.GetVersionInfo(path).ProductVersion == version, "fixture exposes product version " + version);
        return path;
    }
    var a = Build("a.exe", "2026.10.01-aaaaaaa");
    var b = Build("b.exe", "2026.10.01-bbbbbbb");
    var later = Build("later.exe", "2026.10.02-ccccccc");
    var duplicate = Build("duplicate.exe", "2026.10.01-aaaaaaa");
    Check(!NativeStores.NewerBuild(a, b), "same-day A cannot override official B");
    Check(!NativeStores.NewerBuild(b, a), "same-day B cannot override official A");
    Check(!NativeStores.NewerBuild(a, duplicate), "identical builds are not newer");
    Check(NativeStores.NewerBuild(later, a), "later date is newer");
    Check(!NativeStores.NewerBuild(a, later), "earlier date is not newer");

    var stores = new NativeStores(root);
    var store = stores.CreatePending();
    var cached = Path.Combine(stores.Root, "launcher-update.exe");
    await using var bridge = new LocalAccountBridge(_ => Task.FromResult(true));
    foreach (var pair in new[] { (Official: b, Cache: a), (Official: a, Cache: b),
        (Official: a, Cache: duplicate), (Official: later, Cache: a), (Official: a, Cache: later) })
    {
        IntegrationSafety.OfficialPath = pair.Official;
        File.Copy(pair.Cache, cached, true);
        var expected = pair.Cache == later ? cached : pair.Official;
        IntegrationSafety.Verified.Clear();
        var prepared = await stores.PrepareIntegrated(store, bridge);
        Check(IntegrationSafety.Verified.SequenceEqual(new[] { expected }) &&
            File.ReadAllBytes(prepared.Start.FileName).SequenceEqual(File.ReadAllBytes(expected)),
            $"PrepareIntegrated selects and verifies {Path.GetFileName(expected)} over {Path.GetFileName(pair.Cache)}");
    }
    foreach (var pair in new[] { (Previous: a, Next: b, Accept: true), (Previous: b, Next: a, Accept: true),
        (Previous: a, Next: duplicate, Accept: false), (Previous: a, Next: later, Accept: true),
        (Previous: later, Next: a, Accept: false) })
    {
        File.Copy(pair.Previous, cached, true);
        var updateBase = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pair.Previous))).ToLowerInvariant();
        stores.SaveLauncherUpdate(File.ReadAllBytes(pair.Previous), updateBase);
        IntegrationSafety.Verified.Clear();
        var accepted = await stores.PreserveLauncherUpdate(pair.Next, new string('0', 64), updateBase);
        Check(accepted == pair.Accept && IntegrationSafety.Verified.SequenceEqual(new[] { pair.Next }) &&
            File.ReadAllBytes(cached).SequenceEqual(File.ReadAllBytes(pair.Accept ? pair.Next : pair.Previous)),
            $"PreserveLauncherUpdate orders {Path.GetFileName(pair.Next)} after {Path.GetFileName(pair.Previous)} (accept: {pair.Accept})");
    }
    var previous = File.ReadAllBytes(cached);
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(b)));
    Check(!await stores.PreserveLauncherUpdate(b, hash, new string('1', 64)), "unchanged running image is not an updater replacement");
    foreach (var publisherFailure in new[] { true, false })
    {
        IntegrationSafety.RejectPublisher = publisherFailure;
        PatchInstaller.RejectCompatibility = !publisherFailure;
        try
        {
            await stores.PreserveLauncherUpdate(b, new string('0', 64), new string('1', 64));
            throw new Exception("Invalid update accepted");
        }
        catch (IOException) { }
        Check(File.ReadAllBytes(cached).SequenceEqual(previous), "rejected updater image preserves cache (publisher: " + publisherFailure + ")");
        try
        {
            await stores.PrepareIntegrated(store, bridge);
            throw new Exception("Invalid launch image accepted");
        }
        catch (IOException) { }
        Check(File.ReadAllBytes(cached).SequenceEqual(previous), "rejected launch image preserves cache (publisher: " + publisherFailure + ")");
    }
}
finally
{
    // Only the randomly generated, private fixture belongs to this check.
    Directory.Delete(root, true);
}

namespace HytaleAccounts
{
    // Test-only boundaries: exercise real selection, version reads and protected storage without Hytale.
    public static class IntegrationSafety
    {
        public static string OfficialPath { get; set; } = "";
        public static readonly List<string> Verified = [];
        public static bool RejectPublisher;
        public static Task VerifyPublisher(string path)
        {
            Verified.Add(path);
            if (RejectPublisher) throw new IOException("Synthetic publisher rejection");
            return Task.CompletedTask;
        }
        public static ProcessStartInfo StartInfo(string path) => new(path);
    }
    internal sealed class PatchInstaller
    {
        internal static bool RejectCompatibility;
        internal static byte[] PatchCurrent(byte[] bytes, int port, string capability) =>
            RejectCompatibility ? throw new IOException("Synthetic compatibility rejection") : bytes;
        internal string BackupPath => throw new Exception("Legacy restoration must not run");
        internal object Read() => throw new Exception("Legacy restoration must not run");
        internal void VerifyInstalled(object record) => throw new Exception("Legacy restoration must not run");
    }
}
