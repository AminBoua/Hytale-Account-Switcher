# Hytale Account Switcher

Switch between multiple Hytale accounts directly from the existing **Hytale Launcher** on Windows.

No separate launcher window and no custom login page. Saved accounts appear in Hytale's existing profile dropdown, and **Add account** uses Hytale's own browser-based login flow.

> **Unofficial addon:** This project is not affiliated with or endorsed by Hypixel Studios. The download contains the addon and its .NET runtime — not Hytale's launcher executable or game files.

## Features

- **Switch accounts without restarting** the Hytale Launcher.
- **Use the existing profile dropdown** instead of a separate account manager.
- **Add accounts through Hytale's own login flow** in your default browser.
- **Keep your current account active** until a new login succeeds.
- **Fall back to another saved account** when logging out, when possible.
- **Return to Hytale's normal sign-in screen** when no usable account remains.
- **Uninstall through Windows Settings** and restore the original launcher shortcuts.

## Download

Download the latest **`HytaleAccounts-Setup.exe`** from the [Releases](https://github.com/AminBoua/Hytale-Account-Switcher/releases) page.

### Compatibility

| Requirement | Supported value |
| --- | --- |
| Platform | Windows x64 |
| Verified Hytale Launcher builds | `2026.09.30-326a0c2`, `2026.10.09-4e41671` |
| Launcher location | `C:\Program Files\Hypixel Studios\Hytale Launcher\hytale-launcher.exe` |
| Original 2026.09.30 executable SHA-256 | `e34e33bf28271cb2647c37624591e6e588ea5d55619deadc18f8e67491f2fafe` |
| Original publisher | Hypixel Studios Canada Inc |

> [!IMPORTANT]
> Compatibility is tied to the **Hytale Launcher build**, not the game version shown beside the Play button.
>
> The addon verifies the current launcher publisher signature and native adapter structure before running. Compatible updates are handled automatically through current frontend assets and Go function metadata. Changes to the calling convention, receiver layout, frontend allocation or Go metadata still require a compatible addon release.

## Installation

You do **not** need Git, Node.js, Visual Studio, a separate .NET runtime, or the source code to use the ready-built addon.

1. Install the official Hytale Launcher and open it once to make sure it works.
2. Close any running Hytale game.
3. Download **`HytaleAccounts-Setup.exe`** from [Releases](https://github.com/AminBoua/Hytale-Account-Switcher/releases).
4. Double-click the installer.
5. Approve the Windows administrator prompt.
6. Setup will open Hytale with the addon installed.

After installation, continue opening Hytale through your normal **Hytale Launcher** desktop shortcut or Start-menu entry.

The addon does **not** create a second launcher shortcut. A hidden Windows scheduled task checks existing shortcuts every minute and restores integration if the official updater rewrites them. It preserves user-selected targets, saves the updater's latest shortcut for uninstall, and is removed with the addon.

> [!NOTE]
> The executable is currently unsigned, so Windows may display its standard reputation/SmartScreen warning.
>
> You can verify the downloaded executable against the `SHA256SUMS.txt` included with the release.

### Verify your download

Download `SHA256SUMS.txt` from the same release and open PowerShell in your download folder:

```powershell
Get-FileHash -LiteralPath .\HytaleAccounts-Setup.exe -Algorithm SHA256
Get-Content -LiteralPath .\SHA256SUMS.txt
```

The two SHA-256 values should match. Letter case does not matter.

This confirms that your executable matches the published release artifact.

## Usage

### Add another account

Sign into Hytale normally if you aren't already signed in.

Open Hytale's existing profile dropdown and select **Add account**.

Your default browser will open Hytale's own login flow. Complete the login there.

Your current account remains selected until Hytale successfully verifies the new login.

### Switch accounts

Open the profile dropdown and select one of your saved accounts.

The launcher switches to that account in the **same window**, without requiring another browser login or launcher restart.

> [!IMPORTANT]
> Close the game before switching accounts.

### Log out

Hytale's normal **Logout** action applies to the currently selected account.

If another usable saved account exists, the addon restores it instead of unnecessarily returning you to the login screen.

If no usable account remains, Hytale's normal sign-in page appears.

## Security and privacy

The addon is designed to leave authentication under Hytale's control.

**Hytale still handles:**

- Browser authentication
- The active encrypted `account.dat`
- Encryption keys
- Token refresh
- Logout
- Starting the game

The addon does **not** implement its own OAuth flow and does **not** decrypt your Hytale credentials.

Saved accounts are stored as opaque snapshots of Hytale's native account state. Account metadata and snapshots are stored beneath Hytale's existing data directory.

The original signed `hytale-launcher.exe` is left intact.

At runtime, the addon verifies the supported original executable and its publisher before creating a temporary launcher copy containing the integration needed for account switching.

The temporary executable is removed after normal launcher exit.

### Local communication

The addon uses a loopback HTTP bridge between the helper and Hytale's native frontend.

Requests require a per-launch capability and the expected native Wails origin, and request sizes are limited.

The bridge does not expose a separate credential vault or replacement Play action.

## Uninstall

1. Close the Hytale game.
2. Open **Windows Settings > Apps**.
3. Find **Hytale Accounts addon**.
4. Select **Uninstall**.
5. Approve the administrator prompt.

After uninstalling, open your normal **Hytale Launcher** shortcut again.

The uninstaller restores launcher shortcuts that still belong to the addon and removes addon-owned files.

Uninstalling does **not** delete:

- Your Hytale installation
- Worlds or settings
- Your current native login
- Hytale session keys
- Saved account snapshots

It also does not intentionally log you out.

> [!WARNING]
> Use the Windows uninstaller instead of manually deleting the addon files. The uninstaller also restores launcher shortcuts that were changed during installation.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| Incompatible native adapter/signature | Compatible updates are automatic. A changed native account interface still requires a compatible addon release. |
| Setup cannot write files or shortcuts | Close Hytale and the game, approve the administrator prompt, and check access to the Hytale installation. |
| No launcher shortcut found | Reinstall the official Hytale Launcher so its original shortcuts exist, then retry setup. |
| Account switching is blocked | Close the running game before switching accounts or logging out. |
| Add account is waiting for authentication | Complete Hytale's login flow in your browser. Your previous account stays active until it succeeds. |
| Saved account cannot be restored | Sign into that account again through Hytale. Logout or session expiry can make a saved native session unusable. |
| Launcher updated and the addon stopped working | Install the latest addon release. Never replace the updated launcher with an older executable. |

### Check your launcher version

You can inspect the installed launcher from PowerShell:

```powershell
$launcher = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'

(Get-Item -LiteralPath $launcher).VersionInfo.ProductVersion
Get-FileHash -LiteralPath $launcher -Algorithm SHA256
Get-AuthenticodeSignature -LiteralPath $launcher |
    Select-Object Status, SignerCertificate
```

## How it works

### Launcher integration

The addon helper and removal scripts are installed under:

```text
C:\Program Files\Hypixel Studios\Hytale Launcher\.hytale-accounts-addon
```

Setup updates the targets of the existing Hytale Launcher shortcuts and stores their original state so they can be restored later.

The signed original `hytale-launcher.exe` remains intact.

Opening that executable directly bypasses the shortcut integration. Normal desktop and Start-menu shortcuts launch through the addon.

At runtime, the helper:

1. Verifies the original Hytale Launcher.
2. Creates a temporary native launcher copy.
3. Adds the frontend bootstrap and native session adapter.
4. Starts the launcher normally.
5. Removes the temporary executable after normal exit.

The native adapter resolves functions and stack/preemption metadata from the current executable's Go function table. It verifies the service wrapper and authentication receiver layout before generating native branches. Earlier in-place installations retain their exact-build restoration checks.

Hytale can automatically update the temporary native executable. The helper follows that update, verifies and retains the new signed executable locally, and reopens it with the addon. Subsequent launches use that updated build until the official installation catches up. Account switching, Add and logout continue in the existing process; Hytale's own launcher updates restart it.

### Account switching

The addon works with Hytale's existing account state rather than implementing a replacement authentication system.

When switching accounts, it:

1. Pauses relevant native account activity.
2. Restores the selected opaque account snapshot.
3. Asks Hytale to load the account.
4. Verifies the native identity.
5. Resumes normal launcher activity.

If activation or verification fails, the previous account state is restored.

When adding an account, the current account remains active until Hytale's native login succeeds.

When logging out, native logout is used for the selected account. The addon then attempts to restore another usable saved account before falling back to Hytale's sign-in screen.

## Development

The recommended build process validates the project and produces the same self-contained installer format distributed through Releases.

### Requirements

| Tool | Requirement | Purpose |
| --- | --- | --- |
| .NET SDK | .NET 10 x64 SDK | Build and publish the C# launcher helper |
| Node.js | Node 22 tested | Run frontend checks |
| Git | Optional | Clone the repository and check out releases |
| Windows | x64 | Required for launcher integration and native checks |

Visual Studio is optional.

There are no third-party NuGet or npm package dependencies, and there is no `npm install` step.

The supported official Hytale Launcher must also be installed in its standard location because native validation uses it as a binary fixture.

### Clone

```powershell
git clone https://github.com/AminBoua/Hytale-Account-Switcher.git
Set-Location .\Hytale-Account-Switcher
```

To build the source corresponding to a particular release, check out its tag:

```powershell
git checkout v0.1.1
```

### Build and validate

Close the Hytale Launcher and game, then run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1
```

The build performs:

1. C# checks covering native storage, rollback, logout fallback, bridge validation and binary integration.
2. Frontend checks against the embedded JavaScript.
3. Installation/removal tests using temporary fixtures.
4. A self-contained `win-x64` publish.
5. Installer packaging and SHA-256 generation.

A successful build produces:

```text
Ready: dist/HytaleAccounts-Setup.exe
```

### Build output

| Output | Purpose |
| --- | --- |
| `dist/HytaleAccounts-Setup.exe` | Finished installer |
| `dist/SHA256SUMS.txt` | Installer SHA-256 |
| `dist/build/HytaleAccounts.exe` | Published helper before installer naming |

### Run checks individually

```powershell
dotnet run --project .\Checks\Checks.csproj -c Release
node .\Checks\NativeLoginChecks.cjs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Checks\InstallerScriptChecks.ps1
```

Automated checks do not perform a real browser login, launch the game, or establish server-side logout isolation.

Those interactions should be tested manually before publishing a release.

### Manual publish

If you only want to inspect publishing without running the complete validation process:

```powershell
dotnet publish .\Launcher\Launcher.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o .\dist\build
```

Then create the setup executable and checksum:

```powershell
Copy-Item `
    -LiteralPath .\dist\build\HytaleAccounts.exe `
    -Destination .\dist\HytaleAccounts-Setup.exe `
    -Force

$hash = (
    Get-FileHash `
        -LiteralPath .\dist\HytaleAccounts-Setup.exe `
        -Algorithm SHA256
).Hash.ToLowerInvariant()

"$hash  HytaleAccounts-Setup.exe" |
    Set-Content `
        -LiteralPath .\dist\SHA256SUMS.txt `
        -Encoding ASCII
```

> [!WARNING]
> Manual publishing does **not** run the project's validation checks. Use `Build-Installer.ps1` for release builds.

## Manual release testing

Before distributing a build:

1. Open your original **Hytale Launcher** Start-menu or desktop entry and verify no duplicate launcher shortcut exists.
2. Sign into account A through Hytale normally.
3. Select **Add account** and sign into account B through the browser.
4. Switch back to account A through the existing profile dropdown.
5. Verify the account changes without reopening the launcher or browser.
6. Log out of account A and verify account B becomes active.
7. Verify Hytale's native sign-in appears when no usable account remains.
8. Uninstall **Hytale Accounts addon** through Windows Settings.
9. Open the original Hytale Launcher shortcut and verify normal behavior and preserved game data.

> [!NOTE]
> This test performs a real logout. Use accounts you are prepared to sign into again.

## Project structure

| Path | Responsibility |
| --- | --- |
| `Launcher/Launcher.csproj` | Windows executable and embedded frontend/setup resources |
| `Launcher/App.xaml.cs` | Helper startup, setup commands and single-instance handling |
| `Launcher/Setup.cs` | Elevation, package migration and normal-user launcher startup |
| `Launcher/NativeStores.cs` | Native storage, account snapshots and atomic activation |
| `Launcher/NativeSession.cs` | Account switching, login verification and logout fallback |
| `Launcher/NativeDropdown.js` | Integration with Hytale's existing profile dropdown |
| `Launcher/LocalAccountBridge.cs` | Capability-protected local communication |
| `Launcher/PatchInstaller.cs`, `Launcher/NativeAdapter.cs` | Current-build native adapter and restoration safeguards |
| `Launcher/IntegrationSafety.cs` | Compatibility and publisher verification |
| `Checks/` | Native, frontend and installer checks |
| `Install.ps1` | Installation logic embedded into setup |
| `Uninstall.ps1` | Removal and shortcut restoration |
| `InstallerSafety.ps1` | Installer safety checks |
| `Repair-Integration.ps1` | Invisible recovery of existing shortcuts after official updates |
| `Build-Installer.ps1` | Validation, publishing and packaging |

Generated binaries, logs, local account data, encryption keys and Hytale game files are not included in the source repository.

Ready-built installers and their checksums are distributed through [Releases](https://github.com/AminBoua/Hytale-Account-Switcher/releases).

## Disclaimer

Hytale Account Switcher is an unofficial community project and is not affiliated with, authorized by, or endorsed by Hypixel Studios.

Hytale and related names and assets belong to their respective owners.
