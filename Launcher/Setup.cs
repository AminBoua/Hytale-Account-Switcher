using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HytaleAccounts;

internal static class Setup
{
    internal const string ApplyUser = "--apply-user-shortcuts";
    internal const string RestoreUser = "--restore-user-shortcuts";
    private const string InstallWorker = "--install-elevated";
    private const string UninstallWorker = "--uninstall-elevated";
    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    private static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is [] or ["--install"])
        {
            return await InstallAsync();
        }
        if (args is ["--uninstall"])
        {
            RequireInstalled();
            Deployment.ValidateRemoval(Sid);
            if (Elevated) { using var desktop = StartOnDesktop(Deployment.Installed, "--uninstall", Sid); }
            else
            {
                using var package = new Deployment.PackageSources(Deployment.Destination, download: false);
                using var worker = StartElevated(UninstallWorker, download: false, package.ManifestHash);
            }
            return 0;
        }
        if (args is [InstallWorker or UninstallWorker, var user] && Elevated && user == Sid && new SecurityIdentifier(user).IsAccountSid())
        {
            if (args[0] == InstallWorker) await Deployment.InstallAsync(user);
            else await Deployment.RemoveAsync(user);
            return 0;
        }
        if (args is [ApplyUser or RestoreUser] && !Elevated)
        {
            using var stage = Deployment.PinStage();
            var restore = args[0] == RestoreUser;
            using var shortcuts = new Shortcuts(Shortcuts.UserState, Shortcuts.Roots(machine: false), Sid, machine: false, writable: true, create: true);
            shortcuts.Apply(Sid, restore);
            if (restore) shortcuts.Clean();
            return 0;
        }
        if (args is [Deployment.Repair or Deployment.UserRepair])
        {
            RequireInstalled();
            await RepairAsync(args[0] == Deployment.Repair);
            return 0;
        }
        throw new IOException("Unrecognized or unauthorized setup command.");
    }

    private static async Task<int> InstallAsync()
    {
        if (Elevated) throw new IOException("Open Hytale Accounts Setup normally from your Windows desktop.");
        bool download = !string.Equals(Environment.ProcessPath, Deployment.Installed, StringComparison.OrdinalIgnoreCase);
        var source = Path.GetDirectoryName(Environment.ProcessPath!)!;
        if (download && !string.Equals(Path.GetFileName(Environment.ProcessPath), Deployment.SetupName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Open the HytaleAccounts-Setup.exe file from the complete release package.");
        await LauncherImage.ValidateOfficialAsync();
        using (var common = new Shortcuts(Deployment.Destination, Shortcuts.Roots(machine: true), Sid, machine: true, writable: false))
        using (var personal = new Shortcuts(Shortcuts.UserState, Shortcuts.Roots(machine: false), Sid, machine: false, writable: false))
            if (!common.HasLaunchLink() && !personal.HasLaunchLink())
                throw new IOException("No original Hytale launcher shortcut was found. Reinstall the official launcher and retry setup.");
        using var package = new Deployment.PackageSources(source, download);
        using var worker = StartElevated(InstallWorker, download, package.ManifestHash);
        await worker.WaitForExitAsync();
        var failedOperation = worker.ExitCode switch
        {
            125 => "check the Windows user and setup command",
            126 => "check the setup source directory",
            127 => "create a protected setup directory",
            128 => "copy and verify the setup files",
            129 => "run the protected setup worker",
            _ => null
        };
        if (failedOperation is not null)
            throw new IOException($"Setup could not {failedOperation} (setup code {worker.ExitCode}).");
        if (worker.ExitCode != 0) return worker.ExitCode;
        using var launcher = Process.Start(StartInfo(Deployment.Installed)) ?? throw new IOException("Open Hytale using your usual shortcut.");
        return 0;
    }

    private static Process StartElevated(string mode, bool download, string manifestHash)
    {
        string source = Path.GetDirectoryName(Environment.ProcessPath!)!;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("HytaleAccounts.Elevation.ps1")
            ?? throw new IOException("The protected setup bootstrap is missing.");
        using var reader = new StreamReader(stream);
        var values = new[] { source, download ? Deployment.SetupName : Deployment.Executable, manifestHash, mode, Sid };
        var command = reader.ReadToEnd() + "\nInvoke-AddonWorker " + string.Join(' ', values.Select(value => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"));
        var start = StartInfo(Deployment.PowerShell);
        start.UseShellExecute = true;
        start.Verb = "runas";
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Could not start the elevated setup bootstrap.");
    }

    private static ProcessStartInfo StartInfo(string executable) => new(executable)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory
    };

    private static void RequireInstalled()
    {
        if (!string.Equals(Environment.ProcessPath, Deployment.Installed, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This command must run from the installed addon.");
    }

    private static async Task RepairAsync(bool machine)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (machine ? !identity.IsSystem : Elevated) throw new IOException("The shortcut repair account does not match its scope.");
        var state = machine ? Deployment.Destination : Shortcuts.UserState;
        using var installation = new WindowsFiles.DirectoryPin(Deployment.Destination, machine: true, forMutation: true);
        while (WindowsFiles.FileExists(Deployment.Installed))
        {
            var journal = Shortcuts.ReadJournal(state, machine);
            if (journal is not null && WindowsFiles.FileExists(LauncherImage.OfficialPath))
            {
                var sid = machine ? journal.UserSid ?? throw new IOException("The machine shortcut journal has no owner.") : Sid;
                using var links = new Shortcuts(state, Shortcuts.Roots(machine), sid, machine, writable: true);
                links.Apply(sid, restore: false);
            }
            await Task.Delay(TimeSpan.FromMinutes(1));
        }
    }

    internal static async Task RunOnDesktopAsync(string path, string mode, string sid)
    {
        using var process = StartOnDesktop(path, mode, sid);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("The desktop shortcut operation failed. Its recovery journal was preserved.");
    }

    private static Process StartOnDesktop(string executable, string mode, string sid)
    {
        using var current = WindowsIdentity.GetCurrent();
        if (current.User!.Value != sid || !GetTokenInformation(current.AccessToken, 19, out IntPtr linkedHandle, IntPtr.Size, out _))
            throw new IOException("Setup requires the installing user's Windows desktop session.");
        using var linked = new SafeAccessTokenHandle(linkedHandle);
        using var user = new WindowsIdentity(linked.DangerousGetHandle());
        using var caller = Process.GetCurrentProcess();
        var session = caller.SessionId;
        var shell = GetShellWindow();
        GetWindowThreadProcessId(shell, out var shellPid);
        if (shell == IntPtr.Zero || shellPid == 0) throw new IOException("Could not identify your Windows desktop.");
        using var desktop = Process.GetProcessById((int)shellPid);
        if (!OpenProcessToken(desktop.Handle, 0xA, out var shellToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var shellLifetime = shellToken;
        using var shellUser = new WindowsIdentity(shellToken.DangerousGetHandle());
        if (session == 0 || desktop.SessionId != session || user.User != current.User || shellUser.User != current.User ||
            new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator) || new WindowsPrincipal(shellUser).IsInRole(WindowsBuiltInRole.Administrator) ||
            !TokenEquals(shellToken, 8, 1) || !TokenEquals(shellToken, 12, session) || !TokenEquals(shellToken, 18, 3) ||
            !TokenEquals(linked, 12, session) || !TokenEquals(linked, 18, 3))
            throw new IOException("Setup requires the same Windows user and unelevated desktop session.");
        if (!DuplicateTokenEx(shellToken, 0x02000000, IntPtr.Zero, 2, 1, out var primary)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var launchToken = primary;
        using var directory = new WindowsFiles.DirectoryPin(Path.GetDirectoryName(executable)!, machine: true);
        if (!CreateEnvironmentBlock(out var environment, launchToken, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default", Flags = 1 };
            if (!CreateProcessWithTokenW(launchToken, 1, executable, new StringBuilder($"\"{executable}\" {mode}"),
                0x08000400, environment, Environment.SystemDirectory, ref startup, out var process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var handle = new SafeProcessHandle(process.Process, ownsHandle: true);
            CloseHandle(process.Thread);
            return Process.GetProcessById(process.ProcessId);
        }
        finally { DestroyEnvironmentBlock(environment); }
    }

    private static bool TokenEquals(SafeAccessTokenHandle token, int kind, int expected) =>
        GetTokenInformation(token, kind, out int actual, sizeof(int), out _) && actual == expected;

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
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle copy);
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
