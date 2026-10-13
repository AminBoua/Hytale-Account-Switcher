# Security re-audit — 2026-10-13

**Verdict: CAUTION.** Synthetic checks pass after hardening storage ownership/mutation validation, retaining handles through reads and writes, and ensuring replacement addon files receive private ACLs. The earlier migration and bridge fixes remain in place. Real elevation and enforcement under another Windows user's token remain unverified. Windows Authz access decisions for a modeled unrelated user are covered; those are simulated access checks, not cross-user file-open tests.

Reviewed the existing working-tree fixes, their callers, embedded-resource packaging, installation/removal/repair scripts, and test fixtures. Earlier uncommitted work was retained.

Routine builds now enforce [the safe security release gate](SECURITY-RELEASE.md). The final 2026-10-13 gate run passed nine safe stages and eight expected-failure controls, then correctly refused the security-validated release claim because actual VM evidence is absent. See the release-gate follow-up below for exact provenance. **SafeStatus: passed; VmStatus: unverified; SecurityValidated: false.**

| Area | Evidence and result |
| --- | --- |
| Snapshot/runtime ACLs | Existing snapshots, copied keys, pending files, cached updates and runtime images receive protected ACLs for the current user, SYSTEM and Administrators. New temporary files have that ACL before bytes are written. Migration is idempotent; link and inaccessible-file cases stop initialization. |
| Hostile storage paths | Owner and effective mutation rules are checked for each ancestor and leaf before use. Foreign child insertion is rejected in storage directories; ancestor-only sibling creation is permitted. Inherited read-only groups, inherit-only Creator Owner, Owner Rights and package/capability ACEs are compatible. Unsafe paths are rejected without repairing Hytale-owned ACLs. Existing foreign-writable files fail before read, version inspection, replacement or migration. |
| Private replacement and conflicts | Snapshot/key/metadata/runtime/update writes replace with a private file instead of inheriting the old destination's permissive DACL. Handles remain held through reads and private atomic rename; exclusive directory creation refuses intervening entries. Existing Hytale files retain their DACL through `File.Replace`. Directory/leaf sharing failures name the blocked path and tell the user to close its program and retry, using existing exception/status reporting. Tests verify the error excludes synthetic sensitive contents. |
| ACL migration races | Deterministic temporary-source instrumentation exercises root/descendant replacement, leaf replacement/write, empty-directory junction conversion, and pre-acquisition junction insertion. Protected files, inherited files, nested files and junctions are inserted both after inspection and immediately before directory ACL application. Migration refuses changed membership, leaves inserted bytes/ACLs alone, and preserves outside sentinels. Late hard-link creation succeeds in unpinned positive controls but is blocked with sharing violation 32 while migration holds its pins; this is recorded as prevention, not a successful insertion. Hooks are absent from shipped source. |
| Effective access | `AuthzInitializeContextFromSid` with `AUTHZ_SKIP_TOKEN_GROUPS`, explicit enabled Everyone/Authenticated Users/Users groups via `AuthzAddSidsToContext`, and `AuthzAccessCheck` evaluate a synthetic unrelated SID. Permissive positive controls are readable; private descriptors and migrated fixture DACLs deny each tested read, write, delete and ownership/permission-change right; the modeled owner retains full control. No account creation, login, impersonation, or directory-service group lookup occurs. |
| Active-session ACLs | `File.Replace` preserves the destination DACL. Tests passed for both protected restrictive ACLs and inherited ACLs, including rollback after deletion and failed metadata commit. Native parent/key ACLs and unrelated fixture data remain unchanged. |
| Staging integrity | Exclusive directory creation supplies the ACL before extraction; embedded resources supply scripts. Source and payload handles prevent overwrite/replacement. Tests cover policy rejection, hard links, junctions, pre-existing staging, partial extraction, cleanup and hidden process settings. Actual administrator/SYSTEM-only extraction was not performed. |
| Shortcut races | Existing handle-relative opens retain the inspected leaf through writes/deletion. Deterministic fixture attacks cover directory/leaf replacement, pre-acquisition junctions, in-place parent junction conversion, hard links and concurrent creation during restore. Victim sentinels remain intact. |
| Bridge authentication | Standalone tests cover missing/wrong/cross-launch capabilities; Origin, Host, query and referrer rejection; bootstrap loading; preflight; method, content type, size and JSON depth checks. Rejected requests never invoke the synthetic handler. Authorized requests succeed; UI-only mode rejects commands; interrupted shutdown is tolerated. |
| Bridge limits | An incomplete authorized body returns 408 around the five-second deadline without invoking a handler. Sixteen blocked handlers saturate requests; request 17 receives 503. Handler responses time out, but outstanding work retains its slots. Unauthorized requests never invoke handlers before, during or after saturation. State reads recover while handlers remain blocked, commands recover after release, shutdown finishes, and a synchronously blocking handler cannot stop the accept loop or its response deadline. |
| Disposable VM validation harness | Prepared in `Checks/VmValidation`, with a reproducible guest runbook, host-safe validator and separate host evidence. Scripts parse, the harness builds without warnings, and refusal/package checks pass. **Actual VM UAC, cross-user, elevated/SYSTEM staging and task scenarios have not run.** No usable authorized VM was discovered. |

