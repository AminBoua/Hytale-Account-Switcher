# Storage permission rejection

## Outcome

The reported `%APPDATA%\Hytale` directory is accurately rejected as unsafe. The
validation policy is unchanged. No live permissions, files, ownership, storage,
authentication material or launcher processes were changed.

Branch: `fix/storage-permission-rejection`.
Worktree: sibling `Mod-hytale-launcher-storage-permission-rejection`.
Starting local `main`: `4578eb31f6ab5a73a803a0bf97f010339de27111`.
The original checkout was left unchanged. This report and its changes are in the
branch's final commit; no merge, push, publication or worktree removal occurred.

## Exact decision and privacy-safe evidence

Read-only inspection was limited to the live directory's attributes and security
descriptor. No enumeration of its children or account/session/key reads occurred.
Private account identifiers and raw live descriptors are omitted here and from
fixtures. Only built-in principals and generalized profile roles are reported.

- Owner: built-in Administrators (`S-1-5-32-544`), which passes ownership validation.
  Primary group: current profile user; group membership does not confer trust.
- DACL: present, auto-inherited, unprotected. Directory is not a reparse point.
- Rejecting zero-based ACE **0**: AccessAllowed, explicit, `ObjectInherit |
  ContainerInherit`; neither `Inherited` nor `InheritOnly`.
- Trustee: built-in Users (`S-1-5-32-545`, SDDL `BU`). Access mask:
  **0x001F01FF**, Full Control. It applies to this directory and is inheritable by
  files and directories. No deny ACEs precede or follow it.
- Remaining entries are inherited trusted-principal full-control grants and a
  read-only profile-principal grant. They do not cancel the Users grant.
- `StorageSafety.Require` maps generic rights, subtracts preceding same-trustee
  and Everyone denies, and intersects untrusted allows with its mutation mask.
  Here effective mutation is **0x000D0156**: create/write, append/create-directory,
  write extended attributes, write attributes, delete child, delete, WRITE_DAC
  and WRITE_OWNER. Ancestor sibling-creation exemption removes only `0x6`, leaving
  **0x000D0150**, so the ancestor case also correctly rejects this grant.

The diagnostic now appends the ACE index, inheritance flags, original mask and
effective mutation mask. It adds no principal names/SIDs or file contents to the
existing path-bearing error. Ownership, link/hard-link, unsupported-rule,
null-DACL, mutation and fail-closed decisions remain intact.

## Initialization and provenance

`NativeStores` resolves the official root and calls
`PrivateFiles.SecureTree(HytaleRoot/Accounts)`. `CreateDirectory` pins existing
directories or the parent before creating anything; `PinnedDirectories` checks
each ancestor through `StorageSafety.Require`. The root's Users grant therefore
blocks initialization before migration can repair addon ACLs. Existing Accounts
also cannot bypass the ancestor check. Capture, key-copy and atomic-write paths
use the same shared validator and pinned paths.

`SecureTree` scopes ACL migration to Accounts, inspects and pins the tree before
applying private ACLs, and uses non-propagating handle-based directory updates.
Private creation grants only the current user, SYSTEM and Administrators. Native
replacement preserves the native destination DACL. These paths do not create a
Users full-control ACE on the Hytale root. Inspection of setup/installer ACL
call sites found no such root grant; installer sharing/replacement was not changed.

The rejection text/policy was introduced by `6fefa45` (v0.1.2 security hardening),
before v0.1.4. The observed root ACE is explicit, not merely inherited bookkeeping.
There is no evidence here of an installation/migration/propagation regression
creating it. Its author and creation time cannot be established from a current
ACL; historical installer execution was not reproduced. This uncertainty does
not affect the unsafe-access finding.

## Disposable reproduction and checks

`StorageSafetyChecks` builds the observed descriptor shape in memory using only
synthetic profile identities. It retains Administrators ownership, a separate
primary group, inherited trusted/read-only entries, and the explicit Users ACE.
It tests both Users read/execute (legitimate) and Full Control (unsafe), each with
explicit/inherited ACEs and protected/unprotected auto-inheritance flags. Windows
Authz independently verifies each of the eight unrelated-user mutation rights.
A preceding Users deny eliminates mutation; inherit-only grants do not apply to
the parent; changing the primary group to Users does not make its allow trusted.
Existing controls cover deny ordering, foreign owners, null DACLs and generic
rights.

A disposable on-disk directory reproduces initialization rejection without ACL
repair or addon creation, then verifies that the legitimate read-only Users
variant initializes while preserving its parent DACL. Only the DACL is applied
to this fixture; its owner stays the fixture's actual user. Live data is unused.

Passed on Windows with .NET 10:

- `dotnet run --project Checks/Checks.csproj -c Release -- --storage-safety`
  (build and focused storage/ACL checks).
- Same project, Release `--no-build`: `--acl`, `--acl-access`,
  `--acl-propagation`, `--account-storage`.
- `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File
  Checks/AclMigrationRaceChecks.ps1`, and its `-Storage` variant.
- Mutation controls: `-StorageSafety -Mutation Policy`,
  `-StorageSafety -Mutation PrivateReplacement`, `-Mutation Membership`,
  `-Mutation Propagation`, `-PinsOnly -Mutation Pins`. All five failed at their
  expected assertions; none survived. Assertions were not disabled.
- `python -m graphify update .` and `git diff --check`.

Migration checks retain synthetic session/key bytes, native and unrelated ACLs,
rollback, link/hard-link rejection, conflicting-handle rejection, concurrent
insertion detection, and protection against parent-to-child ACL propagation.
Authz uses simulated ordinary-user group membership, not a second logged-in user.

## Remediation proposal — requires separate review and user action

No repair was executed. Keep the gate enabled. A reviewer should:

1. Close the launcher/game normally before any later repair. Save the root's ACL
   metadata privately for comparison and confirm the current user still has its
   separate full-control grant, alongside SYSTEM/Administrators. Stop if the
   descriptor differs from the finding or the broad grant is intentional policy.
2. Remove **only** the exact explicit Users Full Control ACE on the Hytale root
   (`(A;OICI;FA;;;BU)`). Use a reviewed, handle-based DACL-only update that does not
   propagate to children (as with `SetKernelObjectSecurity` using
   `DACL_SECURITY_INFORMATION`, preserving protection state). Do not invoke the
   addon's whole-tree private ACL migration as a root repair. Elevated permission
   may be necessary because the owner is Administrators; do not take ownership.
3. Preserve every other ACE, owner, primary group and inheritance/protection
   setting. Do not use recursive resets, broad grants, replacement of child
   permission entries, or data deletion. Re-read metadata and verify only that
   ACE changed, and that unrelated users lack each mutation right on the root.
4. A non-propagating root repair deliberately leaves existing child descriptors
   untouched. Review any subsequently rejected path separately, metadata-only;
   inherited copies or independent explicit grants may require their own
   narrowly approved repair. Do not assume the root edit repairs the entire tree.
5. Retry normal startup only after the reviewed repair and metadata verification.
   Never bypass the gate to make startup succeed.

The live unsafe grant remains a startup blocker. Package work, broad regression,
live startup/authentication, real second-user access and historical installer
reproduction were outside this task and were not checked.
