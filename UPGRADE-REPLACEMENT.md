# Existing addon upgrade replacement investigation

Investigated on 2026-11-01 in the sibling worktree
`Mod-hytale-launcher-addon-upgrade-replacement`, branch
`fix/addon-upgrade-replacement`, from local main
`eb3bf60eb463eb6c8070bdc6101a919719eca682`.

## Result

The production repair already exists in the starting main, in commit
`cbf3621334dca4f6f8aea58e93191d1501083ea4` (2026-10-29). No further
behavior change is needed for the reproduced failure. This change documents
the requirement at the production call site and adds one standalone regression
for the exact journal failure; it does not duplicate or replace that repair.

The local release tag `v0.1.3` resolves to commit
`4a9254dea0c281d0bd8850d655c7d8391e2776c3` (2026-10-25). Its
`Setup.RunElevated` holds the installed addon directory with the default
read-sharing `PinnedDirectories` mode throughout the installer child process.
Although `forMutation` is defined in that version, this call does not enable it.
Main enables `forMutation: true` at this call site.

## Proven failure path

1. `Setup.RunElevated` pins the existing addon directory, starts the hidden
   Windows PowerShell installer, and waits while retaining that pin.
2. `Install.ps1` loads the existing restoration journal, writes the addon payload,
   then calls `Save-ShortcutJournal` before registering the Windows Apps entry.
   The tagged installer writes payload files through existing handles; main instead
   stages siblings and publishes them atomically through `ShortcutFile.ReplaceFrom`.
3. `Save-ShortcutJournal` writes and closes `shortcuts.new`, then invokes the
   three-argument `[IO.File]::Replace` on an existing `shortcuts.json`.
4. With the released read-sharing directory pin, that actual function reproduces
   Win32 error 1175 / HRESULT `0x80070497`, including the reported PowerShell
   `"Replace" with "3" argument(s)` wrapper. No executable or destination
   file handle is open in the focused fixture.
5. Acquiring a read/write-sharing directory pin before releasing the restrictive
   pin permits the same atomic publication with the same ACL. Directory rename
   remains blocked because delete sharing is still denied.

The same failure is reproduced through the real setup worker by the existing
`DestinationPin` regression control. Unmodified main passes that check.

## Safety and scope

`PinnedDirectories` first excludes existing directory writers, validates regular
paths and trusted ACLs, then acquires the mutation pin before releasing its
restrictive handle. None of these checks or permissions were weakened.

`ShortcutFile.ReplaceFrom` flushes staging and releases both leaf handles before
`File.Replace`, retains directory pins, then reopens and validates both leaves in
`finally`. Its callers are installer publication and rollback. They retain the
existing recovery-backup and helper-last commit behavior. No changes are needed
to those callers, their fixtures, exports or configuration.

`Save-ShortcutJournal` is also called by shortcut update and legacy restoration
record adoption; install, repair and uninstall share the journal locking and
shortcut code. Their behavior is unchanged. Path/link checks, protected-directory
checks, original shortcut records, official launcher bytes/ACLs, and account/game
data remain under the existing safeguards.

[Microsoft's ReplaceFile contract](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-replacefilew)
requires access for replacement and ACL preservation, including DELETE and
WRITE_DAC on the replacement file. Writable bytes alone are insufficient. This
repair changes handle sharing, not ACL grants, and retains atomic replacement.

Windows PowerShell 5.1 converts ordinary `$null` to an empty string for a string
parameter. Both the tag and main already use `NullString.Value` for the optional
backup name, correctly passing true null. C# `File.Replace(..., null)` is also
correct. No null-binding workaround or delete-then-copy fallback was added.

## Checks

Run from the sibling worktree, unelevated, with Windows PowerShell
`5.1.26100.9818`; all installation paths, registry entries and task mocks belong
to disposable fixtures. No real Hytale installation was used.

| Command (after `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`) | Result |
| --- | --- |
| `Checks/UpgradeJournalReplacementChecks.ps1` | Exit 0; 11 PASS assertions: exact error/HRESULT, null binding, retained journals, original protected ACL, restoration record, stage consumption, and directory pin |
| `Checks/SetupUpgradeRegressionChecks.ps1` | Exit 0; build 0 warnings/errors; 7 PASS assertions, including real worker existing/missing publication and parent preflight ordering |
| `Checks/SetupUpgradeRegressionChecks.ps1 -Mutation DestinationPin` | Expected exit 1; existing payload and journal publication fail with error 1175; missing payload and journal publication fail with sharing violations |
| `Checks/InstallerScriptChecks.ps1` | Exit 0; 1661 PASS assertions, including upgrade rollback, retained recovery backups, forced interruption/retry, original shortcut restoration, official bytes/ACLs, and Hytale data preservation |
| `Checks/ShortcutRaceChecks.ps1` | Exit 0; 213 PASS assertions covering links, identities, held handles, restoration and cleanup |

`python -m graphify extract . --code-only` and queries were used before broad
exploration; `python -m graphify update .` succeeded after code changes. Graph
output is excluded by the repository's shared local exclude file and is not
tracked. `git diff --check` passed. The new focused check is standalone; the
parallel regression task owns broader test/runner additions.

## Limits

The release comparison is against the local `v0.1.3` source tag, not a supplied
downloaded executable. GitHub's latest-release API returned HTTP 404, so the
currently distributed artifact was not identified or inspected. The exact
reported path and error are reproduced, but an unknown executable's provenance
cannot be established from its dialog alone. Existing downloaded release bytes
are not repaired by this branch; a future installer must be built from main
including `cbf3621` or later. No installer artifact was published or installed,
and no live UAC, multi-user VM, authentication or launcher runtime was tested.
