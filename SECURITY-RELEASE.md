# Security release gate

**Current assurance: safe synthetic checks only; actual elevated/cross-user VM validation is UNVERIFIED.** A normal build may produce a development artifact with that status. It cannot be described as security-validated until the gate accepts successful matching VM evidence. This is validation of the documented test matrix, not a security certification or proof of live Hytale compatibility.

## Routine builds

Run unelevated on Windows x64 with Git, .NET 10 SDK, Node.js and Windows PowerShell 5.1:

```powershell
# Safe checks only; no installer packaging, elevation, live accounts or external services.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1 -ChecksOnly

# Same checks followed by local installer packaging (does not distribute a release).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1

# Audit/release-candidate run: also prove that deliberately broken copies fail.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1 -ChecksOnly -RegressionControls
```

`Build-Installer.ps1` is the repository's build/release entry point. All its paths run the safe gate first. A direct `dotnet build` is compilation only and supplies no security validation. There is no skip-checks build option. Safe checks refuse an elevated token; never elevate to fix a failure.

## Native runtime and elevation startup

The Windows x64 installer remains a self-contained single-file .NET/WPF executable, with native libraries embedded for extraction and runtime license notices in the ZIP. The installed helper and Windows profile behavior are unchanged. Removing native self-extraction without distributing those libraries would silently introduce a runtime dependency and is not this fix.

.NET extracts bundled native files before application startup; setting an extraction variable in `App.OnStartup` or `Setup.RunElevated` cannot protect that boundary. [Microsoft documents the extraction order and directory-permission requirement](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview). The audited host's Temp cache permits additional principals to modify files, so setup must not elevate apphost with that cache.

`Setup.ElevationStart` now elevates the absolute system Windows PowerShell executable with an encoded command containing the embedded `Launcher/ElevatedRuntime.ps1` resource and literal quoted arguments. No loose bootstrap script crosses UAC. The existing executable/ancestor pins remain held across the bootstrap and worker lifetime, and the worker retains its existing source, payload, link, ACL and installation checks. PowerShell module-search hardening and shortcut changes belong to separate branches.

