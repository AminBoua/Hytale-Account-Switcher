# Background repair console fix

Branch: `fix/background-console`, based on main `6a4ebc9`.

## Cause and process trees

The previous SYSTEM and limited Interactive tasks directly started Windows PowerShell every minute, with a one-minute execution limit. `Hidden` controls task listing visibility; PowerShell's `-WindowStyle Hidden` cannot prevent the console allocated when a console-subsystem executable starts. The interactive user action is the desktop-visible launch path. A script failure did not remove the repeating trigger, so it launched again the next minute. Repair-Integration.ps1 itself contains no relaunch or retry loop; it imports the shortcut helpers (including Add-Type), opens the journal and performs COM shortcut repair.

The new tree is Task Scheduler -> installed GUI-subsystem HytaleAccounts.exe -> system PowerShell with UseShellExecute=false/CreateNoWindow=true. The worker branch runs before launcher/session/mutex/UI initialization. No new executable, script payload, shortcut or chooser is introduced. The existing PowerShell script still owns shortcut repair.

SYSTEM starts at boot, the limited Interactive user task at that user's logon; installation explicitly starts both immediately. Task names are unchanged. IgnoreNew prevents overlapping workers, execution time is unlimited, and there are no repetition or restart-on-failure settings. Successful repair passes wait one minute in the existing worker. The first failed pass or 50-second timeout ends the worker with status 1; timeout kills its process tree. Retry requires a subsequent boot/logon, explicit task start, or reinstall. Install accepts exact legacy/current actions, rejects foreign or additional actions, and disables/stops validated tasks before replacing the payload.

Each pass pins the trusted installed directory and validates/holds the executable and all imported repair sources. It uses protected system module discovery and retains the normal Windows profile. Failure diagnostics are created with private, exclusive file creation at the task identity's LocalApplicationData/HytaleAccounts/repair-error-<guid>.txt (SYSTEM uses its own profile). Failure to write diagnostics still yields nonzero Task Scheduler status.

## Required integration on the uninstall branch

Do not ship the combined change until these changes are made:

1. Uninstall.ps1 task validation must accept exactly one action matching either the existing legacy system PowerShell command or the new installed `<destination>\HytaleAccounts.exe` action. Machine arguments must equal `--repair-integration`; per-user arguments must equal `--repair-integration-user`. Keep exact executable/argument comparisons and foreign-task rejection, including legacy support.
2. Validate all owned repair task actions, then disable/stop and wait for BOTH scopes before `Stop-InstalledLauncher $installed` and before acquiring the shortcut journal. A resident repair worker is now an HytaleAccounts.exe process and cannot be closed with CloseMainWindow. Retain bounded waits and failure reporting; do not treat an arbitrary same-name process as an owned task.
3. Keep task removal in the existing authorized uninstall handoff/finalization lifecycle. No Setup.cs or payload-list change is required: RepairWorker.cs compiles into the already packaged executable. Diagnostics live outside the installation directory, not in the payload deletion list.
4. Run InstallerScriptChecks.ps1 in full after integration. Its mocked registration/start behavior has been updated; no real scheduled task or shortcut is used.

## Validation

Passed: `dotnet run --project Checks -- --repair-worker`, `dotnet run --project Checks -- --setup`, Windows PowerShell `Checks/RepairTaskChecks.ps1`, and `Checks/ShortcutRaceChecks.ps1`.

The installer fixture passed missing/unrelated shortcut preflight and ordinary installation, then stopped at the expected old uninstall validation: `Shortcut repair task changed; files preserved.` The remaining full installer scenarios require the integration above.

The real child-process fixture checks GetConsoleWindow()==0 for both repair scopes. Actual Task Scheduler desktop/window visibility was NOT visually tested, and installed tasks and real shortcuts were not changed. The source establishes the console-allocation path; no live installed task/process trace was collected. SYSTEM execution and real boot/logon triggers require disposable Windows integration testing.
