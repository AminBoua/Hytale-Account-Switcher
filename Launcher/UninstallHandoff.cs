using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace HytaleAccounts;

internal static class UninstallHandoff
{
    // Use only the caller's linked token, never another logged-on user's token.
    internal static void Start(string destination)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(current.AccessToken, 19, out IntPtr linkedHandle, IntPtr.Size, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Removal requires the installing user's normal Windows session.");
        using var linked = new SafeAccessTokenHandle(linkedHandle);
        using var user = new WindowsIdentity(linked.DangerousGetHandle());
        var session = Process.GetCurrentProcess().SessionId;
        var shell = GetShellWindow();
        GetWindowThreadProcessId(shell, out var shellPid);
        if (shell == IntPtr.Zero || shellPid == 0)
            throw new IOException("Cannot identify your Windows desktop; files preserved.");
        using var desktop = Process.GetProcessById((int)shellPid);
        if (!OpenProcessToken(desktop.Handle, 8, out var shellToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (shellToken)
        using (var shellUser = new WindowsIdentity(shellToken.DangerousGetHandle()))
        {
            if (session == 0 || desktop.SessionId != session || user.User != current.User || shellUser.User != current.User ||
                new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator) ||
                !GetTokenInformation(linked, 12, out int tokenSession, sizeof(int), out _) || tokenSession != session ||
                !GetTokenInformation(linked, 18, out int elevation, sizeof(int), out _) || elevation != 3)
                throw new IOException("Removal requires the same Windows user and unelevated desktop session; files preserved.");
        }
        using var directories = new SetupPayload.PinnedDirectories(destination, requireTrusted: true, forMutation: true);
        var files = new List<FileStream>();
        try
        {
            foreach (var name in SetupPayload.Scripts.Concat(SetupPayload.RuntimeFiles).Concat(new[] { "HytaleAccounts.exe", "shortcuts.json" }))
            {
                var file = SetupPayload.OpenSource(Path.Combine(destination, name));
                files.Add(file);
                SetupPayload.RequireTrusted(file.GetAccessControl());
            }
            using (var journal = JsonDocument.Parse(files[^1]))
                if (!journal.RootElement.TryGetProperty("UserSid", out var owner) || owner.GetString() != current.User!.Value)
                    throw new IOException("Cannot confirm the installing Windows user; files preserved.");
            if (!CreateEnvironmentBlock(out var environment, linked, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var executable = Path.Combine(destination, "HytaleAccounts.exe");
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default", Flags = 1, ShowWindow = 0 };
                if (!CreateProcessWithTokenW(linked, 1, executable, new StringBuilder("\"" + executable + "\" --uninstall"),
                    0x08000400, environment, Environment.SystemDirectory, ref startup, out var process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(process.Thread); CloseHandle(process.Process);
            }
            finally { DestroyEnvironmentBlock(environment); }
        }
        finally { foreach (var file in files) file.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out IntPtr value, int size, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int size, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, int logonFlags, string application, StringBuilder command,
        int flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