Before starting apphost, the bootstrap checks the Program Files directory and its ancestors for links, trusted ownership and lower-privilege mutation rights. It creates a fresh unpredictable `Program Files\HytaleAccounts-runtime-<guid>` directory with a protected, inheritable DACL granting mutation only to Administrators/SYSTEM, with Administrators ownership. An inheritable OWNER RIGHTS read-control rule suppresses implicit owner WRITE_DAC on extracted children, even if their creator owner is the elevated user's SID. It checks the applied permissions and sets **only the child process's** `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. It never adopts or repairs an existing cache. Unsupported ACLs and mutation allows masked by denies are conservatively rejected. TEMP, TMP, APPDATA, LOCALAPPDATA, USERPROFILE and global directory permissions are unchanged. The worker runs hidden; its exit status returns through the bootstrap. Cache cleanup follows worker exit or startup failure; crashes/locks may leave an administrator-only directory that subsequent runs never reuse. Bootstrap failure returns a setup error without starting the worker.

Use the normal unelevated setup entry point. Directly invoking the self-extracting EXE with an elevated token bypasses this bootstrap and remains unsupported; a managed rejection of that invocation occurs after native startup. The bootstrap protects the installer-created elevated worker, not arbitrary administrator-created process launches or a compromised same-user installer/controller. Existing trust in Administrators, SYSTEM and TrustedInstaller, and the documented over-the-shoulder source-policy limitation, still apply.

Safe regression coverage is in `Checks/RuntimeExtractionChecks.ps1`. It uses a private disposable fixture and a harmless published single-file WPF probe, substituting the fixture user for Administrators only in a disposable bootstrap copy. Checks cover protected creation, pre-existing cache preservation, individual foreign mutation grants, junction rejection, actual native extraction before application initialization, WPF native module paths, poisoned inherited cache isolation, literal argument forwarding, profile preservation, nonzero status propagation and cleanup after failure. Actual extracted DLLs must inherit OWNER RIGHTS; Windows Authz models an ordinary owner against those DACLs and positively detects the implicit WRITE_DAC regained by removing that ACE. `-Mutation CacheEnvironment` deliberately removes the child environment assignment in a disposable copy and must fail at the native extraction assertion; the release gate runs it with `-RegressionControls`. These are unelevated synthetic checks, not real administrator/cross-user enforcement evidence.

After publishing and creating the release ZIP, the build runs `Checks/RuntimePackageChecks.cs` against the **actual release bytes**, without loading or executing setup. It checks the .NET bundle manifest, included Core/WPF runtime configuration and libraries, all seven embedded setup/bootstrap resources against source, and ZIP membership and byte hashes. Results are saved as `dist/RUNTIME-PACKAGE.json`. The package format and distribution remain one setup EXE plus the existing license/notice files; no preinstalled .NET runtime is required by the product. This inspection fails closed on an unknown bundle format or compressed entries pending review.

**Still unverified:** actual UAC (both same-user and another administrator), real second-user attempts to write/delete/replace the runtime directory during extraction/loading, production Program Files creation/cleanup with a full token, SYSTEM behavior, interruption/antivirus locks, signing and live installation/accounts. The current host run does not request elevation or perform any of these actions. A release claim requires fresh authorized disposable-VM evidence for this changed startup boundary, including native module paths and runtime directory ownership/DACLs during the elevated worker's lifetime; the existing VM harness alone does not assert that full new runtime attack matrix.

Host validation on **2026-11-01 (Europe/Amsterdam)**: unelevated `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Build-Installer.ps1 -RegressionControls` passed all 29 gate stages, including 13 expected-failure controls, then published and statically inspected the actual release EXE/ZIP. The C# build had zero warnings/errors. The separate production bootstrap guard check returned 125 for an unelevated token before staging or worker startup. No installer was executed, elevation requested or live account accessed. Graphify was created because the base had no graph, queried before exploration and updated after the code changes; its outputs are ignored and no graph files were tracked at this base.

The final safe report is `dist/security/20261031-230900-69ce2cb79231487aa8218d374fa789f5/report.json`, with source SHA-256 fingerprint `9DA46A198388082BE80575CB8A8E8CE74F29C0142C65CC4FA7F17A1BD37989A5`. Static inspection is `dist/RUNTIME-PACKAGE.json`; the inspected EXE SHA-256 is `8E4AA93DB5948F686BEE81CFC2DC42D01C4BC2B767B9BD90A27029A34FDF76F5`. The status file correctly retains `SecurityValidated=false` and actual VM status `unverified`. These ignored build artifacts are local evidence, not committed binaries or proof of real elevation enforcement.

The checked-in [GitHub Actions workflow](.github/workflows/checks.yml) runs safe checks and all regression controls for pushes and pull requests, then packages a development installer with its licenses. GitHub's hosted Windows runner starts as administrator, so `Checks/Run-CiChecks.ps1` creates a disposable standard user, loads its profile, runs the unchanged safe gate with that token, and removes the user afterward. This bootstrap is allowed only on GitHub-hosted runners and must not be run on a personal machine. It does not run the actual privileged VM matrix or publish releases. CI retains synthetic logs and the development ZIP for 14 days; preserve release evidence separately. Require the `Windows safe checks` status on `main` when GitHub branch protection is available.

| Recorded step | Coverage |
| --- | --- |
| `build` | Compile launcher/checks with package sources cleared and NuGet audit disabled; missing local prerequisites fail |
| `csharp-safe` | Generated PE/Go fixtures for adapter jump destinations, pc metadata, relocation, incompatible layouts and `PatchCurrent` frontend/input preservation; storage ownership/mutation policy, profile/group/capability ACL compatibility, private replacement, rollback, links, ACL migration/propagation, Authz access simulations, fake setup extraction/failure cleanup, embedded resources, synthetic add/logout/catalog, bridge authentication, request/handler timeouts, saturation/recovery and shutdown |
| `frontend` | Node assertions over the injected frontend using mocked browser/bridge state |
| `powershell-modules` | Production install/user-integration starts, captured uninstall arguments and unchanged embedded uninstall entry point, and actual publisher configuration/command reject disposable substitute modules and forged signature results; no UAC or real user module changes |
| `installer-fixtures` | Fake install/repair/remove lifecycle, sentinel preservation, temporary shortcuts and unique HKCU marker; all task mutations mocked |
| `shortcut-races` | Directory, junction, hard-link and leaf substitutions against temporary shortcut fixtures |
| `acl-races` | Deterministic migration directory/leaf pinning, junction conversion and concurrent insertion at inspected/ACL-write boundaries |
| `storage-races` | Retained read/write/creation handles, destination insertion rejection and fixture preservation |
| `vm-harness-host-only` | Script parsing, offline compilation/package hashes/provenance, simulated guard predicates and actual compiled refusal of a non-designated path; **no privileged guest tests** |
| `evidence-gate` | Disposable fabricated evidence and Git fixtures test missing/failed/simulated/tampered/stale evidence rejection, actor/ACL validation and source invalidation; **not a VM pass** |
| `evidence-runner` | Disposable runner fixtures verify successful children with unwritable logs fail the stage and gate, later stages still run, child exit codes are retained, and expected negative controls remain enforced |
| `runtime-notices` | Synthetic pack fixtures verify component/version attribution, complete license/notice text, and rejection of missing licenses or an empty pack set |

The optional negative controls remove the install, uninstall or publisher module-path restrictions, disable finalization confirmation, destination pinning, membership checking, ACL propagation suppression, path pins, mutation policy, private replacement, body timeout, handler timeout, or outstanding-handler slot retention, or introduce a wrong adapter jump destination or pc metadata offset in disposable source copies. Each must exit nonzero at its expected regression; compile errors or surviving mutations fail the gate. Normal builds run the positive suites; mutation builds are an explicit audit cost.

Installation and publisher verification supply the Windows system module directory explicitly. Windows PowerShell may also add its protected Program Files module directory during startup. Uninstall sets `PSModulePath` with .NET system paths immediately after parameter binding, before its first cmdlet: shell elevation cannot supply a custom child environment. `-NoProfile` alone does not prevent automatic module loading; removing `PSModulePath` restores default discovery, including writable user locations.

Run `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Checks/PowerShellModuleRegressionChecks.ps1` for the focused suite. It compiles a disposable source copy, observes the real publisher `ProcessStartInfo` at `Process.Start`, and maps absent/empty module paths to a fixture directory to model writable default discovery. The real signature command still runs in Windows PowerShell. Controls prove the substitute modules execute and can return a false valid Hypixel signature. The uninstall fixture runs the captured production command without elevation and stops at its first safety dependency, before removal operations. `-Mutation InstallModules`, `-Mutation UninstallModules` and `-Mutation PublisherModules` must each fail their corresponding assertion. These are isolated regression checks; actual elevated discovery and real signed-launcher acceptance remain unverified.

The default C# entry point and `--safe` run the generated adapter fixtures before stopping at the legacy lifecycle/native executable checks. `--native-adapter` runs just those fixtures. Safe checks use fake bytes and loopback listeners; they do not inspect the installed launcher or credential storage. Native binary and live integration checks are separate opt-in work and are not evidence from this gate. See [the audit](SECURITY-REAUDIT.md) for remaining limitations.

## Actual VM evidence and the release claim

Prepare a fresh package and perform the complete [disposable VM runbook](Checks/VmValidation/README.md), including normal UAC, over-the-shoulder UAC, unrelated standard-user attacks, actual administrator/SYSTEM staging, both real repair tasks, failure cleanup and sentinels. Only an explicitly authorized isolated disposable VM may execute those scenarios. Ordinary builds never launch a VM or request UAC. Older packages without source/SDK provenance must be rebuilt, not relabeled.

After guest `Verify` and `Cleanup`, copy the complete `ModLauncherSecurityVM-Evidence-<id>` export to a local evidence directory. Then run:

```powershell
# Check the release claim without creating an installer.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1 `
    -ChecksOnly -SecurityValidated -VmEvidence C:\ReleaseEvidence\ModLauncherSecurityVM-Evidence-<id>

