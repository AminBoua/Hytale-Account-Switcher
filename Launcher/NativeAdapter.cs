using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace HytaleAccounts;

// Resolve relocated Go functions; reject changes to the adapter's calling convention and receiver layout.
internal static class NativeAdapter
{
    internal sealed record Fingerprints(string Wrapper, string Receiver);
    private static readonly Fingerprints SupportedBuild = new(
        "DD6C4F61BCEA6A8C9D3C4699630E81A5CCE6EDA9A267BD282B025878B0E30693",
        "EB0D23B4FE16090D9681B633E3B3943852803C8B73CCC384749E3E54717759F3");

    internal static void Apply(byte[] bytes, Fingerprints? fingerprints = null)
    {
        try { ApplyCore(bytes, fingerprints ?? SupportedBuild); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or BadImageFormatException or InvalidOperationException)
        { throw new IOException("The updated launcher has an incompatible native account adapter. Files and accounts were preserved.", ex); }
    }
    private sealed record Function(int Entry, int Size, int Metadata, int Arguments);
    private static void ApplyCore(byte[] bytes, Fingerprints fingerprints)
    {
        using var pe = new PEReader(new MemoryStream(bytes, false));
        if (pe.PEHeaders.CoffHeader.Machine != System.Reflection.PortableExecutable.Machine.Amd64)
            throw new IOException("Only the Windows x64 launcher is supported.");
        var text = pe.PEHeaders.SectionHeaders.Single(s => s.Name == ".text");
        int U32(int at) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4)));
        int Word(int at) => checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at, 8)));
        var header = bytes.AsSpan().IndexOf(Convert.FromHexString("F1FFFFFF00000108"));
        if (header < 0) throw new IOException("Launcher Go function metadata is missing.");
        var count = Word(header + 8);
        var names = checked(header + Word(header + 32));
        var pc = checked(header + Word(header + 56));
        var table = checked(header + Word(header + 64));
        if (count is < 1 or > 1000000 || names >= pc || pc >= table || table > bytes.Length - checked(count * 8 + 4))
            throw new IOException("Invalid launcher function metadata.");
        var functions = new Dictionary<string, Function>();
        const string service = "hytale-launcher/srv/account.(*Service).CheckNetworkMode";
        const string controller = "hytale-launcher/srv/account.(*Controller).";
        const string auth = "hytale-launcher/internal/auth.(*Controller).Init";
        var wanted = new[] { service, controller + "CheckNetworkMode", controller + "Shutdown", controller + "Init", controller + "StartUserTasks", auth };
        for (var i = 0; i < count; i++)
        {
            var entry = U32(table + i * 8);
            var end = U32(table + (i + 1) * 8);
            var metadata = checked(table + U32(table + i * 8 + 4));
            var nameAt = checked(names + U32(metadata + 4));
            if (nameAt >= pc) throw new IOException("Invalid launcher function name.");
            var length = bytes.AsSpan(nameAt, pc - nameAt).IndexOf((byte)0);
            if (length < 0) throw new IOException("Unterminated launcher function name.");
            var name = Encoding.UTF8.GetString(bytes, nameAt, length);
            if (!wanted.Contains(name)) continue;
            if (end <= entry || end > text.SizeOfRawData || U32(metadata) != entry)
                throw new IOException("Invalid launcher function bounds.");
            functions.Add(name, new Function(checked(text.PointerToRawData + entry), end - entry, metadata, U32(metadata + 8)));
        }
        Function Get(string name) => functions.TryGetValue(name, out var f) ? f : throw new IOException("Updated launcher account function is missing: " + name);
        var wrapper = Get(service);
        var network = Get(controller + "CheckNetworkMode");
        var stop = Get(controller + "Shutdown");
        var init = Get(controller + "Init");
        var run = Get(controller + "StartUserTasks");
        var load = Get(auth);
        if (wrapper.Arguments != 32 || network.Arguments != 32 || new[] { stop, init, run, load }.Any(f => f.Arguments != 8))
            throw new IOException("Updated launcher account calling convention changed.");
        if (wrapper.Size != 96) throw new IOException("Updated launcher account wrapper changed size.");
        var body = bytes.AsSpan(wrapper.Entry, wrapper.Size).ToArray();
        var destination = checked(wrapper.Entry + 27 + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(23, 4)));
        body.AsSpan(23, 4).Clear();
        body.AsSpan(53, 4).Clear();
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, digest);
        if (!digest.SequenceEqual(Convert.FromHexString(fingerprints.Wrapper)) || destination != network.Entry)
            throw new IOException("Updated launcher account wrapper changed layout.");
        // Native Init stores auth at Controller+8 immediately before invoking auth.Init.
        var initBody = bytes.AsSpan(init.Entry, init.Size);
        const int receiverSize = 9;
        const int receiverWithCall = receiverSize + sizeof(int);
        var expectedReceiver = Convert.FromHexString(fingerprints.Receiver);
        var receiver = 0;
        for (; receiver <= initBody.Length - receiverSize; receiver++)
        {
            SHA256.HashData(initBody.Slice(receiver, receiverSize), digest);
            if (digest.SequenceEqual(expectedReceiver)) break;
        }
        if (receiver > initBody.Length - receiverWithCall ||
            checked(init.Entry + receiver + receiverWithCall +
                BinaryPrimitives.ReadInt32LittleEndian(initBody.Slice(receiver + receiverSize, sizeof(int)))) != load.Entry)
            throw new IOException("Updated launcher authentication receiver layout changed.");
        var adapter = Convert.FromHexString("488B004883FF08752D48BA6879613A73746F70483911742348BA6879613A6C6F6164483911741D48BA6879613A72756E214839117417E90000000084DB7513E900000000488B4008E900000000E900000000E900000000");
        void Jump(int at, Function target) => BinaryPrimitives.WriteInt32LittleEndian(adapter.AsSpan(at + 1, 4), checked(target.Entry - (wrapper.Entry + at + 5)));
        Jump(54, network); Jump(63, stop); Jump(72, load); Jump(77, run); Jump(82, init);
        // Reuse constant Go pc tables covering the entire leaf adapter, with SP=0 and no safe points.
        int ConstantTable(byte delta)
        {
            var pattern = new byte[] { delta, (byte)wrapper.Size, 0 };
            var at = bytes.AsSpan(pc, table - pc).IndexOf(pattern);
            if (at < 0) throw new IOException("Updated launcher lacks safe leaf metadata.");
            return at;
        }
        var stack = ConstantTable(2);
        var unsafePoint = ConstantTable(1);
        if (U32(wrapper.Metadata + 28) < 1) throw new IOException("Launcher preemption metadata is missing.");
        bytes.AsSpan(wrapper.Entry, wrapper.Size).Fill(0xcc); adapter.CopyTo(bytes, wrapper.Entry);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(wrapper.Metadata + 16, 4), (uint)stack);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(wrapper.Metadata + 44, 4), (uint)unsafePoint);
    }
}
