using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HytaleAccounts.Checks;

internal static class PackageChecks
{
    internal static void Run(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        var entries = zip.Entries.ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        using var manifest = JsonDocument.Parse(Read(entries["addon-payload.json"]));
        var version = Version.Parse(manifest.RootElement.GetProperty("Version").GetString()!);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "addon-payload.json" };
        foreach (var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
        {
            var name = file.GetProperty("Name").GetString()!;
            Require(Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]+\z"), "Invalid payload filename.");
            Require(name is "HytaleAccounts.exe" or "LICENSE" or "THIRD-PARTY-NOTICES.txt" ||
                name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase), "Unexpected payload type.");
            var zipName = name == "HytaleAccounts.exe" ? "HytaleAccounts-Setup.exe" : name;
            Require(files.Add(zipName), "Duplicate payload filename.");
            using var stream = entries[zipName].Open();
            Require(Convert.ToHexString(SHA256.HashData(stream)).Equals(
                file.GetProperty("Sha256").GetString(), StringComparison.OrdinalIgnoreCase), $"Payload hash mismatch for {name}.");
        }
        Require(files.SetEquals(entries.Keys), "ZIP contains files outside the package manifest.");
        Require(files.IsSupersetOf(["HytaleAccounts-Setup.exe", "LICENSE", "THIRD-PARTY-NOTICES.txt"]) &&
            entries["LICENSE"].Length > 0 && entries["THIRD-PARTY-NOTICES.txt"].Length > 0, "Package is incomplete.");
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var setup = Path.Combine(directory.FullName, "HytaleAccounts-Setup.exe");
            entries["HytaleAccounts-Setup.exe"].ExtractToFile(setup);
            Require(FileVersionInfo.GetVersionInfo(setup).ProductVersion == version.ToString(),
                "Setup EXE product version differs from the manifest.");
            InspectBundle(File.ReadAllBytes(setup), version);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
        Console.WriteLine("PASS package manifest, hashes, setup product version, self-contained Windows x64 bundle, and static resource inspection");
    }

    private static void InspectBundle(byte[] image, Version version)
    {
        using var stream = new MemoryStream(image, writable: false);
        using var apphost = new PEReader(stream, PEStreamOptions.LeaveOpen);
        Require(apphost.PEHeaders.CoffHeader.Machine == Machine.Amd64 &&
            apphost.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsGui, "Package requires an x64 Windows GUI-subsystem executable.");

        // .NET bundle v6 stores the header offset immediately before this marker.
        var marker = image.AsSpan().IndexOf(Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE"));
        Require(marker >= 8, "Missing .NET bundle marker.");
        var header = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(marker - 8, 8));
        Require(header > marker && header < image.Length, "Invalid .NET bundle header offset.");
        stream.Position = header;
        using var reader = new BinaryReader(stream);
        Require(reader.ReadUInt32() == 6 && reader.ReadUInt32() == 0, "Review inspection for the changed .NET bundle format.");
        var count = reader.ReadInt32();
        Require(count is > 0 and < 10000, "Invalid .NET bundle entry count.");
        _ = reader.ReadString();
        stream.Position += 32;
        Require(reader.ReadUInt64() == 0, "Bundle requests extraction.");
        var files = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var offset = reader.ReadInt64();
            var length = reader.ReadInt64();
            var compressed = reader.ReadInt64();
            var type = reader.ReadByte();
            var name = reader.ReadString();
            Require(type is 1 or 3 or 4 && compressed == 0 && offset >= 0 && offset < header &&
                length > 0 && length <= header - offset, $"Invalid or extracted bundle entry {name}.");
            files.Add(name, (checked((int)offset), checked((int)length)));
        }

        byte[] Bundled(string name)
        {
            var (offset, length) = files[name];
            return image.AsSpan(offset, length).ToArray();
        }

        using var config = JsonDocument.Parse(Bundled("HytaleAccounts.runtimeconfig.json"));
        var options = config.RootElement.GetProperty("runtimeOptions");
        var frameworks = options.GetProperty("includedFrameworks").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()).ToArray();
        Require(!options.TryGetProperty("framework", out _) && !options.TryGetProperty("frameworks", out _) &&
            frameworks.SequenceEqual(["Microsoft.NETCore.App"]) && files.ContainsKey("System.Private.CoreLib.dll"),
            "Package must include .NET without a desktop framework dependency.");
        using var assembly = new PEReader(new MemoryStream(Bundled("HytaleAccounts.dll"), writable: false));
        var metadata = assembly.GetMetadataReader();
        Require(metadata.GetAssemblyDefinition().Version == new Version(version.Major, version.Minor, version.Build, 0),
            "Package version differs from the manifest.");
        var section = assembly.GetSectionData(assembly.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress);
        var sources = new Dictionary<string, string>
        {
            ["HytaleAccounts.Elevation.ps1"] = "Launcher/Elevation.ps1",
            ["HytaleAccounts.NativeDropdown.js"] = "Launcher/NativeDropdown.js"
        };
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            var name = metadata.GetString(resource.Name);
            Require(sources.Remove(name, out var path) && resource.Implementation.IsNil, $"Unexpected resource {name}.");
            var offset = checked((int)resource.Offset);
            var blob = section.GetReader(offset, section.Length - offset);
            var content = blob.ReadBytes(blob.ReadInt32());
            Require(content.AsSpan().SequenceEqual(File.ReadAllBytes(path!)), $"Packaged resource differs from source for {name}.");
        }
        Require(sources.Count == 0, "Missing launcher resources.");
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new IOException(message);
    }
}
