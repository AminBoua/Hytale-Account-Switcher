# Settings uninstall regression coverage

## Integration on main (2026-11-01)

Both task branches are merged. The entry fixture now intercepts the actual
`UninstallHandoff.Start` boundary, simulates re-entry under an unelevated context,
and requires exactly one handoff and one removal. An unavailable handoff must
throw before any removal. The fixture never launches the native token handoff;
it does not prove the SID/token checks below. It is included in the safe build
gate. No production test seam was needed.

Production uses only the elevated caller's linked limited token and requires
matching caller, linked-token and desktop SIDs, matching nonzero sessions, a
limited elevation type, no administrator role, and the machine journal's owner
SID. It pins trusted installed payload files and ancestors before hidden launch,
and creates the child's environment from the selected user's token. There is no
fallback to another user's token or elevated per-user restoration.

The package inspector requires the bundled managed assembly to match the current
published intermediate assembly and contain `UninstallHandoff`, in addition to
the existing script, runtime, version and ZIP byte checks.

Actual Windows Settings entry, linked-token acquisition, normal profile loading,
UAC cancellation and alternate-administrator scenarios remain manually
unverified. The disposable-VM procedure below remains required; synthetic
fixtures do not establish actual UAC/security validation. No real uninstall or
account-data mutation is authorized or performed.

Integration validation: `Build-Installer.ps1 -RegressionControls` passed all
36 safe/negative-control checks (report
`dist/security/20261101-153533-5c6da148d2eb4746bea0f3be273160dd/report.json`).
Package inspection passed; the package regression fixture initially lacked the
newly required compiled assembly. The fixture now copies that assembly and
checks that a mismatched assembly is rejected. Final packaging reruns the safe
gate with this fixture correction; its matching report and artifact hashes are
recorded in `dist/SECURITY-STATUS.json`, `dist/RUNTIME-PACKAGE.json` and
`dist/SHA256SUMS.txt`. These generated artifacts are local and ignored by Git.

The remaining sections record the original test-branch baseline before
integration; their RED result and proposed future seam are historical.

Base: `main` at `3ab8eb7`. Worktree branch: `test/settings-uninstall`.
Only Checks sources are changed. No production fix is included.

## Executable coverage

- `SettingsUninstallEntryChecks.ps1` compiles a disposable copy of the actual
  `Setup.Run` entry logic. It substitutes elevation detection, destination and
  removal dispatch; every remaining Process.Start is replaced by a throwing
  interceptor. It runs both entry states instead of asserting source text.
  Ordinary entry must dispatch once and return zero. Elevated entry must reach
  safe user-context removal; direct elevated removal also fails the probe.
  Current main returns the existing Settings rejection, so this test is RED.
- The existing `UninstallFinalizationScriptChecks.ps1` suite executes production
  Setup.Remove, Install.ps1, Uninstall.ps1 and repair scripts on disposable files,
  real fixture .lnk files and an isolated HKCU entry. Task and process discovery
  are intercepted by the existing fixture. No installed addon is invoked.
- Added WrongUser: change only the disposable machine journal's owner SID, then
  exercise real per-user preparation and recovery. Both must fail with the
  production owner-mismatch error. No machine launch may occur. Payload,
  journals, backup bytes, mocked tasks and uninstall registration must survive.
- Added Unavailable: the injected user executor fails both preparation and
  recovery. Setup.Remove must report both failures and never launch removal;
  the same recovery assertions apply. This simulates executor unavailability,
  not Windows shell token acquisition.
- Reused Cancelled, StartupFailure, Locked and NoConfirmation cases, including
  real native sharing violation 32, missing/mismatched confirmation, timeout,
  failed parent and parent-not-exited cases. Each must preserve recovery state
  and permit a subsequent successful retry.
- Added synthetic world, account.dat, opaque saved-account snapshot and key
  sentinels under the fixture's local directory. Failure and success must retain
  their exact hashes. Successful retries also restore exact user/common shortcut
  bytes, retain the official fixture executable and unrelated files, and remove
  addon payload, uninstall entry, mocked tasks and user recovery journal.

## Required production integration

The entry probe's source substitutions are guarded fixture boundaries, not a
user-context handoff implementation. It deliberately does not claim to simulate
an Explorer token. Removing the current rejection alone still fails because it
would dispatch removal while Elevated remains true.

