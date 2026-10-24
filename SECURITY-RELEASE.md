# Security release gate

**Current assurance: safe synthetic checks only; actual elevated/cross-user VM validation is UNVERIFIED.** A normal build may produce a development artifact with that status. It cannot be described as security-validated until the gate accepts successful matching VM evidence. This is validation of the documented test matrix, not a security certification or proof of live Hytale compatibility.

## Routine builds

Run unelevated on Windows x64 with Git, .NET 10 SDK, Node.js and Windows PowerShell 5.1:

```powershell
# Safe checks only; no installer packaging, elevation, live accounts or external services.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1 -ChecksOnly

# Same checks followed by local installer packaging (does not distribute a release).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1

# Audit/release-candidate run: also prove that ten deliberately broken copies fail.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1 -ChecksOnly -RegressionControls
```

`Build-Installer.ps1` is the repository's build/release entry point. All its paths run the safe gate first. A direct `dotnet build` is compilation only and supplies no security validation. There is no skip-checks build option. Safe checks refuse an elevated token; never elevate to fix a failure.

The checked-in [GitHub Actions workflow](.github/workflows/checks.yml) runs safe checks and all ten regression controls for pushes and pull requests, then packages a development installer with its licenses. GitHub's hosted Windows runner starts as administrator, so `Checks/Run-CiChecks.ps1` creates a disposable standard user, loads its profile, runs the unchanged safe gate with that token, and removes the user afterward. This bootstrap is allowed only on GitHub-hosted runners and must not be run on a personal machine. It does not run the actual privileged VM matrix or publish releases. CI retains synthetic logs and the development ZIP for 14 days; preserve release evidence separately. Require the `Windows safe checks` status on `main` when GitHub branch protection is available.

| Recorded step | Coverage |
| --- | --- |
| `build` | Compile launcher/checks with package sources cleared and NuGet audit disabled; missing local prerequisites fail |
| `csharp-safe` | Generated PE/Go fixtures for adapter jump destinations, pc metadata, relocation, incompatible layouts and `PatchCurrent` frontend/input preservation; storage ownership/mutation policy, profile/group/capability ACL compatibility, private replacement, rollback, links, ACL migration/propagation, Authz access simulations, fake setup extraction/failure cleanup, embedded resources, synthetic add/logout/catalog, bridge authentication, request/handler timeouts, saturation/recovery and shutdown |
| `frontend` | Node assertions over the injected frontend using mocked browser/bridge state |
| `installer-fixtures` | Fake install/repair/remove lifecycle, sentinel preservation, temporary shortcuts and unique HKCU marker; all task mutations mocked |
| `shortcut-races` | Directory, junction, hard-link and leaf substitutions against temporary shortcut fixtures |
| `acl-races` | Deterministic migration directory/leaf pinning, junction conversion and concurrent insertion at inspected/ACL-write boundaries |
| `storage-races` | Retained read/write/creation handles, destination insertion rejection and fixture preservation |
| `vm-harness-host-only` | Script parsing, offline compilation/package hashes/provenance, simulated guard predicates and actual compiled refusal of a non-designated path; **no privileged guest tests** |
| `evidence-gate` | Disposable fabricated evidence and Git fixtures test missing/failed/simulated/tampered/stale evidence rejection, actor/ACL validation and source invalidation; **not a VM pass** |
| `runtime-notices` | Synthetic pack fixtures verify component/version attribution, complete license/notice text, and rejection of missing licenses or an empty pack set |

The optional ten negative controls disable membership checking, ACL propagation suppression, path pins, mutation policy, private replacement, body timeout, handler timeout, or outstanding-handler slot retention, or introduce a wrong adapter jump destination or pc metadata offset in disposable source copies. Each must exit nonzero at its expected regression; compile errors or surviving mutations fail the gate. Normal builds run the positive suites; mutation builds are an explicit audit cost.

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
