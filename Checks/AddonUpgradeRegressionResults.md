# Addon upgrade regression coverage

Base: local `main`, `eb3bf60eb463eb6c8070bdc6101a919719eca682`.
Branch: `fix/addon-upgrade-regressions`.
Worktree: sibling `Mod-hytale-launcher-addon-upgrade-regressions`.
Production files are unchanged. The original checkout was not edited.

## Added coverage

`SetupUpgradeChecks.CheckLifecycle` reuses the existing disposable setup-worker
build and `SecurityFixture`. Its resources contain the real installer scripts,
with fixture-only path/registry/task/process boundaries and payload version
comments. Synthetic helper/runtime files are never executed. The installed Apps
version advances from fixture `0.1.1` to `0.1.2`.

The production `Setup.RunElevated`, `SetupPayload` extraction, source handles,
launcher-directory pins, existing-destination mutation pins, `InstallStart`,
and installer file operations remain in use. Both installer phases run in hidden
Windows PowerShell children; the fixture explicitly requires Desktop 5.1.
The existing runner already builds and invokes this class, including from
`Run-SecurityChecks.ps1`; no runner changes are needed.

The case covers:

- Fresh installation with original common/user shortcut backups and both journals.
- Different helper, runtime and script bytes for the newer addon, asserted before
  the attempted upgrade and compared with installed bytes afterward.
- An exclusive destination-file lock: setup rejects with Win32 error 32 before
  publication, preserving the old payload, journals, backups, shortcuts and Apps
  removal registration/version. Disposing the lock allows a successful retry.
- A same-version reinstall, exact payload bytes, updated Apps version/removal
  command, and absence of transaction/staging leftovers.
- Original shortcut backup and journal bytes, installed shortcut bytes, official
  launcher bytes/security descriptor, opaque account/key/game/world/credential
  sentinels, and exact payload security descriptors across upgrade/reinstall.

Existing `InstallerScriptChecks.ps1` already covers partial publication,
rollback failure, killed upgrades, retained backups, retries, removal, and task
ownership. Those cases were run rather than duplicated.

## Commands and results (Windows, 2026-11-01)

Run from this worktree, unelevated:

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Checks/InstallerScriptChecks.ps1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Checks/SetupUpgradeRegressionChecks.ps1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Checks/SetupUpgradeRegressionChecks.ps1 -Mutation DestinationPin
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Checks/ShortcutRaceChecks.ps1
```

| Check | Result |
| --- | --- |
| Existing installer script fixtures | PASS, exit 0 |
| Original setup upgrade checks, before additions | PASS, exit 0 |
| Extended setup upgrade checks | FAIL, exit 1, exact payload descriptor assertion below; fixture build succeeds with zero warnings/errors |
| Existing DestinationPin negative control | Expected FAIL, exit 1; all four expected publication failures are present |
| Existing shortcut race checks | PASS, exit 0 |

### Preserved baseline failure

The extended setup check passes fresh installation, lock rejection, newer-payload
retry, both journal/backup/shortcut preservation checks, official launcher/data
preservation, same-version payload bytes, Apps registration and staging cleanup.
It then fails against unchanged production at:

```text
Lifecycle upgrade/reinstall changed payload ACL: Install.ps1; expected ...D:(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FA;;;<fixture-user>); actual ...D:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FA;;;<fixture-user>)
```

The full diagnostic includes owner/group SIDs. They and the access-control
entries are identical; the observed difference is the auto-inherited DACL flag
(`AI`). This is an exact security-descriptor assertion, not evidence of changed
effective access rights. It compares the fresh payload descriptor with the
descriptor after upgrade and reinstall. The assertion is retained; production
was not patched to make this branch green.

### Reported replacement exception

The normal lifecycle does **not** reproduce the reported replacement exception
on this base. The existing `DestinationPin` mutation deliberately removes
`forMutation: true` in the disposable worker and reproduces it exactly:

```text
Installation failed. ... FAIL setup pin existing journal publication: Exception calling "Replace" with "3" argument(s): "Unable to remove the file to be replaced."
```

This negative control also rejects existing/missing payload publication and
missing-journal publication. Its miniature installer is a focused handle control;
it is not presented as proof of the complete packaged installer. The added
lifecycle separately runs the real installer under the production handle
arrangement.

## Integration follow-up and limits

Combine these tests with the installer repair and rerun the commands above:
normal checks must pass and `DestinationPin` must still fail with its existing
four-error signature. Resolve or explicitly review the preserved payload DACL
metadata failure during integration; do not mistake it for the reported file
replacement error or claim this branch is green.

The existing fixture maps administrator trust to its current user and intercepts
elevation. Task registration and process discovery are mocked; registry writes
use a unique disposable HKCU key. No real Hytale files, accounts, machine-wide
registration, tasks, launcher UI, UAC, signed package or real helper execution
were used. A packaged, elevated upgrade and Windows Settings removal still need
verification in a disposable Windows environment after integration.

Graphify was created with `python -m graphify extract . --code-only`, queried
before source exploration, and refreshed after code changes with
`python -m graphify update .`. Generated graph files are local context, not part
of this test-only commit.