Changes made during this audit:

1. **Closed the migration check/use gap.** `PrivateFiles.SecureTree` previously inspected paths and released file handles before reopening those paths to apply ACLs. It now holds validated directories and non-following file handles until migration finishes, and validates ownership during inspection. Lock failures remain I/O failures and prevent initialization.
2. **Strengthened directory pins.** Setup, reads and ACL migration deny directory write access as well as deletion, preventing a concurrent directory write handle from changing reparse data. Regression tests check denial while pinned and rejection of a pre-existing writer before ACL/metadata changes; retry succeeds after the writer closes. Atomic mutation now has the explicitly validated write-sharing lifetime described below, because NTFS rename requires it.
3. **Improved independent coverage.** Added `--bridge-auth`, independent of native binaries/storage. Added inherited-session ACL preservation checks. Corrected the unelevated-worker test to supply all four valid arguments, so it actually checks the privilege guard.
4. **Closed insertion and propagation gaps.** Pins do not stop new children from being created. Migration now compares immediate directory membership before and after ACL application and stops initialization on changes. Directory DACLs are applied with `SetKernelObjectSecurity` on a non-following handle while the existing pins remain held, avoiding recursive propagation into uninspected children. A separate native-API fixture proves the old setter changes an uninspected child's ACL while the new setter does not. Validated files still receive their explicit protected ACLs. A failed migration may have restricted already-inspected entries; their bytes remain intact and startup does not proceed.
5. **Enforced bridge deadlines and retained work limits.** The incomplete-body test reproduced a real timeout failure: `HttpListener` body reads did not stop on the cancellation token alone. `WaitAsync` now bounds that await and handler responses. Handler execution is scheduled separately so synchronous blocking cannot stall acceptance. A 16-slot semaphore remains occupied until actual handler completion, even after the HTTP response times out. Late faults are observed. This preserves the handler interface and authentication checks.
6. **Rejected hostile storage permissions before use.** Shared `StorageSafety` validation accepts the current user, SYSTEM, Administrators and TrustedInstaller as owners/trusted writers (installer policy excludes the current user). It maps generic access bits, processes ACEs in order, and removes earlier matching-trustee or Everyone denies before deciding whether an untrusted allow can mutate. It does not assume a deny for one group protects an allow to another SID. Foreign ownership, null DACLs and unsupported conditional/object rules fail closed. Attribute-query failures are no longer silently treated as missing files. Ancestor directories can permit new siblings, but not deletion/replacement of existing children, ACL changes or reparse mutation; actual storage directories cannot permit foreign child insertion.
7. **Retained validated objects through sensitive operations.** Snapshot/key reads, active-session rollback capture, update version/publisher inspection and runtime creation retain ancestor and leaf handles. New directories are created exclusively with private ACLs already attached. Installer extraction pins the newly created empty directory before any payload bytes. Atomic writes first acquire strict directory pins to exclude pre-existing writers, validate each DACL, then acquire replacement pins sharing write but denying deletion before releasing the strict handles. This is required for NTFS rename, not an error-retry fallback: validated ACLs must exclude new other-user mutation during this phase. Private file handles stay open through a handle-relative `NtSetInformationFile(FileRenameInformationEx)` rename; an intervening destination is never overwritten for a create operation. Hytale's existing-file branch retains `File.Replace` semantics.
8. **Kept new addon bytes private on replacement.** Capture previously used the same ACL-preserving replacement as Hytale's active file, so a permissive snapshot DACL could survive a new capture. Private replacement now covers snapshots, copied keys (including unchanged key bytes), metadata, runtime images and cached updates. Native parents, existing keys and the active-session DACL remain untouched; rollback still restores both bytes and the original protected/inherited DACL. No vault, authentication changes or product UI were added.
9. **Made sharing failures actionable.** Errors from held directory/leaf opens and atomic replacement identify the exact blocked path and instruct closing the conflicting program before retrying. Tests hold real synthetic directory writers, file writers and readers denying delete sharing, then verify successful retry and preserved native ACLs. Diagnostics contain paths and Windows error codes, never bytes or bridge capabilities.

