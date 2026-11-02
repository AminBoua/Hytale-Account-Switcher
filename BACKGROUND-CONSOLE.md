# Background repair console fix

Branch: `fix/background-console`, based on main `6a4ebc9`.

## Cause and process trees

The previous SYSTEM and limited Interactive tasks directly started Windows PowerShell every minute, with a one-minute execution limit. `Hidden` controls task listing visibility; PowerShell's `-WindowStyle Hidden` cannot prevent the console allocated when a console-subsystem executable starts. The interactive user action is the desktop-visible launch path. A script failure did not remove the repeating trigger, so it launched again the next minute. Repair-Integration.ps1 itself contains no relaunch or retry loop; it imports the shortcut helpers (including Add-Type), opens the journal and performs COM shortcut repair.

The new tree is Task Scheduler -> installed GUI-subsystem HytaleAccounts.exe -> system PowerShell with UseShellExecute=false/CreateNoWindow=true. The worker branch runs before launcher/session/mutex/UI initialization. No new executable, script payload, shortcut or chooser is introduced. The existing PowerShell script still owns shortcut repair.

SYSTEM starts at boot, the limited Interactive user task at that user's logon; installation explicitly starts both immediately. Task names are unchanged. IgnoreNew prevents overlapping workers, execution time is unlimited, and there are no repetition or restart-on-failure settings. Successful repair passes wait one minute in the existing worker. The first failed pass or 50-second timeout ends the worker with status 1; timeout kills its process tree. Retry requires a subsequent boot/logon, explicit task start, or reinstall. Install accepts exact legacy/current actions, rejects foreign or additional actions, and disables/stops validated tasks before replacing the payload.

Each pass pins the trusted installed directory and validates/holds the executable and all imported repair sources. It uses protected system module discovery and retains the normal Windows profile. Failure diagnostics are created with private, exclusive file creation at the task identity's LocalApplicationData/HytaleAccounts/repair-error-<guid>.txt (SYSTEM uses its own profile). Failure to write diagnostics still yields nonzero Task Scheduler status.

## Integration on main

Main now accepts exactly one legacy PowerShell or current GUI worker action for each repair scope. Uninstall validates all actions before changing any task, then disables/stops and waits for both scopes before stopping the installed launcher or opening the journal. Task removal remains after authorized user finalization and journal validation. Foreign actions and bounded stop failures preserve the payload.

RepairWorker.cs is compiled into the existing WinExe. SetupPayload.Scripts, embedded script resources, installer publication and uninstall deletion lists already include every required script; no new payload is needed. Upgrade fixtures retain legacy-action migration coverage. RepairTaskChecks.ps1 additionally exercises uninstall's production task block for legacy/current actions, foreign/additional actions and either worker failing to stop.

## Validation

Passed: `dotnet run --project Checks -- --repair-worker`, `dotnet run --project Checks -- --setup`, Windows PowerShell `Checks/RepairTaskChecks.ps1`, and `Checks/ShortcutRaceChecks.ps1`.

Before integration, the worker branch installer fixture passed missing/unrelated shortcut preflight and ordinary installation, then stopped at the expected old uninstall validation: `Shortcut repair task changed; files preserved.` The integrated installer and setup-upgrade fixtures now pass. Finalization and setup-upgrade fixtures also intercept the new task-start call, and upgrade assertions enforce the GUI-worker action and absence of scheduled failure restarts.

The real child-process fixture checks GetConsoleWindow()==0 for both repair scopes. Actual Task Scheduler desktop/window visibility was NOT visually tested, and installed tasks and real shortcuts were not changed. The source establishes the console-allocation path; no live installed task/process trace was collected. SYSTEM execution and real boot/logon triggers require disposable Windows integration testing.
