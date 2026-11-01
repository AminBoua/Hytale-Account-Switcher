# Setup and release regressions

2026-11-01, Europe/Amsterdam. Branch `fix/setup-release-regressions`, sibling worktree `C:\Users\moham\Documents\ChatGPT\Mod-hytale-launcher-setup-release-regressions`, starting local main **`4578eb31f6ab5a73a803a0bf97f010339de27111`**. Original checkout was clean at entry and was not edited. No installer was run against live Hytale; no live data/permissions were inspected or changed by this task. No merge, push, tag, release, upload, branch deletion or worktree removal was performed.

## Scope and coordination

This branch owns broad checks, package inspection, version metadata and guidance. Its only production metadata changes are launcher `<Version>` and Windows Apps `DisplayVersion`, both **0.1.5**. No installer replacement/handle implementation or storage permission policy is changed. Existing UI, official native authentication/storage, default browser/profile and same-process account transitions remain constraints.

Reached callers/fixtures: `Build-Installer.ps1`, actual-byte `RuntimePackageChecks`, the existing setup worker lifecycle runner, installer script version expectation and VM harness package builder. Documentation points to the new pending notes. No dependencies or parallel fixture framework were added.

The two focused tasks must be integrated by the final integration chat, using their final reports rather than reimplementing their focused tests:

- `fix/setup-file-sharing`, final commit `bd743be65c87cf8fded24cd6c87e5a5c53593d95` (code commit `d4ae7b7865e5efe1f0123ddb6b2642d49a5d53ab`): `SETUP-FILE-SHARING.md` reproduces the exact constructor exception with a seeded setup-owned `OpenSource` reader and a separate hidden PowerShell holder. It improves path/native exception/caller/PID diagnostics; it does **not** establish a production handle leak or fix the original lock. Fresh/upgrade/reinstall, obstruction recovery, races and existing negative control pass in its fixtures. The original failing handle pair and genuine packaged reproduction remain blocked. This branch does not duplicate those focused probes or treat that task's candidate inspection as proof of the failing executable's identity.
- `fix/storage-permission-rejection`, final commit `b2975faa78bce29e8249196df2e6e228860dd919`: `STORAGE-PERMISSION-REJECTION.md` concludes that explicit BUILTIN\Users Full Control correctly causes rejection. It adds privacy-safe diagnostics and synthetic descriptor/on-disk/Authz controls without changing policy. Its focused storage/ACL/migration/race checks and all five mutation controls passed. Broad tests reuse those suites after integration; this branch does not duplicate that matrix or perform its proposed live remediation. The unsafe grant remains a startup blocker, and its historical origin is unproven. A patch must not promise to accept that ACL.

## Rechecked artifact provenance

Read-only GitHub releases API (including drafts) and `git ls-remote --tags origin` were checked on the date above. The API connector does not support the tags collection, so tag inspection used Git's read-only remote listing. Tags are v0.1.0–v0.1.3 only; v0.1.3 is annotated and resolves to `4a9254dea0c281d0bd8850d655c7d8391e2776c3`. No v0.1.4 or v0.1.5 tag/release exists in the observed state. v0.1.0/v0.1.1 releases are drafts; v0.1.2/v0.1.3 are published experimental prereleases. Policy in `SECURITY-RELEASE.md` still requires matching actual VM/live evidence for a security-validated claim and otherwise permits only experimental prereleases. Genuine packaged upgrade acceptance remains an additional publication blocker from `INTEGRATION-UPGRADE.md`.