Application package/capability ACEs do not independently grant an unrelated Windows user access: Windows requires both the normal user/group and AppContainer sides of the token to allow it. Their presence therefore does not cause blanket profile-directory rejection; ordinary group mutation grants still do. See [Microsoft's AppContainer access model](https://learn.microsoft.com/en-us/windows/win32/secauthz/implementing-an-appcontainer). The private rename uses replacement/POSIX flags documented in [FILE_RENAME_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information); no copy/delete fallback weakens the ACL or sharing checks.

The original review ran the following checks successfully (installer, shortcut, setup and frontend checks were not repeated for this focused expansion):

```powershell
dotnet run --project Checks/Checks.csproj -c Release -- --acl
dotnet run --project Checks/Checks.csproj -c Release -- --setup
dotnet run --project Checks/Checks.csproj -c Release -- --bridge-auth
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/ShortcutRaceChecks.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/InstallerScriptChecks.ps1
node Checks/NativeLoginChecks.cjs
git diff --check
```

The installer lifecycle fixture uses temporary paths, a unique test registry key and mocked scheduled-task mutations. It does not install the addon. The staging fixture executes only harmless synthetic scripts without elevation. HTTP requests target locally created loopback listeners; hostile-looking header values do not cause external requests.

The focused expansion ran these affected checks successfully:

```powershell
dotnet build Checks/Checks.csproj -c Release --no-restore --nologo
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --acl
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --acl-access
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --acl-propagation
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/AclMigrationRaceChecks.ps1
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --bridge-auth
git diff --check
```

The final build had zero warnings/errors. Initial sandbox runs could not use HTTP.sys, hard-link creation or owner-only fixture ACLs; affected tests were rerun with sandbox permission using the normal Windows host token. No Windows/UAC elevation was requested. All runtime data was synthetic and temporary; no real credentials, accounts, Hytale processes, installation, scheduled-task registration, or external endpoints were used. Isolated test builds clear package sources and disable NuGet auditing.

Negative controls ran in disposable source copies; **each command below intentionally exited nonzero at the relevant regression**, rather than surviving the mutation:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/AclMigrationRaceChecks.ps1 -Mutation Membership
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/AclMigrationRaceChecks.ps1 -Mutation Propagation
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/AclMigrationRaceChecks.ps1 -Mutation Pins -PinsOnly
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/BridgeRegressionChecks.ps1 -Mutation BodyTimeout
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/BridgeRegressionChecks.ps1 -Mutation HandlerTimeout
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/BridgeRegressionChecks.ps1 -Mutation HandlerSlots
```

Observed failures, respectively: inserted protected file accepted; uninspected inherited child's ACL changed; root replacement succeeded without a sharing violation; incomplete-body response missed its deadline; stalled handler responses missed their deadline; timed-out outstanding handlers no longer retained their concurrency budget. Mutation modes never edit the working-tree production source. The Authz permissive positive controls additionally show that dropping private ACL restrictions would be detected.

The hostile-storage follow-up ran these affected checks successfully (the `--acl` entry now includes `StorageSafetyChecks`):

```powershell
dotnet build Checks/Checks.csproj -c Release --no-restore --nologo
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --storage-safety
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --acl
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --setup
dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --native-logout
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/AclMigrationRaceChecks.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/AclMigrationRaceChecks.ps1 -Storage
git diff --check
```

The build had zero warnings/errors. `--native-logout` uses synthetic stores and simulated native acknowledgements and also checks synthetic Add and loopback bridge shutdown; no real account was changed or launcher started. Normal profile inheritance was exercised by placing uniquely named disposable, private fixtures directly under LocalAppData. This machine's Temp directory permits mutation by additional principals, so it is deliberately not exempted from validation and its ACL was not changed. Disposable instrumented builds still use Temp for nonsensitive synthetic source compilation, with package sources cleared. No real credential paths were inspected, UAC requested, addon installed, tasks registered or external application services contacted in this follow-up.

Two additional disposable-source negative controls intentionally exited nonzero at their relevant assertions:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/AclMigrationRaceChecks.ps1 -StorageSafety -Mutation Policy
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/AclMigrationRaceChecks.ps1 -StorageSafety -Mutation PrivateReplacement
```

Disabling mutation-right rejection failed at “storage directory rejects foreign child insertion.” Restoring the old snapshot replacement behavior failed at “capture replaces existing permissive snapshot with a private file.” The working source was not mutated. Deterministic storage hooks additionally prove read/creation/write handle retention, rejection of intervening destination creation, and preservation of concurrent fixture data. Hooks exist only in disposable build copies.

Disposable VM harness follow-up:

- Added [the VM runbook](Checks/VmValidation/README.md), [host-safe validation](Checks/VmValidation/Validate.ps1) and [host preparation evidence](Checks/VmValidation/HOST-VALIDATION.md). Production code and prior uncommitted work were preserved. The harness references the production security helpers and invokes unchanged shortcut repair code; it never invokes the real setup/install entry point or launcher.
- Guest execution requires explicitly designated VM hardware/UUID distinct from the build host, matching machine/run identity, an eight-hour expiry, disconnected networking, no domain membership, enabled UAC and an immutable administrator-owned package. Test roles must use three uniquely named disposable local accounts. Actual tasks and the registry marker are uniquely namespaced per run; only synthetic shortcut locations and fake account/payload bytes are used.
- The planned guest matrix exercises normal UAC and over-the-shoulder UAC with actual token/SID evidence; unrelated-user read/write/replacement/deletion/insertion and blocking-handle attempts; administrator and SYSTEM staging; SYSTEM and limited interactive repair tasks; failure cleanup and victim preservation. An OTS administrator is expected to reject the other user's source ownership/mutation policy. This is an explicit expected compatibility limitation, not a successful source handoff. Subsequent staging uses separately generated fake bytes and does not relax production validation.
- Host discovery found a physical GIGABYTE G6 KF, no available Hyper-V module/management namespace, no discovered VMware/VirtualBox/QEMU command tools or running VM processes, and no Windows Sandbox executable. Hypervisor presence alone does not authorize guest execution. A suitable VM/checkpoint and access/authorization are still needed. No privileged test was run on the host.
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/VmValidation/Validate.ps1` passed under Windows PowerShell 5.1.26100.9549 / .NET SDK 10.0.203: all scripts parsed, build had zero warnings/errors, pure guard tests passed, package hashes matched, and the compiled runner returned the specific guard refusal for a non-designated host path. These are **host preparation and simulated guard tests only**, separate from the earlier Authz simulations and from future actual cross-user evidence. `git diff --check` passed.
- The guest controller only reports success after both UAC modes, both unrelated-user attack phases, SYSTEM staging and both real task results exist and pass. It exports token identities/types, timestamps, Windows error codes, staging SDDL, package hashes and task XML. Incomplete, canceled or timed-out runs do not count as passes. Cleanup validates exact task identities/actions and the registry marker, exports a fixed evidence list, and removes only the run fixture; checkpoint reversion is the final recovery boundary.

Remaining risks and coverage gaps:

- No further confirmed bypass was found in the exercised paths. This is not an end-to-end security certification: no real UAC handoff, over-the-shoulder elevation, SYSTEM task execution, or second-user enforcement test ran. Authz uses an explicitly modeled SID/group set, not a real logon token, and does not test traversal privileges, integrity levels, existing handles or actual file opens by another principal. Production staging ownership was checked as an ACL policy; the earlier extraction fixture uses the current user.
- The addon preserves Hytale-owned storage ACLs and now rejects hostile ownership/mutation permissions rather than assuming they are safe. Read grants on native data intentionally remain Hytale's responsibility; previously authorized readers and copies obtained before private replacement cannot be revoked by these fixes. The current user, SYSTEM, Administrators and TrustedInstaller remain trusted. Same-user/admin compromise and pre-existing handles granting ACL changes are not isolated; ordinary write handles are excluded at acquisition, but Windows sharing rules do not exclude every security-descriptor operation. Pins are operation-scoped, not held for the launcher lifetime. Mutation directory pins deny deletion but share writing after strict acquisition and validation, relying on the validated DACL against other users.
- Effective mutation validation is a conservative upper bound over every untrusted trustee, not directory-service group enumeration or a real second-user logon. It handles earlier same-trustee/Everyone denies, inherited rules and generic rights, but may reject customized safe ACLs involving cross-group denies or unsupported conditional/object ACEs. No blanket exemption is made for profile paths or the Temp directory. Unsupported rename semantics/filesystems fail closed; compatibility beyond the tested Windows/NTFS host is unverified.
- Deterministic shortcut attacks, ACL migration insertion/replacement attacks and directory-lock regressions are covered; exhaustive concurrent filesystem interleavings, network filesystems, and non-NTFS behavior are not. A conflicting directory writer or changed membership stops migration; retry after the conflicting activity ends. Same-user/admin actors can still change ACLs or insert entries after migration ends.
- Bridge body/handler timeouts and concurrency saturation are covered. A timed-out handler cannot be forcibly stopped and may finish later; permanently stuck handlers keep their slots occupied (bounded at 16). No sustained denial-of-service stress test, stalled response-reader test, or real WebView/browser interaction ran. No live login, switching, logout, game launch, installation or release packaging ran, and no real credential files were accessed.

## Release-gate follow-up — 2026-10-13

Findings and changes:

- The existing build entry point ran only a subset of the security regressions and its default C# path reached installed-native-binary checks. `Build-Installer.ps1` now always invokes the safe gate; the C# default/`--safe` path combines synthetic suites and exits before native binary/legacy lifecycle work. No product UI, authentication or production protection code changed in this follow-up. Existing uncommitted fixes were preserved.
- There was no recorded provenance or enforced distinction between safe synthetic success and actual VM validation. Each gate run now records commands, exit codes, timestamps, log hashes, revision, dirty-tree file hashes/fingerprint and environment. Local packaging writes an installer-hash-bound status file. `-SecurityValidated` refuses missing/invalid/stale VM evidence before packaging; ordinary safe builds retain explicit unverified status.
- VM package/run/summary records now carry source/SDK provenance and the guest summary hashes its required evidence files. The gate verifies recorded actors/tokens, staging ACLs, denied operations, failure outcomes and actual repair/task results. Fingerprinted code/resources/build/check changes or SDK changes require fresh full VM validation. Documentation-only changes do not. [The release policy](SECURITY-RELEASE.md) also requires manual revalidation for target/runtime/security-policy changes and newly discovered security findings.
- Added synthetic gate tests for missing/empty, failed, simulated, stale, hash-tampered, wrong-actor, permissive-ACL and incomplete evidence; disposable Git fixtures cover untracked resources, edits, tracked deletions and documentation-only compatibility. All 15 assertions passed. The successful fabricated-schema test is strictly a parser/validation fixture, never a real VM result.
- The first combined run passed its positive suites but rejected two negative controls because the gate expected incorrect assertion wording. Corrected those matchers and required actual failure text. The final run detected all eight intended regressions in disposable source copies. No further production protection failure was confirmed in this run.

Final command (unelevated):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Build-Installer.ps1 -ChecksOnly -RegressionControls -SecurityValidated
```

This command intentionally requested the release claim without supplying VM evidence. **Overall exit 1 is the expected release-gate refusal**, after every safe stage passed: `Release is UNVERIFIED: No actual VM evidence supplied.` It did not package or distribute an installer.

| Provenance | Recorded value |
| --- | --- |
| Time | 2026-10-13 16:55:21.9046048–16:57:52.5644443 UTC |
| Git HEAD | `ed663e58ee100a312f1b547fd8ca856dc4ff1a8d` |
| Working tree | Dirty, including pre-existing work; HEAD alone is not the tested source |
| Source SHA-256 fingerprint | `C87B616DAB468537E2B78DEF050D9E15F340EF67A1DD160179A2B79F6C1EC110` |
| OS | Microsoft Windows 11 Home, version 10.0.26200, build 26200, AMD64, NTFS |
| Tools | Windows PowerShell 5.1.26100.9549; .NET SDK 10.0.203; Node v22.21.1 |
| Safe result | Passed |
| Actual elevated/cross-user VM result | Unverified: no actual VM evidence supplied |
| Security-validated release | False |

Full command/argument arrays, per-file source hashes, check timings, exit codes and log hashes are in [the machine-readable report](dist/security/20261013-165521-8f279704dcf247228e3bbc4d0a1224a6/report.json); sibling files contain each step's output. Retain this ignored directory externally to the working tree as release evidence; it is not committed by this document.

| Final stage | Result |
| --- | --- |
| Offline C# build | Exit 0, zero warnings/errors |
| Combined C# safe suite | Exit 0: storage/ACL/rollback/setup, simulated Authz access, synthetic transitions/catalog and authenticated loopback bridge limits/recovery |
| Node frontend | Exit 0 |
| Installer lifecycle fixtures | Exit 0; fake files/shortcuts, unique HKCU fixture and mocked tasks only |
| Shortcut race fixtures | Exit 0 |
| ACL migration races | Exit 0 |
| Storage read/create/write races | Exit 0 |
| VM harness host preparation/refusal | Exit 0; parses, compiles, verifies package hashes/provenance and rejects non-designated execution |
| Evidence-gate fixtures | Exit 0; 15 synthetic assertions |
| Eight negative controls | Each exit 1 at its expected regression: Membership, Propagation, Pins, Policy, PrivateReplacement, BodyTimeout, HandlerTimeout, HandlerSlots |

Whitespace validation passed.

No actual UAC, SYSTEM task, second-user enforcement, live launcher/account test, real installation, packaging, signing or publication was performed. Authz and VM guard/evidence-schema fixtures remain simulations. Actual VM use still needs an authorized isolated guest; the earlier host discovery found none. The evidence format is unsigned and trusts the reviewed builder/guest operator, not a hostile administrator. The documented OTS source rejection, storage trust assumptions, bounded bridge behavior and untested platform/live-integration limits above remain in force. A passing safe gate must never be presented as proof those gaps are closed.
