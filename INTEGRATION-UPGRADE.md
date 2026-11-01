# Upgrade integration and release blocker

Integrated on 2026-11-01 (Europe/Amsterdam), starting from `eb3bf60eb463eb6c8070bdc6101a919719eca682`. All three repair tasks had completed, and all eight original worktrees were clean before integration. Fetching origin found no newer main or remote development branches. Merge commits preserve `31928b4` (replacement investigation), `71b0271` (worker lifecycle), and `ab95824` (0.1.4 packaging). All four older development branch tips were already ancestors of main. The existing `release-0.1.1-update-fixes` stash remains untouched.

Integration code/check revision: `8cd6877`. Changes align the lifecycle fixture with version 0.1.4, add its standalone journal reproduction to the mandatory safe gate, and correct the expected Windows inherited-DACL representation. The assertion still compares the entire expected owner/group/DACL descriptor, including every ACE and protection flag. Only the expected auto-inherited flag is set for unprotected descriptors, matching Windows' automatic-inheritance conversion; a protected payload DACL is also exercised and must remain exact. No production account behavior or ACL grant is changed. See [Microsoft's inheritance contract](https://learn.microsoft.com/en-us/windows/win32/secauthz/automatic-propagation-of-inheritable-aces).

The focused setup lifecycle passed after integration. Full runs use these unchanged entry points:

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Build-Installer.ps1 -ChecksOnly -RegressionControls
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Build-Installer.ps1 -RegressionControls
```

The packaging command runs in fresh detached sibling `Mod-hytale-release-v0.1.4` at `8cd6877`, after the documented prerequisite restore with a fresh NuGet cache. Its final candidate, checksums, inspection and reports are retained in the original checkout under ignored `dist/release-v0.1.4`. Original-checkout checks are recorded in `dist/integration-checks.log` and `dist/security/20261101-131237-f927aabafa074ee0a957677b82e037d6/report.json`. A run counts as successful only when its completed report has `SafeStatus: passed` and every stage passes; static package inspection must also pass. Consult those reports for final status, exact source fingerprint, SDK, commands and hashes. Documentation-only commits do not change the tested build-input fingerprint.

Existing completed-task evidence and package artifacts are retained locally under ignored `dist/completed-task-evidence` before auxiliary worktree cleanup. Build caches and regenerable compilation directories are not release evidence. Graphify was queried before source exploration and updated after the final code changes; graph output is locally ignored and no graph files are tracked.

## Unmet release gate

No usable disposable Windows VM is available on this host. Windows Sandbox and Hyper-V management are absent, VirtualBox has no executable installed, and the only VM files found are Ubuntu. No genuine older installed release was upgraded with the newly built package. The synthetic worker fixture uses real setup handles and real installer scripts but intercepts elevation and substitutes payloads/system locations; it is not packaged-installer validation. The current VM harness also does not itself run the real installer, so its host-only validation cannot establish that result.

The required next step is an actual disposable Windows run starting from an older installed package and exercising normal setup/UAC, the real worker and file/directory handles, retry after obstruction, same-package reinstall, preserved journals/backups/data/official bytes and ACLs, existing launcher/search integration, and Settings removal. Fresh matching security VM evidence and live account acceptance remain separate policy gates. Never use the user's live Hytale installation for this work.

No v0.1.4 tag or release is created while packaged upgrade acceptance is missing. Existing release downloads remain unchanged; pushing source does not repair v0.1.3 downloads. The integration is therefore source-complete only when its local and GitHub checks pass, and the requested release remains unfinished until this gate is satisfied.