The latest published [v0.1.3 release](https://github.com/AminBoua/Hytale-Account-Switcher/releases/tag/v0.1.3), ID `423378253`, was published `2026-10-25T12:10:09Z`. GitHub metadata lists:

| Asset | Bytes | GitHub SHA-256 |
| --- | ---: | --- |
| HytaleAccounts-Setup.exe | 139859240 | `d39335d741434a305febcf68d2747937493ff0f0da29ac02a811f2560e41b4b1` |
| HytaleAccounts-Setup.zip | 58875736 | `d358319f9f7e9fbce1162b9ed7077568423fa3678ffa12d16e7ad1f3cd816c97` |

The release also lists LICENSE, THIRD-PARTY-NOTICES.txt and SHA256SUMS.txt. `target_commitish: main` does not prove present main produced a download. Those remote binaries were not downloaded/executed in this task; the earlier actual-v0.1.3 inspection is historical evidence in `UPGRADE-RELEASE.md`.

Both local retained v0.1.4 candidates exist in the original checkout's ignored `dist`:

| Directory | ProductVersion | EXE SHA-256 | ZIP SHA-256 |
| --- | --- | --- | --- |
| release-v0.1.4 | `0.1.4+4578eb31f6ab5a73a803a0bf97f010339de27111` | `8d1a73fdfa6ddd0b27c2bdb45a4b7d0339fea6c67f291a4f6fba83f21fbbe326` | `7f967603e8fa0e1baff09990e04b45c3b000b33f7a552c72b5f5772c65e45a17` |
| release-v0.1.4-initial | `0.1.4+8cd6877776c613addcca480736fd4aaa63b3de37` | `4743bf1d4e78dc17203e056f42de3aebe8bd64d634d31a83942012dc05de3c1c` | `c1f9672d2bba780965b99d4645614a10089fbe5eb3b7666ca0480c0b2d017831` |

Both EXEs are 131675201 bytes, FileVersion 0.1.4.0, Authenticode `NotSigned`. Their ZIPs are respectively 58880972 and 58881056 bytes. Neither EXE was executed or loaded as an assembly. The trusted checks executable parsed bundle/PE/resource bytes. Both pass current static inspection against unchanged starting-main source (before the 0.1.5 bump), including all eight exact embedded resources, managed Core/WPF runtime configuration, zero native bundle entries, five DLL sidecars matching publish output and exact eight ZIP members/bytes. No descriptor normalization was used. Both have identical embedded resource hashes:

| Resource (HytaleAccounts prefix) | Raw SHA-256 |
| --- | --- |
| ElevatedRuntime.ps1 | `e0e3d4f9009cece0d6899cb76aee987006d94a768a7b2e5eb4148f87006ae6e4` |
| Install.ps1 | `1f1537f1c50284a9a00c2d5184ecb0cfcb09950448049a89d7665f5e518e0de5` |
| Uninstall.ps1 | `0b224557bae7af66a3c23b909c3c7f012053b885b2b9498fdc7dddf29290170d` |
| InstallerSafety.ps1 | `60fd1c1d1eea5a889ac965701405034e4556b5f007c9912adb313e8c18d4b430` |
| Repair-Integration.ps1 | `c4198648ba3647804d91fba67d2417f22bce4360093af70d62d67991a5b7089c` |
| ShortcutIntegration.ps1 | `23d6dcac6ccc89c2bfe8cede39a477001d985793281f417b981d55bd2275e2f5` |
| NativeDropdown.js | `fde04c407aa224cc749c259b445f102370a59c15e491e1e4d15c211367405407` |
| ShortcutFile.cs | `56890fe6a2e152c09689bfe02c426c1b8fb2bd359372e62e82cfc432ddf04cf5` |

All nine records in the corrected candidate's SHA256SUMS.txt were independently rehashed and match. Required sidecars are `D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `PresentationNative_cor3.dll`, `vcruntime140_cor3.dll`, `wpfgfx_cor3.dll`, LICENSE and THIRD-PARTY-NOTICES.txt. A standalone new EXE is incomplete. Its retained SECURITY-STATUS source revision is the base above, Dirty false, fingerprint `C5AAA2CF54BCF1CCB2003F997977C70190992D370B1986125DFD62621D2DE2BC`. The copied safe report's hash matches `1D9D0D7D8F1992A436145CD06194A82D74082C7C05033F0B94CC91DC07AE4006`; SafeStatus passed and SecurityValidated false, with no actual VM evidence. The status contains the historical build-worktree report path; the retained copy is under `dist/release-v0.1.4/security/20261101-133355-444a4194fe6341baac79b2cfd3210218/report.json`.

The original root `dist/HytaleAccounts-Setup.exe` is a third distinct stale artifact: FileVersion 0.1.3.0, ProductVersion `0.1.3+eb3bf60eb463eb6c8070bdc6101a919719eca682`, SHA-256 `d096c7aac3e0bb8473563178d75f64714dfac06b3d3fee2fbea6a35f764ae2ec`; it is neither retained v0.1.4 nor GitHub v0.1.3 by hash.

Raw source-fingerprint caveat: the unchanged original checkout currently hashes to `B20B21353DA8FE0DC26A0B753E2D55D6F1F3AC85D8F4FC8E73B5C3F1E688E82F`, different from the candidate's retained clean-build fingerprint. All 20 differing recorded source-file hashes match when those original-checkout files are converted to CRLF in memory; Git has `core.autocrlf=true`. This explains the observed byte differences without modifying files or relaxing raw-byte validation. The embedded resource comparisons above are exact, and the gate still binds evidence to raw source fingerprints. Do not present the historical report as evidence for a different current fingerprint.

Unestablished provenance: the failing executable's path/hash, its sidecar set, source revision and download/transfer history were not supplied. Version text alone cannot identify it; neither local artifact equality with that executable nor historical ACL provenance is established. Unsigned local metadata and self-reported JSON are corroborating records, not publisher attestation. Pushing main never updates an existing release asset. Preserve the failing package and calculate its hash without executing it before attributing either failure to a candidate.

## Coverage added and reused

`SetupUpgradeChecks` retains the existing Windows-desktop/Server fixture initialization (`SetAccessRuleProtection(true, true)` on the fixture launcher directory before file creation). Exact payload owner/group/DACL comparison, including the protected-DACL control, stays intact. Added exact descriptors for every account/key/game/world/credential sentinel file and directory, original shortcut backups, journals and integrated shortcuts. Lock rejection now compares all Apps registration value names, kinds and values; same-package reinstall compares the entire registration again. Existing checks verify fresh registration, changed helper/script/runtime bytes, retry, staging cleanup and official launcher bytes/ACLs. Fixture “0.1.1 → 0.1.2” labels represent synthetic changed payloads, not real released packages.

`RuntimePackageChecks` now enforces exact ZIP name multiplicity (rejecting duplicate members) and records file/product versions, archive hash and raw resource/sidecar hashes. New `RuntimePackageRegressionChecks.ps1` reuses the built inspector and disposable source/ZIP copies: good baseline, wrong project version, wrong Apps version, stale embedded frontend source, missing sidecar, altered sidecar, duplicate ZIP member, then good baseline again. `Build-Installer.ps1 -RegressionControls` invokes it after static inspection. None of these controls executes an installer.

Existing shared suites remain responsible for partial publication/rollback, killed upgrades, retry, journals/backups, task ownership, removal, links and races. The setup worker keeps actual source/destination/staging pins over a real hidden Windows PowerShell 5.1 process; the parent preflight intercepts UAC. The focused setup report must supply additional real parent/worker handle findings. Existing storage tests cover legitimate current-user/SYSTEM/Administrators/read-only group ACLs and unsafe foreign mutation, null DACLs, ownership, deny ordering, junctions/hard links, write/delete-share conflicts and retry. The storage task supplies its extended observed-shape legitimate/unsafe matrix. No negative controls were removed and no real difference was hidden by descriptor normalization.

Integration must retain both tasks' additions to `SetupUpgradeChecks.cs`, this branch's exact descriptor/registration assertions, and the 0.1.5 fixture boundary. Do not resolve the overlap by dropping either coverage set. Per the focused setup report, its new diagnostic assertions are **red on original production code** (missing path/native cause/caller) and green only with that branch's diagnostic changes. They remain red if the tests alone are integrated here. This branch's existing/extended broad lifecycle is green and does not naturally reproduce the user's unexplained lock; the original failure remains unresolved, not a concealed passing regression. Storage's unsafe-ACL rejection remains an intentional negative result even after integration.

## Local validation

Final `Build-Installer.ps1 -RegressionControls`: **exit 0**, **35/35 safe stages** (18 positive stages, all 17 deliberately failing controls caught), static inspection passed, **eight package-control invocations passed** (two baselines and six intended rejections), and all **nine checksum records independently rehashed successfully**. No unexpected broad regression remains red in this branch. The focused setup lifecycle and two consecutive VM package host validations also passed. Graphify update and staged whitespace checks passed.

Authoritative report: `dist/security/20261101-143234-c98fda3d6cc141bcbc265c16b8158785/report.json`; SHA-256 **`50016F39731C7D8AAC587A18818728860F6022465ECDB5AEE8910D19349FEE62`**. Source fingerprint **`A27402F129252E860857EC141C9A2E50FCC2A90F57215BB3C5DD8B47FB6A6553`**; gate ran `2026-11-01T14:32:34Z`–`2026-11-01T14:44:35Z`. Full build output is retained in ignored `release-regression-accepted-stdout.log` / `release-regression-accepted-stderr.log`. Source revision is the starting base with Dirty true: evidence binds these working-source bytes, not a clean final integrated release. Documentation-only commits do not change that build-input fingerprint.

The branch-only unpublished v0.1.5 package is retained under this worktree's ignored `dist`, with FileVersion **0.1.5.0**, ProductVersion **`0.1.5+4578eb31f6ab5a73a803a0bf97f010339de27111`**, EXE SHA-256 **`0644a47b975eed7fe62972719c010ab53c480c29d4d6989f78e9e9ffd0722b57`**, ZIP SHA-256 **`5e1327e0a4b5e3fdc4750582b91f0a85ce4586cb4e8c8f2f75ff502b9a942055`**. RUNTIME-PACKAGE.json records all eight resource hashes, seven sidecar hashes and exact ZIP entries. This artifact does not include the two separate diagnostic branches and must not replace the final integrated build.

Environment: unelevated Windows 11 Home x64/NTFS, .NET SDK 10.0.203, Node v22.21.1, Windows PowerShell **5.1.26100.9818**. No Windows Server, actual UAC/cross-user, genuine packaged older-release upgrade, real Settings removal or live account/game validation was performed. SecurityValidated is **false**. The setup task's diagnostic regressions require its production diagnostic changes on integration.

Earlier runs are retained as history, not final validation:

| Run under dist/security | Result and resolution |
| --- | --- |
| `20261101-135819-24d121885acb43a39ef4b95f4c890b3e` | Safe 35/35 at fingerprint `9960D431641BA8F7DE17C33FF569E18E739E0BC086DD85EF4C7E850F04009071`; packaging harness then missed a wrapped stderr assertion. Collecting raw ToString lines fixed matching; package assertions stayed exact. |
| `20261101-141206-294652793d31492fb680326e1274ed38` | Interrupted during killed-upgrade fixture; PowerShell parents disappeared, leaving its deliberate paused child. No completed report; not a pass. Only the verified orphan child was stopped. |
| `20261101-141843-31c3aa0d727b43d68fda8e1a6bfbb58a` | 34/35, fingerprint `84EF15202764DC11D9F92E72580AA7C313A271148A5BF4BDD854FFCBA6267AA0`; unchanged VM package assertion caught missing D3DCompiler_47_cor3.dll. The reused probe publish directory contained only EXE/PDB. |

The real harness package regression is fixed in `Checks/VmValidation/Build.ps1`: publish the harmless probe directly into each fresh package with debug symbols disabled, removing the reused intermediate directory/copy step. Two consecutive existing `Validate.ps1` runs and the final full gate verify sidecar/hash/provenance/guard assertions. No missing-sidecar check was suppressed. The observable build launch supplies Windows PowerShell's protected system module directory, retains its process handle and captures stdout/stderr directly; an initial launch without that module setting failed before tests. No launcher profile variables or production behavior were changed by that invocation adjustment.

Interrupted-fixture evidence is copied under ignored `dist/interrupted-installer-fixture` and `dist/interrupted-installer-registration.json` (value names, types and values). Automatic approval review rejected its cleanup command with only “blocked by policy”; original `%TEMP%\addon-485c4f01a0e64c5588aa36493b4bf114-José` and unique synthetic marker `HKCU:\Software\HytaleAccounts-Check-485c4f01a0e64c5588aa36493b4bf114` remain preserved. These are test state, not live Hytale or the actual Windows Apps uninstall key. No cleanup bypass was attempted.

## Final integrated build and acceptance commands

Only the final integration chat may integrate/publish. First read both focused final reports, resolve fixture version boundaries to 0.1.5 and commit the combined source. Recheck tag/releases immediately before finalizing version; if 0.1.5 has appeared, choose a fresh patch and update project, Apps, fixture boundaries and pending notes together. Never overwrite/reuse a published version. This branch reserves 0.1.5 even though local 0.1.4 was unpublished, avoiding confusion between the two retained candidates and the reported failure.

Run from the clean integrated checkout, unelevated:

```powershell
$ErrorActionPreference = 'Stop'
$releaseCommit = (git rev-parse HEAD).Trim()
if (git status --porcelain) { throw 'Commit integrated source first.' }
git ls-remote --tags origin
if ($LASTEXITCODE) { throw 'Remote tag read failed.' }
gh api repos/AminBoua/Hytale-Account-Switcher/releases --paginate --jq '.[] | {tag_name,draft,prerelease,target_commitish,published_at}'
if ($LASTEXITCODE) { throw 'Release metadata read failed.' }
# Review the output: stop if v0.1.5 already exists. Never force a tag/release.
git worktree add --detach ..\Mod-hytale-release-v0.1.5 $releaseCommit
if ($LASTEXITCODE) { throw 'Fresh release worktree creation failed.' }
Set-Location ..\Mod-hytale-release-v0.1.5
if (Test-Path dist) { throw 'Expected a fresh dist directory.' }
dotnet --info
node --version
dotnet restore Checks/RuntimeExtraction/RuntimeExtraction.csproj --source https://api.nuget.org/v3/index.json -p:NuGetAudit=false
if ($LASTEXITCODE) { throw 'Runtime prerequisites failed.' }
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Build-Installer.ps1 -RegressionControls
if ($LASTEXITCODE) { throw 'Integrated gate/build/package controls failed.' }
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --runtime-package dist/HytaleAccounts-Setup.exe dist/HytaleAccounts-Setup.zip $PWD.Path
if ($LASTEXITCODE) { throw 'Actual-byte inspection failed.' }
Get-Content dist/RUNTIME-PACKAGE.json
Get-Content dist/SECURITY-STATUS.json
if (@(Get-Content dist/SHA256SUMS.txt).Count -ne 9) { throw 'Expected all nine checksums.' }
foreach ($line in Get-Content dist/SHA256SUMS.txt) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9_.-]+)$') { throw 'Malformed checksum record.' }
    $expected = $Matches[1]; $name = $Matches[2]
    if ((Get-FileHash -LiteralPath (Join-Path dist $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) { throw "Checksum mismatch: $name" }
}
```

Retain exact EXE/ZIP/all sidecars, checksums, RUNTIME-PACKAGE.json, SECURITY-STATUS.json, full gate report/logs and source/environment fingerprints privately outside ignored build output. Verify the eight ZIP members are the EXE plus seven sidecars above. The build runs source fixtures and static package controls; it does not perform the following acceptance.

Require the repository's Windows safe checks status on the exact integrated revision under the existing policy. This local branch has not been pushed and supplies no hosted Windows Server CI result; the fixture initialization and exact descriptor assertions were retained for that later run.

Genuine packaged acceptance requires a separately authorized disposable Windows environment, checkpointed before each case, with no personal accounts/data. Obtain an older genuine published package (e.g. v0.1.3 ZIP, whose expected digest is above) and its checksum list using read-only download commands in that disposable environment:

```powershell
# Disposable acceptance environment only; use new empty directories.
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path C:\Acceptance\v0.1.3 | Out-Null
gh release download v0.1.3 --repo AminBoua/Hytale-Account-Switcher --pattern HytaleAccounts-Setup.zip --pattern SHA256SUMS.txt --dir C:\Acceptance\v0.1.3
if ($LASTEXITCODE) { throw 'Older package download failed.' }
if ((Get-FileHash C:\Acceptance\v0.1.3\HytaleAccounts-Setup.zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne 'd358319f9f7e9fbce1162b9ed7077568423fa3678ffa12d16e7ad1f3cd816c97') { throw 'Older ZIP differs from observed published asset.' }
Expand-Archive -LiteralPath C:\Acceptance\v0.1.3\HytaleAccounts-Setup.zip -DestinationPath C:\Acceptance\v0.1.3\expanded
# Transfer the retained final ZIP, seven sidecars, EXE and checksums into
# C:\Acceptance\v0.1.5 before continuing; do not substitute another build.
$candidateDirectory = 'C:\Acceptance\v0.1.5'
if (@(Get-Content "$candidateDirectory\SHA256SUMS.txt").Count -ne 9) { throw 'Expected nine candidate records.' }
foreach ($line in Get-Content "$candidateDirectory\SHA256SUMS.txt") {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9_.-]+)$') { throw 'Malformed candidate checksum record.' }
    $expected = $Matches[1]; $name = $Matches[2]
    if ((Get-FileHash -LiteralPath (Join-Path $candidateDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected) { throw "Candidate checksum mismatch: $name" }
}
Expand-Archive -LiteralPath C:\Acceptance\v0.1.5\HytaleAccounts-Setup.zip -DestinationPath C:\Acceptance\v0.1.5\expanded
```

Manually install the official compatible Hytale launcher in that disposable environment with approved test accounts. Snapshot exact official bytes/owner/group/DACL, native session/key/game sentinels, both shortcut journals/original backups and Apps registration. From an ordinary user session run `C:\Acceptance\v0.1.3\expanded\HytaleAccounts-Setup.exe`, approve the real UAC prompt, and establish the genuine older installation. Save its package hashes and installed-state snapshot. Then run `C:\Acceptance\v0.1.5\expanded\HytaleAccounts-Setup.exe` normally as the same user and approve UAC. This executes known, verified packages only in the designated acceptance environment, never on this user's installation.

Record this matrix; a passing source fixture is not a packaged acceptance result:

| Case | Required observation |
| --- | --- |
| Fresh v0.1.5 | Real parent, UAC worker and PowerShell succeed; original launcher/shortcuts/search stay integrated, single Apps entry at 0.1.5 |
| Genuine older → v0.1.5 | Unchanged original official bytes/ACL, all session/key/game sentinels, both journals/original backups; complete payload/sidecars and updated existing Apps entry |
| Sharing violation/retry | Hold an exclusive handle on an identified disposable installed payload, run candidate, retain error/state evidence, release handle and retry; preserve old payload/ACLs/registration/recovery on rejection |
| Same-package reinstall | Run the identical retained candidate again; exact payload/recovery/official/data ACL preservation and stable Apps registration; no leftover staging |
| Legitimate/unsafe ACL | Legitimate read-only broad grants operate; unsafe mutation denies before alteration, with diagnostic and files/ACLs preserved; no policy bypass or automatic broad repair |
| Removal | Use Windows Settings > Apps; restore exact originals, keep Hytale data, remove addon entry without a second launcher/uninstall shortcut |

Use a hidden test helper or a disposable PowerShell console's `[IO.File]::Open($fixturePayload, 'Open', 'Read', 'None')` to hold only the identified fixture payload; dispose it before retry. Record exact path, actors, PIDs, masks, Windows errors and before/after descriptors. The focused report determines which additional source/staging/parent handles must be probed. Do not infer a specific `.ctor` call site from exception text alone. No automated command here bypasses UAC, redirects a real launcher profile or repairs live ACLs.

Actual elevated/cross-user security validation is separate again: follow `Checks/VmValidation/README.md` on an explicitly designated disposable VM with a fresh matching package/export. That harness does not run the real installer and cannot replace genuine packaged upgrade acceptance. After a valid matching export, rerun the final build with `-RegressionControls -SecurityValidated -VmEvidence C:\ReleaseEvidence\<actual-export>`, then repeat actual-byte inspection/checksums and acceptance on the resulting bytes. Without that export keep SecurityValidated false. Live login/add/switch/logout/Play acceptance requires approved test accounts and the README sequence, preserving the existing process/window and normal default browser/profile.

Publication remains blocked until focused integration and genuine packaged upgrade acceptance pass. Before any later authorized release, update `RELEASE-NOTES-v0.1.5.md` with exact integrated commit, package hashes, checks and limits, then preserve the evidence. This task provides no publish/push/upload commands and performs none of those actions.
