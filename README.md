# Hytale Account Switcher

Switch between saved Hytale accounts from the official launcher's existing profile dropdown on Windows. Add accounts through Hytale's browser login, then switch without reopening the browser or restarting the launcher.

**[Download the installer](https://github.com/AminBoua/Hytale-Account-Switcher/releases)** · [Installation](#installation) · [Usage](#usage) · [Troubleshooting](#troubleshooting) · [Development](#development)

> [!NOTE]
> This is an unofficial community addon, not affiliated with or endorsed by Hypixel Studios. Downloads contain the addon and its .NET runtime; the official Hytale Launcher and game must be installed separately.

## Features

- Saved accounts and **Add account** inside Hytale's existing profile dropdown.
- Account switching in the same launcher process and window.
- Native browser authentication using your default browser and normal Windows profile.
- Your current account stays selected until a new login succeeds.
- Logout switches to another usable saved account, or shows native sign-in in the same window.
- Integration with existing desktop shortcuts and the Start-menu search entry.
- Removal through Windows Settings, with shortcut restoration and Hytale data retained.

## Installation

### Requirements

- Windows x64.
- The official Hytale Launcher installed at `C:\Program Files\Hypixel Studios\Hytale Launcher\hytale-launcher.exe`, with its original shortcuts present.
- Administrator approval for installation and removal.

The packaged installer includes the .NET runtime. Development tools are only needed when building from source.

1. Open the official launcher once and confirm it works.
2. Close Hytale and any running game.
3. Download **`HytaleAccounts-Setup.exe`** from [Releases](https://github.com/AminBoua/Hytale-Account-Switcher/releases).
4. Run the installer and approve the Windows administrator prompt.
5. Setup opens Hytale with the addon enabled. Continue using your usual **Hytale Launcher** shortcut or Start-menu entry.

Setup changes existing shortcuts and saves their original state. It installs a hidden `HytaleAccounts-Repair` scheduled task that checks every minute for shortcuts rewritten by the official updater. User changes to shortcut targets are preserved. No second launcher entry or addon uninstall shortcut is created.

> [!IMPORTANT]
> Compatibility depends on the launcher executable, not the game version beside **Play**. Before integration, the addon verifies the official publisher signature and the native interfaces it needs. Compatible builds are resolved from their current frontend assets and Go metadata; changes to those interfaces can require an addon update.

### Verify the download

The build script does not sign the installer, so Windows may show a reputation or SmartScreen warning. Download `SHA256SUMS.txt` from the same release and compare it with the installer hash:

```powershell
Get-FileHash -LiteralPath .\HytaleAccounts-Setup.exe -Algorithm SHA256
Get-Content -LiteralPath .\SHA256SUMS.txt
```

The SHA-256 values should match, ignoring letter case. This checks that the file matches the published release artifact.

## Usage

Close the game before adding or switching accounts, or logging out.

| Action | Steps | Result |
| --- | --- | --- |
| First sign-in | Sign in through Hytale normally. | The addon adopts the session after Hytale verifies it. |
| Add an account | Open the profile dropdown, select **Add account**, and complete the login in your browser. | The new account is saved and selected only after native authentication succeeds. |
| Switch accounts | Select a saved account in the profile dropdown. | Hytale loads its saved native session in the existing process and window. |
| Log out | Use Hytale's normal **Logout** action. | The current account is signed out. Another usable saved account is loaded, or native sign-in appears. |

The saved-account dropdown remains available after login. Hytale's native sign-in screen remains the entry point when no usable session is available.

## Uninstall

1. Close the game.
2. Open **Windows Settings > Apps**.
3. Select **Hytale Accounts addon > Uninstall** and approve the administrator prompt.
4. Open your usual Hytale Launcher shortcut to use the official launcher again.

Removal restores shortcuts still owned by the addon and removes its helper, repair task, and installation files. It retains Hytale, worlds, settings, the current native login, session keys, and saved account snapshots. It does not intentionally log you out.

> [!IMPORTANT]
> Use the Windows uninstaller. Manually deleting the addon folder leaves shortcut integration behind.

## Authentication and local data

Hytale owns browser authentication, the active encrypted `account.dat`, encryption keys, token refresh, logout, and **Play**. The addon stores opaque copies of native session files for switching; it does not decrypt credentials or implement a separate OAuth flow.

| Location | Contents |
| --- | --- |
| `C:\Program Files\Hypixel Studios\Hytale Launcher\.hytale-accounts-addon` | Installed helper, removal and repair scripts, shortcut backups, and installation records. |
| `<Hytale data directory>\Accounts\native-stores.json` | Saved profile metadata and account selection. |
| `<Hytale data directory>\Accounts\<account>\AppData\Hytale` | Opaque native session snapshots and associated native keys. |
| `<Hytale data directory>\hytale-accounts.log` | Helper status and account transition diagnostics. |

The data directory comes from Hytale's `GameInstallPath` registry setting, preferring the current user's setting, and falls back to `%APPDATA%\Hytale`.

Treat account snapshots and keys as private session data. Do not publish them with logs or bug reports.

### Launcher integration

The installed signed `hytale-launcher.exe` stays intact. The hidden helper verifies it, creates a temporary copy with the frontend integration and native session adapter, and opens that copy as the product window. The temporary executable is removed after normal exit. Opening the installed official executable directly bypasses the addon.

The frontend communicates with the helper over a loopback HTTP bridge. Commands require a per-launch capability and the expected native Wails origin, with limits on request size, duration, and concurrency.

For switching, the helper pauses native account activity, restores the selected snapshot, loads it through Hytale, verifies the profile identity, and resumes activity. If switching fails, it attempts to restore the previous session. **Play** is blocked while account state is pending or unverified.

The helper can preserve a signed launcher update made to its runtime copy and reopen it with integration. Hytale's own update flow may restart the launcher; ordinary account changes keep its process and window open.

## Troubleshooting

| Problem | What to do |
| --- | --- |
| Signature or native adapter verification fails | Install the latest addon release. If the official executable is modified or its signature is invalid, reinstall the official launcher first. |
| Setup cannot find a launcher shortcut | Reinstall the official launcher to restore its shortcuts, then retry setup. |
| Setup cannot write files or shortcuts | Close Hytale and the game, approve elevation, and check access to the installation directory. |
| An account change is blocked | Close the running game and finish or cancel any pending browser login. |
| Add account is waiting | Complete Hytale's authentication in your default browser. The existing account stays selected until success. |
| A saved account cannot be restored | Sign into it again using **Add account**. Saved sessions can expire or become unusable after logout. |
| The addon disappears after a launcher update | Allow the shortcut repair task to run. If integration still fails, install the latest addon release. |
| Uninstall reports missing or changed restoration records | Keep the remaining addon files and resolve the reported issue before retrying removal. |

Never restore an older launcher executable over an official update.

To inspect the installed launcher's build and signature:

```powershell
$launcher = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
(Get-Item -LiteralPath $launcher).VersionInfo.ProductVersion
Get-FileHash -LiteralPath $launcher -Algorithm SHA256
Get-AuthenticodeSignature -LiteralPath $launcher |
    Select-Object Status, SignerCertificate
```

## Development

### Prerequisites

- Windows x64 and the .NET 10 SDK.
- Node.js for frontend checks; no npm packages are required.
- The official launcher in its standard installation location for native binary checks.
- Git to clone the repository. Visual Studio is optional.

The C# projects use platform libraries without third-party NuGet dependencies.

```powershell
git clone https://github.com/AminBoua/Hytale-Account-Switcher.git
Set-Location .\Hytale-Account-Switcher
```

### Build the installer

Close the launcher and game, then run from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1
```

The script runs C# checks, frontend checks, and installer fixture checks before publishing a self-contained `win-x64` executable and generating its checksum.

> [!NOTE]
> Some native lifecycle checks require the exact `2026.09.30-326a0c2` fixture executable with SHA-256 `e34e33bf28271cb2647c37624591e6e588ea5d55619deadc18f8e67491f2fafe`. A newer compatible launcher can work with the addon while failing these fixture checks.

| Output | Purpose |
| --- | --- |
| `dist/HytaleAccounts-Setup.exe` | Distributable installer. |
| `dist/SHA256SUMS.txt` | Installer checksum. |
| `dist/build/HytaleAccounts.exe` | Published helper used to create the installer. |

Run the checks individually with:

```powershell
dotnet run --project .\Checks\Checks.csproj -c Release
node .\Checks\NativeLoginChecks.cjs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Checks\InstallerScriptChecks.ps1
```

The default checks cover storage and rollback, account transitions, logout fallback, bridge validation, native binary integration, frontend behavior, and installation/removal using fixtures. Real browser login, game launch, and server-side logout behavior need manual verification.

### Manual release checks

1. Install and open Hytale through its existing shortcut. Confirm there is one launcher entry.
2. Sign into account A, add account B, then switch back to A. Confirm switching keeps the same launcher process and window and opens no browser login.
3. Log out of A and confirm B becomes active in the same window.
4. Log out of the final usable account and confirm native sign-in appears in that window without extra account controls.
5. Sign in again and confirm the saved-account dropdown and **Add account** return.
6. Check native **Play** with the intended account, then close the game.
7. Uninstall through Windows Settings and confirm the original shortcuts work and Hytale data remains.

These steps perform real logout; use accounts you can sign into again.

### Project layout

| Path | Responsibility |
| --- | --- |
| `Launcher/App.xaml.cs`, `Launcher/Setup.cs` | Hidden helper startup, single-instance handling, setup, and elevation. |
| `Launcher/NativeSession.cs` | Account changes, native verification, logout fallback, and process lifecycle. |
| `Launcher/NativeStores.cs` | Native storage discovery, snapshots, and atomic activation. |
| `Launcher/NativeDropdown.js` | Existing dropdown integration, browser login, and frontend state. |
| `Launcher/LocalAccountBridge.cs` | Local authenticated frontend/helper communication. |
| `Launcher/NativeAdapter.cs`, `Launcher/PatchInstaller.cs` | Native adapter generation, frontend patching, and legacy restoration safeguards. |
| `Launcher/IntegrationSafety.cs` | Publisher verification and integration safety checks. |
| `Install.ps1`, `Uninstall.ps1`, `InstallerSafety.ps1` | Shortcut integration, removal, and installation safeguards. |
| `Repair-Integration.ps1` | Hidden shortcut recovery after official updates. |
| `Checks/` | C#, JavaScript, and PowerShell checks. |
| `Build-Installer.ps1` | Validation, publishing, packaging, and checksum generation. |
