# Addon upgrade package investigation and final release procedure

Historical v0.1.4 investigation. The next candidate is v0.1.5; use [SETUP-RELEASE-REGRESSIONS.md](SETUP-RELEASE-REGRESSIONS.md) for current provenance and build/acceptance commands. Do not run the historical v0.1.4 publication commands below.

Investigation date: 2026-11-01 (Europe/Amsterdam). Packaging branch: `fix/addon-upgrade-package`, sibling worktree `Mod-hytale-launcher-upgrade-package`. Starting local `main`: `eb3bf60eb463eb6c8070bdc6101a919719eca682`. The original checkout was not edited. No remote writes or live installation were performed.

## Published bytes versus local source

Read-only `gh api repos/AminBoua/Hytale-Account-Switcher/releases` showed v0.1.3 as the newest published release (experimental prerelease, 2026-10-25T12:10:09Z); v0.1.0/v0.1.1 were drafts. `target_commitish: main` is not evidence of current-main contents. The annotated v0.1.3 tag resolves to `4a9254dea0c281d0bd8850d655c7d8391e2776c3`, matching its release notes.

Downloaded the actual [v0.1.3 ZIP](https://github.com/AminBoua/Hytale-Account-Switcher/releases/download/v0.1.3/HytaleAccounts-Setup.zip) and [checksum list](https://github.com/AminBoua/Hytale-Account-Switcher/releases/download/v0.1.3/SHA256SUMS.txt). The ZIP is 58,875,736 bytes, SHA-256 `d358319f9f7e9fbce1162b9ed7077568423fa3678ffa12d16e7ad1f3cd816c97`. Its extracted EXE is 139,859,240 bytes, SHA-256 `d39335d741434a305febcf68d2747937493ff0f0da29ac02a811f2560e41b4b1`. These match GitHub asset digests and SHA256SUMS.txt. The ZIP contains only that EXE, LICENSE and THIRD-PARTY-NOTICES.txt.

Static .NET bundle/PE inspection, without executing setup, found assembly version 0.1.3.0 and five native bundled entries. The seven embedded resources below match v0.1.3 source after CRLF/LF normalization; none match starting-main source after that normalization. Hashes below are the raw downloaded resource bytes, not normalized text:

| Resource | SHA-256 |
| --- | --- |
| Install.ps1 | `6e0853d13cda77a96aef12befb362ac295509f0d0aa3a6e3e1fd515bdfa494b4` |
| InstallerSafety.ps1 | `4d4eed19fde90adf545b154411ecc1258144ea20eebb4f25f226173b308623bf` |
| Uninstall.ps1 | `97a35a3dfc227df893935778e1e828fb0c77e66cda8d0beaa364879c67716376` |
| Repair-Integration.ps1 | `b14a74b8efcca7840832955d03fcfe65d8db2bd8f97fb263f4480a7d3101be10` |
| ShortcutIntegration.ps1 | `92dd7f20642cbe83cafcda8bf364c6a10e8926f4b662f501494bf7e56ce711d1` |
| ShortcutFile.cs | `90b8d2b91b45fe9f9b15458ca8c7b245228e3ca49a3df519ff634b696f0dec7b` |
| NativeDropdown.js | `d7b30c6b10e34e6adfcb0861a41232691671c072395e7595d42e687b47fce09d` |

The download has no ElevatedRuntime.ps1 resource. Its Install.ps1 writes payloads with `File.Write`; starting main uses staged `ReplaceFrom` publication and helper-last installation. Thus re-downloading v0.1.3 does **not** obtain main's installer implementation. The published ShortcutIntegration.ps1 also contains a three-argument `[IO.File]::Replace` when saving an existing shortcut journal. This is a possible matching call site, not a reproduced failure. This does not establish which file/operation caused a user's reported three-argument `Replace` failure; that requires the installer task's reproduction and fix. No replacement logic is changed here.

The old ZIP's lack of DLL sidecars is consistent with its self-extracting build; it is not a reason to change the newer packaging design. Starting main already disables ordinary native extraction and packages the five required WPF DLLs beside setup. The supported new download must be the **entire ZIP** (EXE + five DLLs + two license files). A standalone new EXE is incomplete; do not advertise it as an installer download. Static inspection establishes package composition, not successful real installation or live compatibility.

## Changes and boundaries

Prepare patch 0.1.4 in Launcher.csproj and Install.ps1's Windows Apps DisplayVersion, with its existing fixture expectation updated. Extend actual-artifact inspection to enforce project/assembly/file/product/Windows Apps version consistency and byte equality of the frontend resource as well as the seven existing script resources. Retain RUNTIME-PACKAGE.json in CI artifacts and name native sidecars in the build's ready message. No payload format, dependency, security gate or installer replacement logic is changed.

README upgrade guidance requires normal-user setup from the complete extracted package, using the same Windows account over the existing addon. Keep Hytale accounts/keys/game data, machine and user shortcut journals/backups and the existing Apps registration. No normal-upgrade uninstall, directory deletion or manual helper replacement is prescribed. Release notes explicitly leave the parallel fix and combined acceptance pending.

## Final integration procedure (commands below have NOT published anything)

First integrate the separate installer replacement fix and its broad upgrade regression checks. Review the final diff, commit all source changes, ensure the Windows safe checks status passes on the exact integrated revision, and record its full SHA. The commands below run from that clean integrated checkout; a fresh detached sibling prevents stale build artifacts. Do not substitute this packaging branch's artifact for the combined candidate.

```powershell
$ErrorActionPreference = 'Stop'
$releaseCommit = (git rev-parse HEAD).Trim()
if (git status --porcelain) { throw 'Release source must be clean.' }
git worktree add --detach ..\Mod-hytale-release-v0.1.4 $releaseCommit
if ($LASTEXITCODE) { throw 'Create fresh release worktree failed.' }
Set-Location ..\Mod-hytale-release-v0.1.4
if (Test-Path dist) { throw 'Expected fresh build directory.' }
dotnet --info
node --version
dotnet restore .\Checks\RuntimeExtraction\RuntimeExtraction.csproj --source https://api.nuget.org/v3/index.json -p:NuGetAudit=false
if ($LASTEXITCODE) { throw 'Prerequisite restore failed.' }
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\Build-Installer.ps1 -RegressionControls
if ($LASTEXITCODE) { throw 'Combined safe gate or packaging failed.' }

# Repeat actual-byte inspection; never start setup merely to inspect it.
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --runtime-package dist/HytaleAccounts-Setup.exe dist/HytaleAccounts-Setup.zip $PWD.Path
if ($LASTEXITCODE) { throw 'Artifact inspection failed.' }
Get-Content dist/RUNTIME-PACKAGE.json
Get-Content dist/SECURITY-STATUS.json
Get-Content dist/SHA256SUMS.txt
foreach ($line in Get-Content dist/SHA256SUMS.txt) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9_.-]+)$') { throw 'Malformed checksum line.' }
    $expected = $Matches[1]; $name = $Matches[2]
    if ((Get-FileHash -LiteralPath (Join-Path dist $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) { throw "Checksum mismatch: $name" }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $PWD 'dist/HytaleAccounts-Setup.zip'))
try { $zip.Entries | Select-Object FullName, Length } finally { $zip.Dispose() }
```

The exact eight ZIP entries must be HytaleAccounts-Setup.exe, D3DCompiler_47_cor3.dll, PenImc_cor3.dll, PresentationNative_cor3.dll, vcruntime140_cor3.dll, wpfgfx_cor3.dll, LICENSE and THIRD-PARTY-NOTICES.txt. The inspection enforces membership, publish/ZIP byte equality, current embedded source, versions, included Core/WPF runtimes and absence of native extraction entries. SHA256SUMS.txt has nine records: ZIP plus all eight entries.

Retain dist/RUNTIME-PACKAGE.json, dist/SECURITY-STATUS.json, checksums, exact artifacts and the complete dist/security/<run> report/log directory privately outside this ignored worktree. Record source SHA/fingerprint, SDK, report paths and hashes. Review diagnostics for local identities/paths before publishing excerpts. Do not commit generated binaries, reports or private diagnostics.

Run the existing authorized disposable-VM runbook and README live sequence, plus the installer task's real in-place upgrade acceptance from supported installed releases. Verify retained accounts/data and machine/user shortcut restoration records, the single existing Apps removal entry with 0.1.4, unchanged integrated shortcut/search behavior, and successful later removal/restoration in the disposable fixture. Record exact versions, hashes, actors and results; preserve recovery data after any failure. Host synthetic checks cannot supply this evidence.

For a security-validated claim, use the fresh matching actual VM export and repeat packaging with **all** controls and existing gates:

```powershell
# Replace this path only with a real, complete authorized VM export.
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\Build-Installer.ps1 -RegressionControls -SecurityValidated -VmEvidence C:\ReleaseEvidence\ModLauncherSecurityVM-Evidence-<id>
if ($LASTEXITCODE) { throw 'Validated release gate failed.' }
```

Reinspect/checksum and retain the resulting bytes after that build. Without matching actual evidence, keep SecurityValidated false and describe every unverified VM/live scenario; only an explicitly experimental prerelease is permitted by existing policy. Combined safe checks and installer upgrade validation must not be claimed until actually run. Update RELEASE-NOTES-v0.1.4.md with the exact integrated source, results and limitations, commit that documentation, and record the final SHA (documentation-only commits may reuse matching evidence under SECURITY-RELEASE.md). If any build input changes, rebuild and revalidate.

Only the final integration/release task, after review and authorization, should run these remote-write commands from the final clean release worktree. They are instructions, not actions taken in this investigation:

```powershell
$releaseCommit = (git rev-parse HEAD).Trim()
if (git status --porcelain) { throw 'Release source must be clean.' }
# Confirm the retained reports apply to this source fingerprint before proceeding.
git tag -a v0.1.4 $releaseCommit -m 'v0.1.4 experimental upgrade patch'
if ($LASTEXITCODE) { throw 'Tag creation failed; do not overwrite an existing tag.' }
git push origin refs/tags/v0.1.4
if ($LASTEXITCODE) { throw 'Tag push failed.' }
gh release create v0.1.4 --repo AminBoua/Hytale-Account-Switcher --verify-tag --prerelease --title 'v0.1.4 — experimental upgrade patch' --notes-file RELEASE-NOTES-v0.1.4.md dist/HytaleAccounts-Setup.zip dist/SHA256SUMS.txt dist/LICENSE dist/THIRD-PARTY-NOTICES.txt
if ($LASTEXITCODE) { throw 'Release creation failed; inspect remote state before retrying.' }
```

Do not upload a bare new setup EXE, broad `dist/*` globs or private evidence. After publication, independently download ZIP/checksums to a fresh directory, verify the ZIP and all extracted entries against SHA256SUMS.txt, and compare downloaded bytes to the retained candidate. Keep links on `/releases` because prereleases need not appear under `/releases/latest`.

## Local verification results

The fresh sibling worktree started without bin/obj/dist outputs. Prerequisite restore succeeded, then `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File .\Build-Installer.ps1 -RegressionControls` completed successfully: **34/34 gate stages** (17 positive stages and 17 negative controls rejected at their required assertions). Initial compilation had zero warnings/errors. Environment: Windows 11 x64/NTFS, .NET SDK 10.0.203, Node v22.21.1, Windows PowerShell 5.1.26100.9818. No actual VM evidence was supplied; SecurityValidated is false.

The gate tested the dirty working source based on the recorded starting commit, fingerprint `ee135fb68cc77a55265fccbfea55e03b934a968de8ac3734f56aa97c511c35b4`; this is evidence for those exact bytes, not proof that the unmodified base passed these additions. The final documentation/commit does not change the tested build inputs. Retained local evidence (ignored, not committed):

- `dist/security/20261101-125344-3885bee9ba0e47f182b7b47b3d8207a5/report.json`, SHA-256 `44a8ea18173efa2107a54c4e416113098d003dbaeb7e49940a2951dc65d572d1` and its full per-stage logs.
- `dist/RUNTIME-PACKAGE.json`: actual self-contained Core/WPF bundle, zero native bundle entries, all eight current resource bytes, version 0.1.4, and exact eight-entry ZIP/sidecar equality **passed** without executing setup.
- `dist/SECURITY-STATUS.json` and `dist/SHA256SUMS.txt`: all nine checksum records independently verified.
- Candidate EXE SHA-256: `248d59a01773f1db60571805294050f3b667d9ca40898894073905cb51785165`.
- Candidate ZIP SHA-256: `bd41e1f1edda979f3cb88055240ffe3e2d6e19e1a91fe6405f7bcb5039ac98a5`.

Six additional isolated inspector runs accepted the exact package/source twice and rejected four disposable mutations at their intended assertions: project/assembly version mismatch, Windows Apps version mismatch, stale embedded frontend and missing native DLL in the ZIP. Candidate/source bytes were not modified. Published v0.1.3 ZIP and all three extracted files also matched their four published checksum records. `git diff --check` passed. Graphify was extracted, queried before exploration, and updated after code changes; generated graph files remain ignored.

These results do not validate the separate installer replacement fix, actual UAC/cross-user enforcement, real in-place upgrades, live login/switch/logout/Play or a future integrated artifact. The draft release notes deliberately retain **pending combined validation**. No push, merge, upload or release publication was performed.
