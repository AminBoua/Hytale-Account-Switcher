# Settings uninstall access failure — 2026-11-03

Base: `main` at `6a4ebc9`. Branch: `fix/uninstall-access`.

## Reproduced failure and fix

The native diagnostic reproduced Win32 error 5 at `CreateProcessWithTokenW`
with the original linked-token launch, after the SID/session checks succeeded.
The executable was protected system `cmd.exe /c exit 0`, not the installed addon.
Thus this reproduction does not depend on addon ACLs, open shortcut files or tasks.
Changing profile flags alone did not fix it. A primary duplicate of the validated
desktop token successfully launched the hidden child with profile loading retained.

`TokenLinkedToken` is useful for identity checks but can be identification-only;
it is not a reliable process launch token. The fix retains those checks and obtains
the launch token with `DuplicateTokenEx` from the same user's desktop primary token.
It additionally requires that desktop token to be primary, limited, non-admin and
in the caller's nonzero session. No alternate-user fallback or ACL changes exist.
`MAXIMUM_ALLOWED` requests handle access on the new token; it does not grant new
privileges or change any token/file DACL.

The [Windows process-launch contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createprocesswithtokenw)
requires a primary token. The child environment comes from the selected token,
with `LOGON_WITH_PROFILE` and the existing hidden launch flags retained.

## Path traced in current code

* `Install.ps1` registers the existing HKLM Apps entry as the installed
  `HytaleAccounts.exe --uninstall`; the local registration was inspected read-only.
* `App.OnStartup` calls `Setup.Run(true)`. Elevated entry calls `UninstallHandoff`;
  ordinary entry calls `Setup.Remove`. There is no separate account UI.
* Handoff retains trusted ancestor pins, read-only payload pins denying write/delete,
  regular-file checks and machine-journal owner validation across process creation.
  Directory write sharing remains enabled for journal replacement. All handles are
  released when handoff returns; no file-sharing change was needed.
* The limited child prepares user shortcuts with the journal retained, then asks
  UAC for the machine worker. Failed startup/cancellation invokes user recovery.
  Final user restoration completes before the protected confirmation event is set.
* `Wait-RemovalFinalization` requires confirmation, acknowledges it, then waits for
  that same parent to exit successfully before machine mutation. Missing/failed
  finalization preserves retry state. `Uninstall.ps1` checks owner and task actions,
  restores original shortcuts and opens the complete deletion set before deletion.
  Its deletion list excludes Hytale, account snapshots, keys and worlds.

## Regression checks

`UninstallHandoffChecks.ps1 -RunElevated` compiles a disposable copy of production
handoff code. It bypasses only installed payload validation and substitutes system
`cmd.exe /c exit 0`. The child starts suspended so the test can inspect its actual
token before resuming it. It must have the caller's SID/session, primary type,
limited elevation and no administrator membership, then exit zero. This passed
with actual host UAC consent. `-RunElevated -LinkedToken` restores the old launch
argument and fails with native error 5 (expected negative control).

This check is deliberately opt-in, outside the unelevated safe build gate. Without
`-RunElevated`, run it from an already elevated diagnostic console; otherwise it
fails explicitly. It never launches the real addon or runs uninstall scripts.

Other checks run: Release build of `Checks/Checks.csproj`, Settings entry fixture,
C# `--safe` suite, and all six uninstall-finalization scenarios (Locked, Cancelled,
StartupFailure, NoConfirmation, WrongUser, Unavailable), including successful
retries and exact shortcut/data preservation assertions. See local ignored logs
under `dist/uninstall-diagnostic/` for execution output.

## Integration and limits

No scheduled-task implementation change is required by this token fix alone. Main
now jointly updates uninstall validation for the GUI repair worker and these legacy actions:
`HytaleAccounts-Repair` and `HytaleAccounts-Repair-S-1-*`, system PowerShell,
`-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File
"<destination>\Repair-Integration.ps1"`, with ` -UserOnly` for user tasks.
Current GUI actions use the installed `HytaleAccounts.exe` with exactly
`--repair-integration` or `--repair-integration-user`. Both task scopes stop before
launcher shutdown and journal access; foreign actions are rejected before task mutation.
Recurring repair must respect the journal lock and stop when its journal is gone;
it must not recreate user integration after finalization. Real task permissions
and registration remain owned by the other branch and were mocked in these tests.

Actual Settings-driven uninstall, full installed-payload launch, alternate-admin
credentials, absent desktop, profile unload and cross-user Windows enforcement
were not exercised. Host UAC validation here proves only the native handoff with
a harmless child. No real installation, shortcuts or accounts were modified.
End-to-end validation still belongs in a disposable Windows installation.