The production branch needs a narrow injectable entry dispatcher: destination,
entry elevation/identity, existing user integration and worker launch callbacks,
and a user-context handoff callback reporting the selected SID and whether that
context is elevated. Keep the real token acquisition/validation in production.
The test should intercept that callback and run the existing Remove fixture only
when the returned context is unelevated and matches the journal owner. An
unavailable or different-SID result must stop before file mutation. Rebind the
entry probe to this seam when it exists; do not add a fake handoff to make it green.
Current automated WrongUser and Unavailable cases cover downstream recovery only,
not identity validation at that future entry boundary. The entry regression is
standalone and intentionally not wired into the passing main build gates yet.

## Exact manual token/UAC check (disposable Windows VM only)

1. Take a VM snapshot. Use a normal Windows profile with Explorer running. Install
   an official Hytale launcher and the candidate addon in the VM. Use synthetic
   accounts/world data only. Record the installing user's SID, exact shortcut
   bytes, official launcher hash, user and machine journals and backup hashes.
2. Open Windows Settings > Apps > Installed apps > Hytale Accounts addon >
   Uninstall. At the entry breakpoint/log, verify its actual token is elevated.
   Verify the handoff selects the installing user's interactive, unelevated token
   and normal profile. User shortcut restoration must execute under that SID;
   machine removal alone may elevate. No separate chooser or uninstall shortcut.
3. Cancel UAC. Verify addon files, uninstall entry, journals and backups remain
   usable, and shortcut repair/retry works. Repeat with the user shortcut held
   open without write/delete sharing across finalization. Verify machine removal
   never starts deleting payload/recovery files after finalization fails.
4. Reset the VM snapshot. Repeat through Settings with a different interactive
   account/over-the-shoulder administrator credential. Verify no other user's
   profile or shortcuts are touched. With no matching unelevated interactive
   shell available, removal must fail safely and retain recovery state. Do not
   fall back to running user restoration with the elevated/different-user token.
5. Reset and complete same-user Settings removal. Verify both shortcut sets match
   the original bytes, addon payload/tasks/Apps entry are removed, official Hytale
   is usable, and synthetic Hytale/account/world/key data hashes are unchanged.
6. Reset and invoke the installed --uninstall normally with an unelevated token.
   Repeat the success assertions. Confirm no additional launcher UI or entry.

These manual checks were not run on the host. Never use the host's installed
addon, real shortcuts or real account data for this validation.

## Commands and observed results (2026-11-01)

Run from this worktree:

```powershell
python -m graphify extract . --code-only
python -m graphify query "uninstall fixtures elevated recovery finalization"
python -m graphify path Setup UninstallFinalizationChecks
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Checks/SettingsUninstallEntryChecks.ps1
dotnet build Checks/Checks.csproj -c Release --nologo -p:NuGetAudit=false
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Checks/UninstallFinalizationScriptChecks.ps1
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "& './Checks/UninstallFinalizationScriptChecks.ps1' -Scenarios @('WrongUser','Unavailable')"
python -m graphify update .
git diff --check
```

- Entry harness build: PASS, zero warnings/errors. Ordinary entry: PASS.
  Elevated entry: expected FAIL (exit 1), exact current-main error:
  `Remove the addon normally from Windows Settings so your shortcuts are restored as your Windows user.`
- Checks Release build: PASS, zero warnings/errors.
- Full six-scenario Windows PowerShell suite: PASS (exit 0), including six
  successful retries. Follow-up reruns the two added scenarios after adding
  explicit failed-removal data-hash assertions (result below).
- Graphify extraction/query/update: completed. Directed path query found no path;
  fixtures were identified by query. Generated graph files are ignored and are
  not included in this Checks-only commit. Whitespace check: PASS.
- One extra invocation directly from PowerShell 7.6.5 failed the existing
  `confirmed finalization still requires parent exit before machine mutation`
  assertion. It is not counted as a pass; use Windows PowerShell (`powershell.exe`)
  as above, matching the production script host. Cross-runtime compatibility
  was not investigated in this Checks-only task.

No real installation removal, real shortcuts, real accounts, actual UAC prompt,
production source edits, push or merge were performed. Original worktree and the
existing fix/settings-uninstall worktree were left untouched.
- Follow-up Windows PowerShell WrongUser/Unavailable run: PASS (exit 0), including
  explicit failure and success data-hash assertions and successful removal retries.
