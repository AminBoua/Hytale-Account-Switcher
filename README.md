# Hytale Account Switcher

Switch between saved Hytale accounts from the official launcher's profile dropdown, without reopening the launcher or signing in again each time.

A Windows x64 addon that keeps your usual **Hytale Launcher** shortcut, browser, and game data. Hytale handles authentication and Play; the addon adds account switching inside the existing window. This is an unofficial project, unaffiliated with Hypixel Studios.

**[Experimental downloads](https://github.com/AminBoua/Hytale-Account-Switcher/releases)** · [Install](#install) · [Use](#use) · [Troubleshoot](#troubleshoot) · [Build from source](#build-from-source)

## What it does

- **Save accounts:** choose **Add account** and complete Hytale's login in your default browser. Your current account stays selected until authentication succeeds.
- **Switch in place:** select a saved account from the dropdown. The native session reloads in the same launcher process and window.
- **Log out individually:** Hytale logs out the current account, then the addon tries another usable saved account. If none remain, native sign-in appears in the same window.
- **Keep the familiar launcher:** installation updates existing shortcuts and Windows search integration, with no second launcher entry or separate account chooser.

## Install

You need **Windows x64**, the official Hytale Launcher installed at the path below, and an existing Hytale Launcher shortcut. Setup requests administrator approval; everyday launcher use runs as your normal Windows user. The download includes its .NET runtime.

```text
C:\Program Files\Hypixel Studios\Hytale Launcher\hytale-launcher.exe
```

1. Close Hytale and the Hytale Launcher.
2. Open [Releases](https://github.com/AminBoua/Hytale-Account-Switcher/releases), read the release notes, and download **HytaleAccounts-Setup.zip** and **SHA256SUMS.txt** from the same release. The setup ZIP includes the installer and its license notices; GitHub's source archives are for developers. Older packages may provide the `.exe` separately: keep the accompanying license notices with it.
3. In PowerShell, from the download folder, check the installer hash:

   ```powershell
   Get-FileHash .\HytaleAccounts-Setup.zip -Algorithm SHA256
   Get-Content .\SHA256SUMS.txt
   ```

   The hash must match the entry for the downloaded file, ignoring letter case. For a separate `.exe`, check that file instead. A matching hash checks the download against the published file; it is not a publisher signature.
4. Extract the setup ZIP, then double-click **HytaleAccounts-Setup.exe** normally and accept its UAC prompt. Do not start it with **Run as administrator**: setup needs your normal user context to update your shortcuts.
5. Setup opens the launcher. From then on, use your existing **Hytale Launcher** shortcut or Windows search entry.

> [!IMPORTANT]
> The addon installer is unsigned, so Windows may show a reputation warning. The [v0.1.3 release notes](https://github.com/AminBoua/Hytale-Account-Switcher/releases/tag/v0.1.3) also record that actual elevated/cross-user VM validation and fresh live login, switching, logout, and gameplay checks were not performed for that release. See the [security release gate](SECURITY-RELEASE.md) for validation scope.

Setup preserves the installed official launcher's bytes and permissions. At runtime, the helper verifies the official publisher signature and prepares a modified local copy. Compatibility depends on the **launcher build**, not the game version beside Play. Compatible launcher updates can be adopted; changes to Hytale's internal account interface may require a new addon release. Compatibility checks reject unsupported layouts instead of forcing a patch.

Hidden repair tasks check existing shortcuts every minute for changes made by the official updater. Hytale's own update flow may restart the launcher; ordinary account switching keeps the window and process open.

**UAC with another administrator's credentials:** setup may reject a source file owned by a different Windows user. This is a known limitation; see [security validation](SECURITY-RELEASE.md#actual-vm-evidence-and-the-release-claim).

## Use

On first launch, an existing official session is adopted after Hytale verifies it. Otherwise, sign in through Hytale's normal sign-in screen. The account dropdown and **Add account** appear once you are signed in.

| To… | Do this |
| --- | --- |
| Add an account | Open the profile dropdown, choose **Add account**, and finish Hytale authentication in your normal browser. |
| Cancel an unfinished login | Choose **Cancel login** in the same dropdown. Your current account stays selected, and the row returns to **Add account**. |
| Switch accounts | Close the game, then select a saved account from the dropdown. No browser login or launcher restart is needed for a usable saved session. |
| Play | Wait for the selected account to finish loading, then use Hytale's normal **Play** button. |
| Log out | Use Hytale's logout action in settings. Only the current account is logged out; another usable saved account is restored if available. |
| Use a signed-out account again | Choose **Add account** and sign in again. |

Close the game before adding, switching, or logging out of an account. Failed or cancelled browser authentication keeps the previous account selected. The native sign-in screen does not display the addon's account controls.

## Remove the addon

From the **same Windows account that installed it**, close Hytale and open **Windows Settings → Apps → Hytale Accounts addon → Uninstall**. Approve the UAC prompt when requested.

Removal restores shortcuts still owned by the addon and removes its repair tasks and installed helper files. **Hytale, saved account snapshots, encryption keys, and game data are retained.** There is no separate uninstall shortcut. Uninstalling the addon does not log out your Hytale accounts. Use the uninstaller: manually deleting the addon folder leaves shortcut integration behind.

## Local data and privacy

Hytale owns authentication, the active encrypted session, logout, and Play. The addon copies opaque native session files for switching; it does not decode or re-encrypt credentials, run a custom OAuth service, or maintain a separate credential vault. Login uses your default browser and normal Windows profile.

The Hytale data root comes from the official `GameInstallPath` registry setting, falling back to `%APPDATA%\Hytale`.

| Location | Purpose |
| --- | --- |
| `<Hytale data root>\account.dat` and `.keys\` | Hytale's active native session and keys. |
| `<Hytale data root>\Accounts\` | Saved native session snapshots, account metadata, and runtime launcher copies. |
| `<Hytale data root>\hytale-accounts.log` | Account transition status and errors. |
| `<official launcher folder>\.hytale-accounts-addon\` | Installed helper, scripts, and machine shortcut restoration records. |
| `%LOCALAPPDATA%\HytaleAccounts\ShortcutIntegration\` | Per-user shortcut restoration records. |

> [!CAUTION]
> Saved snapshots contain authentication material even though the addon treats it as opaque data. Do not share `account.dat`, `.keys`, or the `Accounts` folder. Review logs before posting them publicly.

## Troubleshoot

| Symptom | What to try |
| --- | --- |
| Setup says to open it normally | Exit the elevated instance and double-click the installer from your regular Windows session. |
| Official launcher or shortcut is missing | Repair or reinstall the official Hytale Launcher in its standard location, then rerun addon setup. |
| Account controls are missing | Sign in first and launch through the existing integrated shortcut. Running the official executable directly bypasses the addon. |
| Switching is blocked | Close the game and let any pending login or transition finish. A signed-out or expired session may need **Add account** again. |
| A launcher update breaks integration | Allow a minute for shortcut repair. If integration still fails, install an addon release compatible with that launcher. Do not replace an updated launcher with an older backup or bypass signature checks. |
| A file is locked or a path is rejected | Close the conflicting program and retry. Preserve the named files; do not delete session data or loosen folder permissions to bypass a check. |
| Uninstall cannot restore shortcuts | Run removal from the Windows account that installed the addon. Keep the shortcut restoration records intact. |

For help, use [GitHub Discussions](https://github.com/AminBoua/Hytale-Account-Switcher/discussions) with your Windows version, addon release, official launcher version, exact error, and steps to reproduce. Include only a reviewed, relevant excerpt from `hytale-accounts.log`. For suspected vulnerabilities, follow [SECURITY.md](SECURITY.md) before sharing details.

## Build from source

Use Windows x64 with **Git**, the **.NET 10 SDK**, **Node.js**, and **Windows PowerShell 5.1** available. Run the following from a normal, unelevated terminal:

```powershell
git clone https://github.com/AminBoua/Hytale-Account-Switcher.git
cd Hytale-Account-Switcher

# Run the safe checks without packaging an installer.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1 -ChecksOnly

# Run the same checks and package a self-contained Windows x64 installer.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1
```

Choose the checks-only command or the packaging command for your task; packaging already runs the checks. It produces:

- `dist\HytaleAccounts-Setup.zip` — installer plus addon license and runtime notices; building does not install it.
- `dist\HytaleAccounts-Setup.exe` — the same installer as in the ZIP.
- `dist\LICENSE` and `dist\THIRD-PARTY-NOTICES.txt` — addon and exact bundled runtime license texts.
- `dist\SHA256SUMS.txt` — checksums for the ZIP, installer and license files.
- `dist\SECURITY-STATUS.json` — artifact hash and validation status.
- `dist\security\<run>\` — check report and logs.

The safe suite covers C# storage/session/bridge behavior, mocked frontend behavior, installer and shortcut fixtures, race checks, and evidence-gate validation. It does not use live Hytale accounts, request elevation, or prove live launcher compatibility. Its build step clears package sources, so missing local SDK/targeting prerequisites must be installed first. A direct `dotnet build` only compiles and does not substitute for the gate.

GitHub Actions runs the safe suite and regression controls under a disposable standard Windows user. Its administrator bootstrap only prepares that test account; it does not establish UAC or cross-user security validation. Packaging also extracts the notices from the runtime packs actually used by the build and fails if a required pack license is missing.

For regression controls and release packaging with matching VM evidence, follow [SECURITY-RELEASE.md](SECURITY-RELEASE.md). Ordinary builds can succeed with `SecurityValidated: false`; an installer build alone is not security validation. The [security re-audit](SECURITY-REAUDIT.md) records remaining limitations.

### Live release checks

With test accounts you can sign into again, verify installation through the existing shortcut; add account B while signed into A; switch back to A without a new browser login or launcher restart; log out A and confirm fallback to B; then log out B and confirm native sign-in in the same window. Sign in again, check the dropdown and Play, then uninstall through Windows Settings and confirm shortcut restoration and data retention. These steps perform real authentication and logout and are separate from the safe suite.

### Source map

| Code | Responsibility |
| --- | --- |
| [`Launcher/NativeSession.cs`](Launcher/NativeSession.cs), [`NativeStores.cs`](Launcher/NativeStores.cs) | Native session transitions and saved snapshots. |
| [`Launcher/NativeDropdown.js`](Launcher/NativeDropdown.js), [`LocalAccountBridge.cs`](Launcher/LocalAccountBridge.cs) | Dropdown integration and authenticated loopback communication. |
| [`Launcher/NativeAdapter.cs`](Launcher/NativeAdapter.cs), [`PatchInstaller.cs`](Launcher/PatchInstaller.cs), [`IntegrationSafety.cs`](Launcher/IntegrationSafety.cs) | Native adapter, runtime patching, and compatibility checks. |
| [`Launcher/Setup.cs`](Launcher/Setup.cs), [`Install.ps1`](Install.ps1), [`Uninstall.ps1`](Uninstall.ps1) | Setup and removal. |
| [`ShortcutIntegration.ps1`](ShortcutIntegration.ps1), [`Repair-Integration.ps1`](Repair-Integration.ps1) | Existing shortcut integration and background repair. |
| [`Checks/`](Checks/), [`Build-Installer.ps1`](Build-Installer.ps1) | Verification and installer packaging. |

## License and compatibility rights

Original addon code is available under the [MIT License](LICENSE), copyright 2026 AminBoua. Bundled runtime components retain their own licenses, supplied in `THIRD-PARTY-NOTICES.txt` with the installer. No Hytale executable or game files are distributed by this project.

The addon patches a local launcher copy and is not endorsed by Hypixel Studios. Hytale's [EULA](https://hytale.com/eula) covers the launcher, permits mods, and includes restrictions and an applicable-law interoperability exception. This project's MIT license grants no rights to Hytale's software; specific permission for this implementation has not been established.
