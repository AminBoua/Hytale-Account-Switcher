# Setup file-sharing investigation — 2026-11-01

**Status: diagnostic repair and focused regressions complete; the original
installation's lock owner and a corrective handle-lifetime change remain blocked.**
The supplied dialog cannot identify its failed path or owner. No naturally
occurring installer self-conflict was reproduced, so this branch does not claim
to fix the original installation failure or change sharing permissions.

- Branch: `fix/setup-file-sharing`
- Worktree: `C:\Users\moham\Documents\ChatGPT\Mod-hytale-launcher-setup-file-sharing`
- Base: `4578eb31f6ab5a73a803a0bf97f010339de27111` (local `main`)
- Final code commit: `d4ae7b7865e5efe1f0123ddb6b2642d49a5d53ab`
- Final report commit: resolve with `git log -1 --format=%H -- SETUP-FILE-SHARING.md`.

The original checkout remains at the base with a clean tracked working tree.
No merge, push, publication, branch deletion or worktree removal was performed.

## Package and reproduction

Read-only inspection of the user-supplied `dist/release-v0.1.4` package matched
its manifest and executable SHA-256
`8D1A73FDFA6DDD0B27C2BDB45A4B7D0339FEA6C67F291A4F6FBA83F21FBBE326`.
Its provenance records the base commit. The existing static package reader,
compiled in a disposable wrapper, verified all eight embedded script/source
resources against that checkout. The executable was never launched.

The real `Setup.RunElevated` worker and its real Windows PowerShell 5.1 child
pass fresh installation, upgrade to different payload bytes, and same-package
reinstall in the existing disposable lifecycle fixture. This fixture redirects
system locations, elevation, tasks/process discovery, resources and registry
registration; it never executes a helper or official launcher.

The added regression reproduces the exact `Installation failed. Exception
calling ".ctor" with "2" argument(s)` message by holding a read-sharing handle
on the fixture's `.hytale-accounts-addon\Repair-Integration.ps1`. It runs twice:

1. A deliberately seeded `SetupPayload.OpenSource` reader in the worker's
   process (final run PID 30976). This models a leaked setup-owned source pin;
   it does **not** establish that production actually leaks one.
2. A separate hidden Windows PowerShell holder (final run PID 29032), released
   through redirected stdin, with no sleeps or retry loops.

Both fail at `Open-ShortcutFile` -> `ShortcutFile(string, bool)` ->
`OpenRelative` -> `NtCreateFile`, returning Windows error 32. The rejected leaf
open requests `0xC0110000` (read/write/delete/synchronize), sharing none. The
existing handle requests read access and shares only read: the handles are
incompatible in both directions. The `FileStream(SafeFileHandle, FileAccess)`
constructor is never reached in these reproductions. The opening process is
the worker's installer PowerShell child, not the holder process.

## Proven defect and repair

The native open exception had neither the full path nor constructor caller in
its message. `Install.ps1` then saved only `ErrorRecord.ToString()`, losing
underlying exception and script-stack details before `Setup.RunElevated` read
and surfaced the temporary diagnostic. Either ownership scenario produced the
same unhelpful dialog; that dialog cannot prove an unrelated process owns a lock.

`ShortcutFile` now adds the attempted full path, numeric Windows error and retry
guidance while retaining the original `Win32Exception` as its inner exception.
The installer saves the exception chain, script stack and opening PowerShell PID.
No account/session files are read or dumped. The code does not identify an
arbitrary live holder from its PID; the fixture knows its holders by construction.

Inspected `Setup.cs`, `SetupPayload.cs`, `Install.ps1`, `ShortcutFile.cs`, journal
open/save/read paths and all constructor/replacement callers. No change was
justified to source pins, trusted-directory mutation pins, path/link/identity or
ACL checks, atomic replacement, rollback, retained backups, helper-last ordering,
authentication or UI. Install/repair/uninstall keep their shared constructor.
In particular the existing journal directory-pin repair addresses error 1175,
not this constructor error. Storage permission policy is untouched.

## Checks and limits

Unelevated Windows PowerShell `5.1.26100.9818`; all filesystem/registry changes
were disposable fixtures. Commands below use `powershell.exe -NoProfile
-NonInteractive -ExecutionPolicy Bypass -File`:

| Check | Result |
| --- | --- |
| New regression with original production code | Expected exit 1: exact constructor message, missing path/native cause/caller |
| `Checks/SetupUpgradeRegressionChecks.ps1` after repair | Exit 0; 0 build warnings/errors; fresh/upgrade/reinstall, both constructor ownership controls, exact payload ACLs, recovery bytes, registration and staging cleanup pass |
| `Checks/SetupUpgradeRegressionChecks.ps1 -Mutation DestinationPin` | Expected exit 1; payload/journal publication fails with restrictive directory pin |
| `Checks/ShortcutRaceChecks.ps1` | Exit 0; 213 PASS assertions |
| `Checks/UpgradeJournalReplacementChecks.ps1` | Exit 0; 11 PASS assertions |

Both constructor obstructions retain the old payload/ACLs, both journals,
shortcut backups and older removal registration. Removing the obstruction permits
successful upgrade and reinstall. The existing exclusive-reader control also
rejects at worker preflight before publication. `git diff --check` passes.
Graphify extraction/query preceded source exploration; `python -m graphify
update .` completed after code changes. Ignored local logs retain the runs.

**Remaining validation:** the original failing path, process and handle pair
require a captured diagnostic or a reproduction in a disposable Windows guest
using this exact package and an older installed package. The user's live Hytale
installation, accounts and data were neither inspected nor tested. No live UAC,
real scheduled repair/helper contention, privileged cross-user matrix or
packaged installer acceptance was run. Source fixtures and static resource
inspection do not establish packaged installer acceptance. Broad regression,
packaging and storage policy work remain with the other tasks. This branch is
an evidence/diagnostic improvement, not a confirmed root-cause replacement fix.
