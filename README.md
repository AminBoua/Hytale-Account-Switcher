# Hytale Account Switcher

An account-switching addon for the Windows Hytale launcher. Keep using your existing **Hytale Launcher** desktop shortcut or Start-menu search entry. Saved accounts appear in the launcher's existing profile dropdown; there is no second visible launcher.

- Switch saved accounts in the same launcher window.
- **Add account** opens Hytale's own login website in your normal default browser.
- Keep the current account selected until a new native login succeeds.
- Log out of the selected account and restore another usable saved account without restarting.
- Show native sign-in when no usable account remains, without extra sign-in controls.
- Remove the addon through **Windows Settings > Apps** to restore normal launcher shortcuts.

This is an unofficial addon. Its download contains the addon and .NET runtime, not Hytale's launcher executable or game files.

## Contents

- [Download and install](#download-and-install)
- [Compatibility](#compatibility)
- [Using accounts and uninstalling](#using-accounts-and-uninstalling)
- [Build from source: complete tutorial](#build-from-source-complete-tutorial)
- [Run checks individually](#run-checks-individually)
- [Publish manually](#publish-manually)
- [Test your build](#test-your-build)
- [Troubleshooting](#troubleshooting)
- [How it works](#how-it-works)
- [Source layout](#source-layout)

## Download and install

To use the ready-built addon, you do **not** need Git, Node.js, Visual Studio, a separate .NET installation, or the source archive.

1. Install the official Hytale launcher and open it once to confirm it works.
2. Close any running Hytale game. Setup can close the launcher normally, but never forcibly terminates the game.
3. Download [HytaleAccounts-Setup.exe](https://github.com/AminBoua/Hytale-Account-Switcher/releases/download/v0.1.0/HytaleAccounts-Setup.exe) from [Releases](https://github.com/AminBoua/Hytale-Account-Switcher/releases).
4. Double-click **HytaleAccounts-Setup.exe**. Keep that filename so the executable enters setup mode.
5. Approve Windows' administrator prompt. Setup needs permission to add files to the existing launcher installation and update its original system-wide shortcuts.
6. Setup opens Hytale with the addon. Afterwards, keep opening your original **Hytale Launcher** shortcut or Start-menu entry.

Setup creates no second launcher entry and no addon uninstall shortcut. The addon appears as **Hytale Accounts addon** in Windows Settings > Apps. Only installation/removal is elevated; the launcher starts with your normal Windows user profile.

Updating the earlier per-user package automatically removes its duplicate launcher and uninstall shortcuts before integrating with the original shortcuts.

### Verify the download

Download [SHA256SUMS.txt](https://github.com/AminBoua/Hytale-Account-Switcher/releases/download/v0.1.0/SHA256SUMS.txt) from the same release. Open PowerShell in the download folder and run:

```powershell
Get-FileHash -LiteralPath .\HytaleAccounts-Setup.exe -Algorithm SHA256
Get-Content -LiteralPath .\SHA256SUMS.txt
```

The two SHA-256 values must match; letter case does not matter. This verifies that the file matches the release artifact. The executable is unsigned and can show Windows' standard reputation prompt.

## Compatibility

| Requirement | Supported value |
| --- | --- |
| Platform | Windows x64 |
| Official launcher path | `C:\Program Files\Hypixel Studios\Hytale Launcher\hytale-launcher.exe` |
| Official launcher build | `2026.09.30-326a0c2` |
| Original executable SHA-256 | `e34e33bf28271cb2647c37624591e6e588ea5d55619deadc18f8e67491f2fafe` |
| Original publisher | Hypixel Studios Canada Inc |

The supported **launcher build** differs from the game version displayed beside Play. Setup and every addon launch verify the exact original executable and its publisher signature. A launcher update requires a compatible addon release; rebuilding this source alone does not support different native bytes.

Inspect your launcher in PowerShell:

```powershell
$launcher = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
(Get-Item -LiteralPath $launcher).VersionInfo.ProductVersion
Get-FileHash -LiteralPath $launcher -Algorithm SHA256
Get-AuthenticodeSignature -LiteralPath $launcher | Select-Object Status, SignerCertificate
```

## Using accounts and uninstalling

If no usable native login exists, Hytale shows its normal sign-in page. After signing in, open the existing profile dropdown and choose **Add account**. Complete Hytale's login yourself in your default browser. The current account is kept until Hytale's native callback verifies the new account.

Choose a saved account in the dropdown to switch without a browser login or launcher restart. Hytale's normal **Logout** action applies to the currently selected account. Another usable saved session is restored before sign-in is shown; if none remain, native sign-in appears in the same window.

Close the game before switching accounts or logging out. Play remains Hytale's own action and is blocked while account verification is pending.

To remove the addon:

1. Close the game.
2. Open **Windows Settings > Apps**, find **Hytale Accounts addon**, and choose **Uninstall**.
3. Approve Windows' administrator prompt.
4. Open your usual **Hytale Launcher** shortcut again.

Removal closes the addon launcher normally, restores original shortcut bytes, removes addon files, and unregisters the Apps entry. Later changes you made to shortcuts are preserved. Hytale's signed executable, current native login, session keys, saved snapshots, game installation, settings and worlds are kept. Removal does not log you out or delete accounts.

Use the uninstaller rather than simply deleting the helper executable: removal also restores shortcuts that point to the helper.

## Build from source: complete tutorial

The recommended build validates the source and produces a self-contained, double-click setup executable like the one in Releases.

### 1. Prepare the machine

Use Windows x64 with the supported official launcher installed in its standard location. The native checks use that executable as a binary fixture. Ordinary automated checks do not require a Hytale login or multiple saved accounts.

Close the Hytale launcher and game before building. The standard build uses temporary test fixtures; it does not run the optional live browser-login/account checks.

### 2. Install development tools

| Tool | Install | Purpose |
| --- | --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) | Windows **x64 SDK**, from the SDK section | Compile C# and publish the self-contained executable |
| [Node.js](https://nodejs.org/en/download) | Windows x64 LTS; checks have been tested with Node 22 | Run frontend checks using built-in Node modules |
| [Git for Windows](https://git-scm.com/install/windows) | Normal Windows installer, if cloning | Download source and check out release tags |

Install the .NET **SDK**, not just the runtime. The runtime alone cannot compile the project. Visual Studio is optional. Windows PowerShell is included with Windows. There are no third-party NuGet or npm package dependencies and no `npm install` step.

Open a **new** PowerShell window after installing tools so PATH changes take effect:

```powershell
dotnet --version
dotnet --list-sdks
node --version
git --version
```

The SDK list should include a `10.0.x` SDK. If a command is missing, confirm the installation and reopen PowerShell. The first .NET restore/publish needs internet access to download any missing Microsoft targeting/runtime packs; later builds can reuse the local cache.

### 3. Get the source

With Git:

```powershell
git clone https://github.com/AminBoua/Hytale-Account-Switcher.git
Set-Location .\Hytale-Account-Switcher
```

For the source corresponding to the downloadable release:

```powershell
git checkout v0.1.0
```

Alternatively, use GitHub's **Code > Download ZIP**, extract the archive, and open PowerShell in the extracted folder. Git is not required to build an extracted archive.

Your current directory must contain `Build-Installer.ps1`, `README.md`, `Launcher`, and `Checks`:

```powershell
Get-ChildItem
```

### 4. Build and validate

From the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1
```

The execution-policy option applies to this launched PowerShell process; it does not change Windows' persistent policy. Run the build as a normal user. Installing the result needs administrator approval, but compiling and fixture checks do not.

The script runs these steps in order:

1. C# checks in Release configuration: native storage, rollback, logout fallback, bridge validation, embedded setup resources and binary adapter/restoration checks.
2. Frontend checks against the actual embedded JavaScript using Node's built-in modules.
3. Installation/removal checks using temporary launcher files, shortcuts and a temporary registry entry.
4. Publishing for `win-x64`, bundling the .NET runtime and native libraries into a single executable with debug symbols omitted.
5. Copying the helper to the setup filename and writing its SHA-256 checksum.

A failed step stops the build. Read the first failure and fix its cause before distributing an output file. An executable left from an earlier successful build does not mean the current checks passed.

A successful run ends with:

```text
Ready: dist/HytaleAccounts-Setup.exe
```

### 5. Find and verify the output

| Output | Purpose |
| --- | --- |
| `dist/HytaleAccounts-Setup.exe` | Finished double-click installer; distribute this file |
| `dist/SHA256SUMS.txt` | SHA-256 of that exact setup executable |
| `dist/build/HytaleAccounts.exe` | Published helper before copying to the setup filename |

The frontend and installation/removal scripts are embedded into the executable. End users do not need these source files next to setup. The bundled runtime makes the executable much larger than the source, but removes the need for a separate .NET runtime installation.

Verify your output:

```powershell
Get-FileHash -LiteralPath .\dist\HytaleAccounts-Setup.exe -Algorithm SHA256
Get-Content -LiteralPath .\dist\SHA256SUMS.txt
```

### 6. Install your build

Double-click `dist\HytaleAccounts-Setup.exe` and approve the administrator prompt. Keep opening **Hytale Launcher** as usual.

To explicitly enter setup mode using the published helper:

```powershell
.\dist\build\HytaleAccounts.exe --install
```

Avoid running `Install.ps1` directly as a normal user. The setup entry point handles elevation, removal of the earlier package, and starting the native launcher under the invoking user's normal profile.

## Run checks individually

From the repository root, with the launcher and game closed:

```powershell
dotnet run --project .\Checks\Checks.csproj -c Release
node .\Checks\NativeLoginChecks.cjs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Checks\InstallerScriptChecks.ps1
```

C# checks cover opaque snapshots, native keys, activation rollback, identity verification, pending login, reauthentication, logout isolation, bridge requests and the supported native adapter. JavaScript checks cover browser-login dispatch, preservation of native login parameters, immediate transition scheduling and native logout/sign-in behavior.

Installer checks verify that user and system shortcuts keep their names, no second launcher entry is created, originals are restored exactly, later user changes and official updates are preserved, and invalid restoration journals are rejected.

Automated checks do not perform a real browser login, launch the game, or establish server-side logout isolation. Use the manual sequence below to check those interactions.

## Publish manually

For a compiler-only build, or to inspect publishing separately from validation:

```powershell
dotnet publish .\Launcher\Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\dist\build
```

Create the setup filename and checksum:

```powershell
Copy-Item -LiteralPath .\dist\build\HytaleAccounts.exe -Destination .\dist\HytaleAccounts-Setup.exe -Force
$hash = (Get-FileHash -LiteralPath .\dist\HytaleAccounts-Setup.exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  HytaleAccounts-Setup.exe" | Set-Content -LiteralPath .\dist\SHA256SUMS.txt -Encoding ASCII
```

Manual publish does **not** run checks. The recommended `Build-Installer.ps1` validates before packaging. SDK versions and source revisions can change binary checksums; compare a download to its release checksum, not to an unrelated local build.

## Test your build

After installing:

1. Search **Hytale Launcher** in Start and open your original entry. Check that no second launcher entry or duplicate desktop shortcut exists.
2. Sign in to account A through Hytale's normal login.
3. Use **Add account** to sign in to account B in your default browser. The launcher window must remain open.
4. Select account A in the dropdown. The same window should switch accounts without opening a browser.
5. Log out of account A. Account B should become active in that window instead of returning to sign-in while a usable account remains.
6. When no usable account remains, confirm native sign-in contains no extra account controls.
7. Remove **Hytale Accounts addon** through Windows Settings > Apps. Open the original shortcut and confirm normal launcher behavior and preserved game data.

This sequence logs out an actual account, so use accounts you are willing to sign in to again.

## Troubleshooting

| Problem | What to check |
| --- | --- |
| `dotnet` is not recognized | Install .NET 10 **SDK** and open a new terminal. |
| `node` is not recognized | Install Node.js and open a new terminal. Node is only needed for checks. |
| SDK/target-framework error | Confirm `dotnet --list-sdks` includes .NET 10. |
| Script cannot be found | Change to the repository root, not the `Launcher` folder. |
| Checks require an idle launcher | Close Hytale and the game normally, then rerun. |
| Official executable missing | Install the official launcher in its standard location. |
| Unsupported build/hash/signature | Compare with the compatibility table. Use a compatible addon or remove it and use normal Hytale. Never replace an updated launcher with an older backup. |
| Setup cannot write files/shortcuts | Close the game, approve elevation with an administrator account, and check access to Hytale's installation and shortcuts. |
| Shortcut-change/invalid-journal error | Unexpected files are being preserved. Inspect the original shortcuts and restoration backups before retrying; do not delete backups blindly. |
| No launcher shortcut found | Reinstall the official launcher so its original shortcuts exist, then retry setup. |
| Account switching blocked during Play | Close the game before switching or logging out. |
| Add account awaiting authentication | Complete Hytale's browser login. The previous account remains selected until native authentication succeeds. |
| Saved account cannot be restored | Sign in again using Hytale's native flow; logout or expiry can make a snapshot unusable. |

## How it works

### Launcher integration

The helper and removal scripts are installed inside the existing launcher directory:

```text
C:\Program Files\Hypixel Studios\Hytale Launcher\.hytale-accounts-addon
```

Setup changes the targets of existing launcher shortcuts and stores their original bytes. The signed original `hytale-launcher.exe` remains intact. Opening that executable directly bypasses shortcut integration; ordinary desktop and Start-menu shortcuts use the addon.

At runtime, the invisible helper verifies the original executable and creates a temporary native copy with the embedded frontend bootstrap and a small native session adapter. The helper has no account-chooser window. The temporary executable stays read-only during use and is removed on normal exit.

The adapter targets exact native functions and binary offsets, including Go stack/preemption metadata, in the supported build. This is why arbitrary launcher updates are rejected.

### Authentication and storage

Hytale owns browser authentication, active encrypted `account.dat`, encryption keys, token refresh, logout and Play. The addon does not implement custom OAuth or decrypt credentials.

The native launcher uses the official `GameInstallPath` registry value, falling back to `%APPDATA%\Hytale`. Opaque account snapshots and local metadata live beneath that root's `Accounts` directory. Your default browser, Windows profile, installation, worlds and settings keep their normal locations. Launcher profile-related environment variables are not redirected.

Switching pauses native account tasks, restores the chosen opaque snapshot to the official active location, asks Hytale to load it, verifies the native identity and resumes tasks in the existing process/window. Failed verification or metadata commits restore the previous account. Add preserves the current account until login succeeds. Logout invokes native logout for the selected account and restores a usable fallback before showing sign-in.

A loopback HTTP bridge connects the helper and native frontend. Requests require an unpredictable per-launch capability and the native Wails origin, and request sizes are limited. It exposes no separate credential vault or replacement Play action.

### Removal

The uninstaller validates the protected restoration journal and backups, closes the addon normally, restores shortcuts that still belong to it, removes exact addon-owned files and unregisters its Apps entry. It never recursively deletes Hytale's account/game directories. Later shortcut changes and newer official launcher bytes are preserved.

## Source layout

| Path | Responsibility |
| --- | --- |
| `Launcher/Launcher.csproj` | Windows executable and embedded frontend/setup resources |
| `Launcher/App.xaml.cs` | Invisible helper startup, setup commands and single-instance handling |
| `Launcher/Setup.cs` | Setup elevation, earlier-package removal and normal-user launcher startup |
| `Launcher/NativeStores.cs` | Official storage, opaque snapshots, identity metadata and atomic activation |
| `Launcher/NativeSession.cs` | In-place switching, pending login, verification and logout fallback |
| `Launcher/NativeDropdown.js` | Existing profile dropdown and native browser-login integration |
| `Launcher/LocalAccountBridge.cs` | Capability-protected loopback communication |
| `Launcher/PatchInstaller.cs` | Exact-build adapter/bootstrap and restoration safeguards |
| `Launcher/IntegrationSafety.cs` | Compatibility, native publisher verification and process environment |
| `Checks/` | Native, frontend and installer checks used by the build |
| `Install.ps1`, `Uninstall.ps1`, `InstallerSafety.ps1` | Auditable scripts embedded in setup |
| `Build-Installer.ps1` | Validation, publishing, setup creation and checksum |

Generated binaries, logs, local account data, encryption keys and game files are not part of the source repository. The ready-built installer and checksum are distributed as release assets.
