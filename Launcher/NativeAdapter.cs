using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace HytaleAccounts;

// Resolve relocated Go functions; reject changes to the adapter's calling convention and receiver layout.
internal static class NativeAdapter
{
    internal static void Apply(byte[] bytes)
    {
        try { ApplyCore(bytes); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or BadImageFormatException or InvalidOperationException)
        { throw new IOException("The updated launcher has an incompatible native account adapter. Files and accounts were preserved.", ex); }
    }
    private sealed record Function(int Entry, int Size, int Metadata, int Arguments);
    private static void ApplyCore(byte[] bytes)
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
        var expected = Convert.FromHexString("493B6610761B554889E54883EC2048894C2440488B00E8000000004883C4205DC34889442408885C241048894C241848897C2420E800000000488B4424080FB65C2410488B4C2418488B7C2420EBB1CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC");
        var body = bytes.AsSpan(wrapper.Entry, wrapper.Size).ToArray();
        if (body.Length != expected.Length) throw new IOException("Updated launcher account wrapper changed size.");
        var destination = checked(wrapper.Entry + 27 + BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(23, 4)));
        body.AsSpan(23, 4).Clear(); body.AsSpan(53, 4).Clear();
        if (!body.AsSpan().SequenceEqual(expected) || destination != network.Entry)
            throw new IOException("Updated launcher account wrapper changed layout.");
        // Native Init stores auth at Controller+8 immediately before invoking auth.Init.
        var initBody = bytes.AsSpan(init.Entry, init.Size);
        var receiver = initBody.IndexOf(Convert.FromHexString("48894B084889C890E8"));
        if (receiver < 0 || receiver + 13 > initBody.Length ||
            checked(init.Entry + receiver + 13 + BinaryPrimitives.ReadInt32LittleEndian(initBody.Slice(receiver + 9, 4))) != load.Entry)
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
