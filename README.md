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

You need **Windows x64**, the official Hytale Launcher installed at the path below, and an existing Hytale Launcher shortcut. Setup requests administrator approval; everyday launcher use runs as your normal Windows user. The download includes its .NET runtime and five native WPF DLLs beside setup. Keep all extracted ZIP files together; copying the EXE alone is unsupported.

Open setup normally. Its UAC request starts a hidden **Windows PowerShell** bootstrap, which prepares a protected runtime directory before starting the installer worker. No separate runtime installation is required. Do not use **Run as administrator** on the downloaded executable.

Before requesting administrator approval, setup checks the current user's and common Desktop and Start Menu locations for an eligible existing shortcut. With none, it asks you to reinstall the official launcher and leaves addon files, Windows Apps registration, repair tasks and shortcuts unchanged. Shortcut eligibility and journal recovery use the same discovery rules as installation; setup does not create a replacement shortcut.

```text
C:\Program Files\Hypixel Studios\Hytale Launcher\hytale-launcher.exe
```

1. Close Hytale and the Hytale Launcher.
2. Open [Releases](https://github.com/AminBoua/Hytale-Account-Switcher/releases), read the release notes, and download **HytaleAccounts-Setup.zip** and **SHA256SUMS.txt** from the same release. The setup ZIP includes the installer, native runtime sidecars and its license notices; GitHub's source archives are for developers. Older packages may provide the `.exe` separately; current packages require the entire extracted ZIP directory.
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

## Upgrade an existing addon

From the same Windows account that installed the addon, close Hytale and its launcher, verify and extract the **complete ZIP** for the intended release into a separate download folder, then run its setup normally and approve UAC. Install over the existing addon; keep using the existing Hytale Launcher shortcut/search entry and Windows Settings → Apps removal entry.

Keep `.hytale-accounts-addon`, its `shortcuts.json` and backups, `%LOCALAPPDATA%\HytaleAccounts\ShortcutIntegration`, and all Hytale account/game data intact. These hold shortcut restoration records, saved accounts and native keys; do not delete the addon directory or uninstall as a routine upgrade step. Do not copy the new helper EXE into the installation manually. Setup updates the existing Apps registration and owned payload while retaining recovery records.

If setup reports a sharing violation (`.ctor` / "being used by another process" or `Replace` / "Unable to remove the file to be replaced"), close the game/launcher and any process holding the named file, retain recovery files, and report the package checksum, version and exact error if retry still fails. If the launcher reports unsafe mutation permissions, retain files and permissions for diagnosis; do not grant broader access or delete Hytale storage. Re-downloading the same release does not obtain newer `main` code. **v0.1.5 is pending integration and packaged acceptance**; the retained local v0.1.4 candidates are unpublished. See the [regression and release report](SETUP-RELEASE-REGRESSIONS.md).

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

An elevated Settings entry hands removal back to the same user's unelevated desktop before restoring user shortcuts. If that user context cannot be confirmed (including another administrator's credentials), removal stops and retains recovery files. Cancelling the removal UAC prompt restores shortcut integration so you can retry.

For an existing **0.1.5** installation with the older uninstaller, first install a newly built package containing this fix over the addon, from the same Windows account. Setup replaces the installed helper used by the existing Apps entry. The version number alone does not identify the fix; downloading the old 0.1.5 package again will not update it.

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
| Duplicate shortcut restoration identity | Installation, repair and removal stop before changing the journal or installed files. Keep `shortcuts.json` and every backup in the affected restoration directory; have the records reconciled before retrying. Even identical backups are retained rather than selecting a record by order or timestamp. |

For help, use [GitHub Discussions](https://github.com/AminBoua/Hytale-Account-Switcher/discussions) with your Windows version, addon release, official launcher version, exact error, and steps to reproduce. Include only a reviewed, relevant excerpt from `hytale-accounts.log`. For suspected vulnerabilities, follow [SECURITY.md](SECURITY.md) before sharing details.

## Build from source

Use Windows x64 with **Git**, the **.NET 10 SDK**, **Node.js 22** (as used in CI), and **Windows PowerShell 5.1** available. Run the following from a normal, unelevated terminal:

```powershell
git clone https://github.com/AminBoua/Hytale-Account-Switcher.git
cd Hytale-Account-Switcher

# Prepare this user's package cache before running the offline checks.
dotnet restore .\Checks\RuntimeExtraction\RuntimeExtraction.csproj --source https://api.nuget.org/v3/index.json -p:NuGetAudit=false

# Run the safe checks without packaging an installer.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1 -ChecksOnly

# Run the same checks and package a self-contained Windows x64 installer.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1
```

Choose the checks-only command or the packaging command for your task; packaging already runs the checks. It produces:

- `dist\HytaleAccounts-Setup.zip` — installer, five native DLL sidecars, addon license and runtime notices; building does not install it.
- `dist\HytaleAccounts-Setup.exe` — the same installer as in the ZIP; its five native DLL sidecars are required beside it.
- `dist\LICENSE` and `dist\THIRD-PARTY-NOTICES.txt` — addon and exact bundled runtime license texts.
- `dist\SHA256SUMS.txt` — checksums for the ZIP, installer, native sidecars and license files.
- `dist\RUNTIME-PACKAGE.json` — inspection of actual embedded source, versions, native sidecars and ZIP bytes.
- `dist\SECURITY-STATUS.json` — artifact hash and validation status.
- `dist\security\<run>\` — check report and logs.

Initial preparation needs access to nuget.org to download the self-contained `win-x64` WPF probe's runtime packs and single-file build tools. Use the same Windows user, selected SDK and package cache for restore and checks; repeat preparation after an SDK change or when using a fresh cache. The offline checks clear package sources and fail closed if a prerequisite is missing. For `NU1101` (package not found), rerun the restore command above with package access, confirm it succeeds, then retry `Build-Installer.ps1`. Do not elevate, add online fallback to the checks, or weaken them.

The safe suite covers C# storage/session/bridge behavior, mocked frontend behavior, installer and shortcut fixtures, substitute PowerShell module rejection, race checks, and evidence-gate validation. It does not use live Hytale accounts, request elevation, or prove live launcher compatibility. A direct `dotnet build` only compiles and does not substitute for the gate.

`Checks/InstallerScriptChecks.ps1` covers missing and unrelated shortcuts, each common/user shortcut location alone, read-only upgrade backups, and ordinary fixture installation/removal with disposable files, an HKCU registry entry and mocked repair tasks. Its upgrade regressions install, rename, retarget to the official launcher, upgrade repeatedly, rename again and remove in both scopes, including repair before upgrade and unrelated replacements. Existing duplicate identities, with identical or conflicting backups, must reject preflight, upgrade, repair and removal while preserving recovery data and mocked tasks. `Checks/ShortcutRaceChecks.ps1` verifies that installation and repair match renamed identities and write through the same held file. `Checks/SetupChecks.cs` checks the hidden, unelevated preflight process configuration. `Checks/SetupUpgradeRegressionChecks.ps1` exercises the real setup parent with elevation intercepted, checking prerequisite failures before legacy cleanup and UAC and cleanup of temporary staging.

Ordinary setup and installed-helper starts use a self-contained managed bundle with native WPF DLLs beside the executable. No native files are extracted to a default or inherited cache. Setup pins every sidecar and both license files across UAC, stages them with the existing protected payload, and installs them before publishing the helper. Missing, linked or foreign-writable setup payloads fail closed. The hidden elevated bootstrap retains its separate protected cache policy as defense in depth. No supporting startup UI or preinstalled .NET runtime is added.

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