# Local release-candidate packaging with the same mandatory gate.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1 `
    -SecurityValidated -VmEvidence C:\ReleaseEvidence\ModLauncherSecurityVM-Evidence-<id>
```

Replace the placeholder with the actual exported directory. `-SecurityValidated` fails before packaging when evidence is missing, incomplete, failed, simulated, hash-mismatched or stale. Without that switch, the safe build can succeed but records `SecurityValidated: false`, `VmStatus.Status: unverified` and the reason. A VM pass alone cannot bypass failing synthetic checks.

The gate requires a successful actual-VM summary, matching source fingerprint/SDK, hash-verified fixed evidence manifest, distinct actor identities and UAC token types, denied unrelated-user operations with successful positive controls, restrictive captured staging ACLs, failure/sentinel outcomes, SYSTEM staging, and successful SYSTEM/limited repair/task results. OTS rejection of a different user's source remains an **expected fail-closed compatibility limitation**, not proof that an OTS installation works.

## Provenance, freshness and retention

Every run writes `dist/security/<UTC>-<id>/report.json` and per-step logs, even when a check fails. The report records Git HEAD, dirty status, per-file SHA-256 hashes, aggregate source fingerprint, OS/build, architecture, filesystem, PowerShell, SDK, Node, timestamps, exact command/argument arrays, exit codes, expected negative failures, log hashes, safe status and VM status. A dirty-tree pass applies to those exact bytes, not to HEAD alone. Source changes during checks or packaging fail validation.

The fingerprint includes all non-Markdown, non-ignored files under `Launcher/` and `Checks/`, root PowerShell/C#/solution/build scripts, global/NuGet/Directory.Build configuration and `.github/workflows/`. It includes untracked additions and marks tracked deletions. Changes to storage/ACL helpers, bridge code, elevation/staging, installer/repair scripts, embedded resources, checks, harness, gate or build configuration therefore require **fresh full VM validation** before the claim. An SDK change also invalidates VM evidence. Documentation-only commits can reuse a matching run; both the guest's tested revision and the current revision remain recorded.

Maintainers must also request fresh VM validation for changed target Windows/runtime/security policy, new supported filesystems/architectures, new security findings, or build inputs added outside the fingerprint's coverage. The gate does not infer these changes. Evidence proves only the recorded guest configuration; other configurations remain unverified. Review the fingerprint's file list when adding build inputs.

Local packaging emits `dist/SECURITY-STATUS.json`, binding the installer SHA-256 to the safe report hash and VM status. It also creates `HytaleAccounts-Setup.zip` containing the executable, MIT `LICENSE` and `THIRD-PARTY-NOTICES.txt` collected from the exact runtime packs used in that publish. Missing runtime license text fails packaging. `SHA256SUMS.txt` covers the ZIP, executable and both license files. Preserve those files, the whole safe-report directory and the complete VM export together. `dist/` is ignored and is not durable evidence unless retained by the maintainer. Never reuse metadata for a different installer hash. No command here publishes or modifies external services.

Until both actual VM validation and the README's live acceptance sequence have passed, publish only explicitly experimental prereleases. Mark older versions lacking the current security fixes unsupported. Link users to `/releases`, not `/releases/latest`, which can select an older regular release or be unavailable when only prereleases exist. Review logs/status metadata for local paths and identities before attaching public evidence. See [SECURITY.md](SECURITY.md) for publication settings and reporting.

Evidence is unsigned, maintainer-supplied test output, not remote attestation. A compromised builder/guest administrator can forge it. Trust the reviewed source, build environment, VM operator and retained evidence. Simulated Authz access, deterministic races and fabricated evidence-schema tests must always be reported separately from actual user-token/elevation enforcement. No live credentials, real installation/authentication/gameplay, exhaustive races, sustained denial-of-service, or compatibility outside the recorded environment are certified.
