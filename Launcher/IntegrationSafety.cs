using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HytaleAccounts;

public record DebugListener(IPAddress Address, int Port, int Pid);
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
    public static bool BlocksDebugOverrides(string path) => File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.ASCII.GetBytes("webviewloader.preventEnvAndRegistryOverrides")) >= 0;
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
    public static bool IsSafe(IEnumerable<DebugListener> listeners, int port, ISet<int> allowedPids)
    {
        var found = listeners.Where(l => l.Port == port).ToList();
        return found.Count > 0 && found.All(l => IPAddress.IsLoopback(l.Address) && allowedPids.Contains(l.Pid));
    }
    public static void VerifyPort(int port, int launcherPid)
    {
        var descendants = Descendants(launcherPid);
        if (!IsSafe(Listeners(), port, descendants)) throw new InvalidOperationException("The debugging listener is not confined to this launcher's loopback connection. Integration has been disabled.");
    }
    public static bool ValidTarget(Uri uri, int port) => uri.Scheme == "ws" && uri.Host == "127.0.0.1" && uri.Port == port && uri.AbsolutePath.StartsWith("/devtools/page/", StringComparison.Ordinal);
    public static ProcessStartInfo StartInfo(string path, int? port = null)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)!, WindowStyle = ProcessWindowStyle.Normal };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)).ToList()) start.Environment.Remove(key);
        if (port.HasValue) start.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = $"--remote-debugging-address=127.0.0.1 --remote-debugging-port={port}";
        return start;
    }
    public static List<DebugListener> Listeners()
    {
        var result = new List<DebugListener>();
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, 3, 0) != 0) throw new IOException("Cannot inspect debugging connection exposure.");
                var count = Marshal.ReadInt32(buffer);
                var rowSize = family == 2 ? 24 : 56;
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * rowSize);
                    var bytes = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                    var portOffset = family == 2 ? 8 : 20;
                    var port = (Marshal.ReadByte(row, portOffset) << 8) + Marshal.ReadByte(row, portOffset + 1);
                    result.Add(new(new IPAddress(bytes), port, Marshal.ReadInt32(row, family == 2 ? 20 : 52)));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }
    public static HashSet<int> Descendants(int root)
    {
        var result = new HashSet<int> { root };
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new IOException("Cannot verify debugging process ownership.");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Exe = "" };
            var all = new List<(int Pid, int Parent)>();
            if (Process32First(snapshot, ref entry)) do { all.Add(((int)entry.Pid, (int)entry.ParentPid)); } while (Process32Next(snapshot, ref entry));
            var changed = true;
            while (changed) { changed = false; foreach (var p in all) if (result.Contains(p.Parent) && result.Add(p.Pid)) changed = true; }
            return result;
        }
        finally { CloseHandle(snapshot); }
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr DefaultHeap;
        public uint Module, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
}
