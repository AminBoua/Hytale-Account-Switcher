# Hytale Account Switcher

Add and switch Hytale accounts in the existing launcher window. The addon keeps the existing Hytale Launcher shortcuts and Windows search entry. Hytale controls sign-in, logout, and Play.

## Install and use

1. Install the official Hytale Launcher in its standard Program Files location.
2. Close Hytale and the launcher.
3. Compare the ZIP SHA-256 with the hash from the same build.
4. Extract the complete ZIP.
5. Open **HytaleAccounts-Setup.exe** from your normal Windows desktop.
6. Approve the Windows setup prompt.
7. Use the existing Hytale Launcher entry.

After sign-in, open the profile dropdown. Select **Add account** to open Hytale sign-in in your default browser. Use **Cancel login** to return to the previous account. Select a saved account to switch in the same launcher window. Close the game before changing accounts.

Use Logout in the Hytale settings. The addon tries another saved session after Hytale confirms logout. Hytale shows its sign-in screen when no usable account remains.

## Upgrade and remove

Use the same Windows account for setup, upgrade, and removal. To upgrade an existing rebuild installation, close Hytale and the launcher. Run the complete new setup package. To remove the addon, select **Hytale Accounts addon** in **Windows Settings > Apps**. Preserve installation files and journals until removal succeeds. Setup and removal preserve Hytale data.

If setup rejects the publisher or launcher layout, use an official Hytale installation for this build. An older in-place patch requires recovery with its original tool or a repair from the official installer.

## Local files

The addon uses the official Hytale storage root. Windows registry setting `GameInstallPath` selects the root. The fallback is `%APPDATA%\Hytale`. The catalog and opaque snapshots stay in its `Accounts` directory. Hytale stores the active session in `account.dat` and `.keys`. Treat all snapshots and keys as private.

For the specific explicit **Users** Full Control grant on the Hytale root, run `Repair-HytalePermissions.ps1` to preview the documented repair. Review its help before applying a change. The preview and repair are separate from setup.

## Build and check

Use Windows x64, .NET 10 SDK, Windows PowerShell 5.1, and Node.js 22. Run the commands from a normal Windows user session.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1 -ChecksOnly
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1
```

The build writes the installer ZIP, SHA-256 file, and validation status under `dist`. Synthetic checks use temporary files and simulated native events. Package inspection reads the EXE and ZIP.

For live acceptance, use a disposable Windows installation with test accounts. Record the launcher process ID and window handle. Add account B while account A is selected. Switch to A. Log out A and verify fallback to B. Log out B and verify native sign-in in the same window. Sign in again and verify Play. Check the default browser and the existing shortcuts.

Test fresh setup, same-user upgrade from an older package, a locked-file retry, and removal through Windows Settings. Check both shortcut scopes. Test UAC cancellation and different credentials. Verify task identities, hidden repair, and access from an unrelated user. Use `Checks/VmChecks.ps1` to record the clean install and removal observations. Use the same package hash and guest identity for all observations.

The addon is an independent project. The ZIP contains addon source output and bundled .NET files. Hytale supplies its launcher separately.
