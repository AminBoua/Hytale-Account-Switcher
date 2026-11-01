# Setup/storage integration — 2026-11-01

## Preserved source and scope

Original main and origin/main were `4578eb31f6ab5a73a803a0bf97f010339de27111`.
All three repair chats had completed before integration. Their final reports and
committed investigations were read; all four worktrees were clean. Fetch found
no new upstream commits or additional remote branches. The only auxiliary local
branches were the three requested repairs. Existing stash
`a9db34b978e6aeb7e3c39c7e494d7a51ea6e9b90` (`release-0.1.1-update-fixes`)
remains intact and was not applied or dropped.

All repair tips are ancestors of integrated source `8676b4f` through merge commits:

- `bd743be65c87cf8fded24cd6c87e5a5c53593d95`: setup sharing diagnostics and probes.
- `b2975faa78bce29e8249196df2e6e228860dd919`: storage diagnostics and ACL matrix.
- `7e2d0ea4e62f9cce17ce437b2603bf55e24936d0`: release regressions and 0.1.5 metadata.

The lifecycle fixture conflict was resolved by retaining both real-handle
obstructions, exact recovery/data ACL checks and complete Apps registration
comparisons. Both obstructions now receive those combined assertions. Production
handle/path/security policy, account UI and runtime behavior were not broadened.
The package and installed Apps version are consistently 0.1.5.

Graphify was queried before source exploration and updated after the code merge.
Its output is ignored; no tracked graph files exist to commit.

## Unresolved findings and publication decision

The original constructor sharing failure is **not a confirmed root-cause fix**.
The probes reproduce both a deliberately retained setup reader and an external
PowerShell reader; they establish recoverable rejection and successful retry,
not which actor held the original installation's lock. New diagnostics retain
the attempted path, Windows error, inner exception, script caller and opening PID.
See [SETUP-FILE-SHARING.md](SETUP-FILE-SHARING.md).

Storage rejection is correct: explicit ACE 0 on the reported Hytale root grants
BUILTIN\Users Full Control, mask `0x001F01FF`, applying to the directory and
inheritable by children. No deny cancels its mutation rights. Live permissions
were not changed. The precise reviewed remediation proposal removes only that
explicit ACE using a non-propagating DACL-only update, retaining every other ACE,
owner, group and protection state. Child descriptors need separate review if
subsequently rejected. See [STORAGE-PERMISSION-REJECTION.md](STORAGE-PERMISSION-REJECTION.md).
The live startup blocker remains unresolved until that remediation is performed.

No registered VirtualBox VM or designated disposable Windows checkpoint was
available. The user subsequently requested source integration/checks, an
unpublished test candidate, and no release publication. No genuine packaged
older-release upgrade, real UAC/cross-user matrix, launcher/account/Play acceptance
or later real Settings removal was performed. Synthetic fixtures and static
package inspection cannot establish these outcomes. SecurityValidated remains
false. No release/tag/assets are published or overwritten.

## Evidence retention

Repair-worktree Markdown reports, logs and all dist contents were copied and
SHA-256 compared before cleanup under the original checkout's ignored
`dist/completed-task-evidence/setup-storage-integration-20261101/`.
Per-task manifests record every retained file hash: 44 setup-sharing files,
225 release-regression files and nine storage files. This includes the earlier
branch-only candidate, completed/failed safe reports, and interrupted-fixture
evidence. Those historical artifacts do not validate the integrated candidate.

The prior task's interrupted synthetic temporary tree and HKCU marker remain
preserved as documented in SETUP-RELEASE-REGRESSIONS.md; cleanup had been rejected
by automatic approval review. No bypass or live-Hytale cleanup was attempted.

## Combined checks and candidate

The required original-checkout command passed all **35/35** stages (18 positive
stages and all 17 expected mutation failures), exit 0:

```powershell
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File Build-Installer.ps1 -ChecksOnly -RegressionControls
```

A fresh detached release worktree at integrated source `8676b4f` runs the complete
repository build procedure with `-RegressionControls`; its actual EXE/resources,
versions, native sidecars, ZIP membership and nine checksums must pass inspection.
This is a test distribution, not packaged installer acceptance.


Both original and fresh-build runs passed 35/35, including upgrade/recovery,
storage/ACL/races, runtime bootstrap, evidence gates and all mutation controls.
The fresh build also passed eight package-control invocations (two baselines and
six expected rejections). No assertion or security gate was disabled.

Original-checkout report:
`dist/security/20261101-144938-959c76ad1d494b36a32c713c648d4330/report.json`,
SHA-256 `64200E4B0806AA6A7AC9DB793E61C97E3DF74EE6599AFBD7DF0E2892422E72ED`.
Source fingerprint `7234948447A27C225FD157911B8C2A5EE06BCBF16D6E6894D6AC46F5B3C975FC`.

Fresh-build report, retained under `dist/test-v0.1.5/security/`:
`20261101-145031-a8beab393eb140bca363e4c9cb8f636d/report.json`,
SHA-256 `D1CFDA5E21C5BCBE853589B67AC73FAC15DCFCFC2BC829EF7880ABC9FF48E235`.
Source fingerprint `76EED111E92FCDE14C5F6C2E0AE9F8F1B990EEF060AA372ACA1359079B699A69`.
Both started from clean integrated revision `8676b4f04726b54a6728a3bdedee851e04e59f24`;
raw working-file line endings differ between the original and fresh checkout.
Each report binds its own bytes; neither substitutes for the other.
Subsequent integration documentation does not change build inputs.

Complete candidate: `dist/test-v0.1.5/HytaleAccounts-Setup.zip`.
FileVersion `0.1.5.0`; ProductVersion
`0.1.5+8676b4f04726b54a6728a3bdedee851e04e59f24`.

- EXE SHA-256: `7B6A9010A2122536B5D6B5BAB379D5EEB1E1902A2A70DFFBFB5224E0F931F6CB`.
- ZIP SHA-256: `E4E48E7A32526CF5C34355BEB02D542A6EABEC90BC1BD04A04753100978CC77C`.

The actual-byte inspector verified all eight embedded resources, matching project/
Apps/assembly versions, zero native bundle entries, five native DLL sidecars plus
two license files, and exactly eight distinct ZIP entries with matching bytes.
All nine SHA256SUMS records were independently checked again after copying the
candidate and complete build evidence back to the original checkout.
`RUNTIME-PACKAGE.json` retains the resource and sidecar hashes.
`SECURITY-STATUS.json` records SecurityValidated false and actual VM unverified.
The historical report path points at the release build worktree; its exact bytes
and report are retained with the candidate before that worktree is removed.

GitHub verification must pass on the final documentation/source commit before
source verification is declared complete. Cleanup uses normal worktree removal
and merged-branch deletion only after all source and private evidence is retained.
