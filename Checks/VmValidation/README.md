# Disposable Windows VM security validation

**Status: prepared and host-validated; privileged/cross-user VM scenarios have NOT been run.** See `HOST-VALIDATION.md`. A successful build or simulated guard predicate is not evidence of Windows cross-user enforcement.

This harness calls the production `Setup.ElevationStart`, `SetupPayload` staging/pinning/ownership helpers, `PrivateFiles`, `NativeStores.AtomicWrite`, and the unchanged `Repair-Integration.ps1` / `ShortcutIntegration.ps1` / `ShortcutFile.cs`. It never calls `Setup.Run` or `Setup.RunElevated`, runs the real installer, launches Hytale, or reads official account storage. The only executed payload script is the literal `exit 23`. Files named `HytaleAccounts.exe` and `hytale-launcher.exe` in the repair/staging fixtures are non-executable text.

## Host preparation (safe)

From the repository root, with the installed .NET 10 Windows SDK:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/VmValidation/Validate.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/VmValidation/Build.ps1
```

`Validate.ps1` parses the scripts, builds with package sources cleared, tests refusal predicates, verifies package hashes/provenance, and invokes only a compiled-runner guard rejection. It creates and removes a temporary build package. `Build.ps1` creates `Checks/VmValidation/package`; it refuses to overwrite an existing directory. Use `-Destination` with a new directory outside the source tree when preserving an older package. Preserve the resulting `package.json` with its schema, Git revision, dirty-tree source hashes/fingerprint, SDK/build environment, host UUID, timestamp and package SHA-256 manifest. Legacy packages without that provenance cannot qualify for the release gate; rebuild them. Copy the **whole** package into the guest through a read-only virtual disk/ISO or a temporary transfer mechanism that is disconnected before testing. No dependencies are downloaded by these scripts. Do not rebuild the package inside the test guest: its build-host UUID is deliberately prohibited from execution.

## Guest prerequisites and explicit authorization

Use a freshly snapshotted, explicitly disposable **Windows 11 x64 VM**, with the .NET 10 **Windows Desktop Runtime**, Windows PowerShell 5.1, local account management and Task Scheduler available. Use a local NTFS `C:` drive and standard `C:\Program Files`; other layouts are deliberately refused. UAC must be enabled with normal consent/credential prompts. Do not disable secure desktop or change UAC policy to make a test pass.

Before importing the package, authorize use of this particular VM/checkpoint. It must contain no real credentials, Hytale installation, personal data, domain membership or production services. Disable guest network adapters (all must be disconnected), shared folders/drives, clipboard sharing and host integrations. Hypervisor detection alone is insufficient: initialization also requires the exact guest SMBIOS UUID, an explicit designation switch and three disposable accounts. The run is bound to that UUID and computer name, expires after eight hours, and cannot run on the package's build host.

In the **guest only**, create three throwaway local accounts through Windows Settings/Computer Management, using the same fresh six-hex-digit suffix:

| Example account | Membership | Purpose |
| --- | --- | --- |
| `MLSV-Admin-a1b2c3` | Administrators | Normal filtered-token UAC initiator and elevated worker |
| `MLSV-User-a1b2c3` | Users, not Administrators | Over-the-shoulder initiator and limited repair task |
| `MLSV-Other-a1b2c3` | Users, not Administrators | Unrelated attacker |

Use passwords created only for this disposable VM. Enter them locally at Windows prompts; never paste them into scripts, command arguments, evidence or this chat. The harness does not create/delete accounts or accept/read passwords. Sign in once as each account to initialize profiles; **keep the standard User account logged in** for its interactive-token scheduled task. The controller verifies distinct SIDs, memberships and token identities. Revert the checkpoint afterwards to remove the accounts.

As the designated Admin account, open an elevated guest PowerShell console and run:

```powershell
$guestUuid = (Get-CimInstance Win32_ComputerSystemProduct).UUID
# C:\VMInput\package is the copied package, not a host shared folder.
& C:\VMInput\package\Run.ps1 -Action Initialize -DesignatedDisposableVM `
    -VmUuid $guestUuid -AdminUser MLSV-Admin-a1b2c3 `
    -StandardUser MLSV-User-a1b2c3 -AttackerUser MLSV-Other-a1b2c3
