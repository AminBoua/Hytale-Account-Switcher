using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HytaleAccounts;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var repair = args.Length == 1 && args[0] is "--repair-integration" or "--repair-integration-user";
        var setup = args.Length != 0 || string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),
            "HytaleAccounts-Setup", StringComparison.OrdinalIgnoreCase);
        try
        {
            return setup ? Setup.RunAsync(args).GetAwaiter().GetResult() : RunLauncher();
        }
        catch (Exception error)
        {
            if (repair) Console.Error.WriteLine(error.Message);
            else ShowError(error.Message, setup ? "Hytale Accounts setup" : "Hytale Launcher");
            return 1;
        }
    }

    private static int RunLauncher()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new IOException("Open Hytale Launcher from your normal Windows desktop.");
        using var instance = new Mutex(false, @"Global\HytaleAccounts-" + identity.User!.Value);
        try
        {
            if (!instance.WaitOne(0)) return 0;
        }
        catch (AbandonedMutexException) { /* Windows grants this process ownership. */ }

        try
        {
            return new AccountSession(new AccountStorage()).RunAsync().GetAwaiter().GetResult();
        }
        finally
        {
            instance.ReleaseMutex();
        }
    }

    internal static void ShowError(string message, string title) => MessageBox(IntPtr.Zero, message, title, 0x10);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string message, string title, uint type);
}
