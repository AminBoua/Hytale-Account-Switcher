using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class RuntimePackageChecks
{
    // Read bundle v6 metadata and PE resources without loading or executing the installer.
    // Format: dotnet/runtime src/installer/managed/Microsoft.NET.HostModel/Bundle/{Manifest,FileEntry}.cs.
    internal static void Run(string executable, string archive, string repository)
    {
        static void Require(bool value, string message) { if (!value) throw new IOException(message); }
        var bytes = File.ReadAllBytes(executable);
        ReadOnlySpan<byte> signature = [0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
            0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae];
        var marker = bytes.AsSpan().IndexOf(signature);
        Require(marker >= 8, "Missing .NET bundle marker.");
        var header = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(marker - 8, 8));
        Require(header > marker && header < bytes.Length, "Invalid bundle header offset.");
        using var input = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(input);
        input.Position = header;
        Require(reader.ReadUInt32() == 6 && reader.ReadUInt32() == 0, "Review inspection for the new bundle format.");
        var count = reader.ReadInt32();
        Require(count > 0 && count < 10000, "Invalid bundle entry count.");
        var bundleId = reader.ReadString();
        input.Position += 32; // deps/runtimeconfig offset+size.
        Require(reader.ReadUInt64() == 0, "Bundle requests legacy extraction.");
        var entries = new Dictionary<string, (int Offset, int Size, byte Type)>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var offset = reader.ReadInt64(); var size = reader.ReadInt64(); var compressed = reader.ReadInt64();
            var type = reader.ReadByte(); var name = reader.ReadString();
            Require(offset >= 0 && size > 0 && offset <= header && size <= header - offset && compressed == 0,
                "Invalid or compressed bundle entry; review inspection: " + name);
            entries.Add(name, (checked((int)offset), checked((int)size), type));
        }
        byte[] Content(string name)
        {
            Require(entries.ContainsKey(name), "Missing bundled file: " + name);
            var entry = entries[name]; return bytes.AsSpan(entry.Offset, entry.Size).ToArray();
        }
        using var config = JsonDocument.Parse(Content("HytaleAccounts.runtimeconfig.json"));
        var options = config.RootElement.GetProperty("runtimeOptions");
        Require(!options.TryGetProperty("framework", out _) && !options.TryGetProperty("frameworks", out _), "Package requires a preinstalled runtime.");
        var frameworks = options.GetProperty("includedFrameworks").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()).ToArray();
        Require(frameworks.Contains("Microsoft.NETCore.App") && frameworks.Contains("Microsoft.WindowsDesktop.App"), "Bundled runtime/WPF missing.");
        Require(entries.ContainsKey("System.Private.CoreLib.dll") && entries.ContainsKey("PresentationFramework.dll"), "Managed runtime/WPF missing.");
        var native = entries.Where(entry => entry.Value.Type == 2).Select(entry => entry.Key).Order().ToArray();
        Require(native.Length == 0 && entries.Values.All(entry => entry.Type is 1 or 3 or 4), "Ordinary startup bundle contains extraction entries.");
        var sidecars = HytaleAccounts.SetupPayload.RuntimeFiles;
        var directory = Path.GetDirectoryName(executable)!;
        Require(Directory.GetFiles(Path.Combine(directory, "build"), "*.dll").Select(Path.GetFileName).Order()
            .SequenceEqual(sidecars.Where(name => name.EndsWith(".dll", StringComparison.Ordinal)).Order()), "Published native sidecar set changed; update packaging and payload checks.");
        foreach (var name in sidecars)
        {
            var published = name.EndsWith(".dll", StringComparison.Ordinal) ? Path.Combine(directory, "build", name) : Path.Combine(directory, name);
            Require(File.ReadAllBytes(Path.Combine(directory, name)).AsSpan().SequenceEqual(File.ReadAllBytes(published)), "Runtime sidecar differs from publish output: " + name);
        }
        using var pe = new PEReader(new MemoryStream(Content("HytaleAccounts.dll"), writable: false));
        var metadata = pe.GetMetadataReader();
        var section = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
        var resources = new List<string>();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            var name = metadata.GetString(resource.Name);
            if (!name.StartsWith("HytaleAccounts.", StringComparison.Ordinal)) continue;
            var file = name["HytaleAccounts.".Length..];
            if (!file.EndsWith(".ps1", StringComparison.Ordinal) && file != "ShortcutFile.cs") continue;
            Require(resource.Implementation.IsNil, "Installer resource is external: " + name);
            var blob = section.GetReader(checked((int)resource.Offset), section.Length - checked((int)resource.Offset));
            var content = blob.ReadBytes(blob.ReadInt32());
            var source = file == "ElevatedRuntime.ps1" ? Path.Combine(repository, "Launcher", file) : Path.Combine(repository, file);
            Require(content.AsSpan().SequenceEqual(File.ReadAllBytes(source)), "Packaged resource differs from checked source: " + name);
            resources.Add(name);
        }
        Require(resources.Contains("HytaleAccounts.ElevatedRuntime.ps1") && resources.Count == 7, "Installer bootstrap/scripts missing.");
        using var zip = ZipFile.OpenRead(archive);
        var names = new[] { "HytaleAccounts-Setup.exe" }.Concat(sidecars).ToArray();
        Require(zip.Entries.Count == names.Length && zip.Entries.All(entry => names.Contains(entry.FullName)), "Unexpected release ZIP contents.");
        foreach (var name in names)
        {
            using var zipped = zip.GetEntry(name)!.Open();
            using var original = File.OpenRead(Path.Combine(directory, name));
            Require(SHA256.HashData(zipped).AsSpan().SequenceEqual(SHA256.HashData(original)), "Release ZIP bytes differ: " + name);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Kind = "static-package-inspection", ExecutedInstaller = false,
            SHA256 = Convert.ToHexString(SHA256.HashData(bytes)), BundleId = bundleId, Frameworks = frameworks, NativeLibraries = native, Resources = resources,
            OrdinaryStartup = "No native or unknown bundle entries; WPF native libraries load beside apphost without extraction",
            Elevation = "System Windows PowerShell still creates a fresh protected runtime cache before starting apphost", ZipEntries = names }));
    }
}
