using HytaleAccounts.Checks;

try
{
    if (args is ["--package", var zip])
    {
        PackageChecks.Run(zip);
        return 0;
    }

    if (args.Length != 0 && args is not ["--safe"])
        throw new ArgumentException("Use --safe or --package <zip>.");

    await SessionChecks.Run();
    StorageChecks.Run();
    await BridgeChecks.Run();
    await LauncherImageChecks.Run();
    SetupChecks.Run();
    ShortcutChecks.Run();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