```

Record the printed root, such as `C:\Program Files\ModLauncherSecurityVM-<32 hex digits>`. Set `$root` to that exact value in each test console. Initialization creates only that unique fixture tree and `HKLM:\Software\ModLauncherSecurityVM-<id>`. It copies a hash-verified package to an administrator-owned, read-only-to-users `bin` directory. Test actors can write only their synthetic fixture/evidence channels. Do not edit the immutable package or `run.json`; start a fresh run after changes. All further commands use this protected package.

## Actual UAC and cross-user tests

Use real guest account sessions (Switch user keeps existing sessions running). A console opened with Windows “Run as different user” using a disposable account is also suitable for the attacker. Do not use `/netonly`, impersonation mocks, or an elevated console for `begin`/`attack`.

For **normal UAC**:

1. In an unelevated Other-account console, start:
   ```powershell
   & "$root\bin\Checks.exe" attack $root normal
   ```
2. In an **unelevated** Admin-account console, start:
   ```powershell
   & "$root\bin\Checks.exe" begin $root normal
   ```
3. Approve the real Windows consent prompt for the validation runner. The runner pins its executable, ancestors and fake downloaded source before requesting UAC. The unrelated process attempts source/runner/library substitution during that lifetime, then attacks the staged payload while the elevated worker holds it. Each cross-user phase has a 120-second deadline; approve promptly. There is no automatic secure-desktop interaction.

For **over-the-shoulder UAC**, repeat with `ots` in an Other-account console and an **unelevated User-account** console:

```powershell
# Other account:
& "$root\bin\Checks.exe" attack $root ots
# User account:
& "$root\bin\Checks.exe" begin $root ots
```

At the real credential prompt, enter the disposable **Admin** account credentials. The worker must have a different SID from the initiator and a full elevated token. **Expected compatibility limitation:** production ownership/mutation validation rejects the User-owned downloaded source under the other administrator SID. This must be a specific ownership/mutation rejection, not an arbitrary I/O failure. It is recorded as `OtsForeignSourceRejected`, not successful installation or successful source handoff. The worker separately exercises elevated staging using generated fake bytes; it never weakens the source policy to continue.

Both modes are single-use. Missing peers, UAC cancellation, unexpected SIDs, wrong token types, unexpected successful access, missing files instead of denied access, altered payloads or timeouts fail the run. Start a new initialization for a retry; preserve failed evidence first.

## Actual SYSTEM and limited repair tasks

Keep the User account signed in, then run in the elevated Admin guest console:

```powershell
& "$root\bin\Run.ps1" -Action Tasks -Root $root
& "$root\bin\Run.ps1" -Action Verify -Root $root
```

This registers and explicitly starts only `ModLauncherSecurityVM-<id>-System` (SYSTEM service-account token) and `...-Limited` (User SID, interactive token, limited run level). They have no recurring triggers, run hidden, and have a three-minute execution limit. No task passwords are stored. Task registration fails on pre-existing names. The production repair script receives fake destinations, synthetic journals and temporary `.lnk` locations; no Start Menu/Desktop folder is touched. Only the fake limited-repair process redirects its own `LOCALAPPDATA` to its unique fixture directory. No Hytale process exists or has its environment changed.

Each task repairs a real synthetic Windows shortcut, then rejects a journal pointing outside the fixture's allowed shortcut root while preserving its victim sentinel. The SYSTEM task also calls the production staging helper to create, read and clean a protected fake payload under its actual SYSTEM token. The controller requires a real task completion code of zero and exports the task XML and token identity. A missing interactive logon or disabled Task Scheduler is a failure, not a skip/pass.

## Expected evidence

| Scenario | Required evidence |
| --- | --- |
| Normal UAC | Initiator SID = elevated SID; elevation types 3 (limited) then 2 (full); real prompt manually approved |
| Over-the-shoulder | Standard initiator type 1, different elevated Admin SID type 2; foreign-owned source explicitly rejected |
| Staging | Actual directory owner Administrators, protected DACL; every file's owner/DACL accepts only Administrators/SYSTEM; captured SDDL |
| Unrelated user | Actual distinct standard SID/type 1; read/write/rename/delete positive control succeeds in its own channel; target operations denied with Windows error 5/32/33 |
| Substitution/cleanup interference | Denied source read/write/replacement/deletion/insertion, runner/library overwrite, directory rename/deletion and exclusive-handle acquisition; stage repeats those data-path probes |
| Failure handling | Fake script exits 23; stage disappears; missing-resource partial extraction disappears; occupied-stage and linked-victim checks preserve sentinels |
| Repair | SYSTEM SID `S-1-5-18`, limited User SID without admin membership; expected shortcut target; unsafe journal rejected; sentinel unchanged; task exit 0 |

`Verify` requires every phase's evidence. `evidence\summary.json` is written only after all checks succeed. It does **not** certify the real installer, UAC binary signing/reputation, live authentication, all filesystem interleavings or an OTS installation. JSON evidence records identities, token types, PIDs, timestamps, access error codes, stage ACLs, package hashes and outcomes; never file contents or passwords. Cross-user result files originate from cooperating test actors; this is a validation harness, not tamper-proof remote attestation against a compromised guest administrator.

## Cleanup and failure recovery

The summary records `Kind: actual-vm`, the package's tested source revision/fingerprint, guest Windows/runtime environment and hashes of the fixed required evidence set. Preserve the full cleanup export. Back on the unelevated build host, pass its directory to `Build-Installer.ps1 -ChecksOnly -SecurityValidated -VmEvidence <directory>` as described in [the release gate](../../SECURITY-RELEASE.md). That gate separately validates recorded identities, outcomes and hashes against current source/SDK. Missing evidence stays unverified; a host-only or fabricated schema check is never a guest result.

After all actor processes have exited, run from the elevated Admin guest console:

```powershell
& "$root\bin\Run.ps1" -Action Cleanup -Root $root
```

Cleanup verifies exact task names/actions before stopping/removing them, removes only the run's registry entry, exports a fixed list of evidence files to `C:\ProgramData\ModLauncherSecurityVM-Evidence-<id>`, then deletes only the canonical fixture tree. Linked directories are removed as links, never recursively traversed. No account, real shortcut location or arbitrary task is removed. Exported failed runs may lack `summary.json`; never report those as passes. Copy evidence out only after tests stop, then revert/delete the disposable VM/checkpoint, including the evidence export and disposable accounts.

On timeout or interruption, let the bounded workers stop before cleanup. If designation expired, package hashes/ACLs changed, cleanup is blocked, or a sentinel changed, **do not bypass guards or elevate on the host**. Preserve evidence through the hypervisor and revert the VM checkpoint. Cleanup does not relax source/staging ACLs to force progress. No cleanup routine protects against a malicious administrator controlling the entire guest; the disposable checkpoint is the final recovery boundary.
