using System.Windows;

namespace HytaleAccounts;
public partial class App : Application
{
    private Mutex? instance;
    private NativeSession? session;
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains(Setup.ElevatedMode))
        {
            base.OnStartup(e); ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await Setup.RunElevated(e.Args); Shutdown(0); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Hytale Accounts setup"); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--install") || e.Args.Contains("--uninstall") ||
            Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("HytaleAccounts-Setup", StringComparison.OrdinalIgnoreCase))
        {
            base.OnStartup(e); ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Shutdown(await Setup.Run(e.Args.Contains("--uninstall"))); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Hytale Accounts setup"); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 1 && e.Args[0].StartsWith("--patch-", StringComparison.Ordinal))
        {
            base.OnStartup(e); ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                NativeStores.RequireIdle();
                var installer = new PatchInstaller();
                switch (e.Args[0])
                {
                    case "--patch-check": await installer.CheckOriginal(); break;
                    case "--patch-install": throw new IOException("Run HytaleAccounts-Setup.exe to install the account addon.");
                    case "--patch-recover": await installer.Recover(); break;
                    case "--patch-uninstall": await installer.Uninstall(); break;
                    default: throw new IOException("Unknown patch command.");
                }
                Shutdown(0);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Hytale Accounts installer"); Shutdown(1); }
            return;
        }
        instance = new Mutex(true, "Local\\HytaleAccounts-" + Environment.UserName, out var first);
        if (!first) { MessageBox.Show("Hytale Accounts is already open."); Shutdown(); return; }
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var stores = new NativeStores();
            session = new NativeSession(stores, Dispatcher);
            session.Status += message =>
            {
                try { File.AppendAllText(Path.Combine(stores.HytaleRoot, "hytale-accounts.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine); }
                catch (IOException) { /* Logging must not interrupt account transitions. */ }
            };
            session.Closed += () => Shutdown();
            await session.OpenInitial();
            if (!session.IsOpen) Shutdown(1);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Hytale Launcher"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
