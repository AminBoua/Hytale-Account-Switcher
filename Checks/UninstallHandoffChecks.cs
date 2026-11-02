using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using HytaleAccounts;
using Microsoft.Win32.SafeHandles;

try
{
    if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        throw new Exception("This native check requires elevation; use -RunElevated or an elevated test console.");
    UninstallHandoff.Start("");
    File.WriteAllText(args[0], "PASS native handoff launches a same-user, same-session, limited primary token; hidden child exits zero.");
}
catch (Exception ex) { File.WriteAllText(args[0], ex.ToString()); Environment.ExitCode = 1; }

internal static class HandoffProbe
{
    internal static void Verify(IntPtr process, IntPtr thread)
    {
        try
        {
            if (!OpenProcessToken(process, 0xA, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            using (var child = new WindowsIdentity(token.DangerousGetHandle()))
            using (var caller = WindowsIdentity.GetCurrent())
            {
                if (child.User != caller.User || new WindowsPrincipal(child).IsInRole(WindowsBuiltInRole.Administrator) ||
                    !GetTokenInformation(token, 8, out var type, 4, out _) || type != 1 ||
                    !GetTokenInformation(token, 18, out var elevation, 4, out _) || elevation != 3 ||
                    !GetTokenInformation(token, 12, out var session, 4, out _) || session != Process.GetCurrentProcess().SessionId)
                    throw new Exception("Native child identity, elevation, type or session is incorrect.");
            }
        }
        finally { ResumeThread(thread); } // Never leave the harmless fixture child suspended on a failed assertion.
        if (WaitForSingleObject(process, 10000) != 0 || !GetExitCodeProcess(process, out var code) || code != 0)
            throw new Exception("Hidden fixture child did not exit successfully.");
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int size, out int returned);
    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
}
