# Host preparation evidence — 2026-10-13

**No privileged VM scenario has run.** These results cover preparation, compilation and refusal safeguards only.

Environment discovery (read-only):

- Machine manufacturer/model: GIGABYTE / G6 KF, a physical laptop.
- `HypervisorPresent = True` does not establish that this OS is a disposable guest or that an accessible VM exists.
- No `Get-VM` command or Hyper-V PowerShell module; querying `root/virtualization/v2` returned `Invalid namespace`.
- No discovered `VBoxManage`, `vmrun`, VMware, QEMU, `virsh` or `multipass` command; the standard VMware/VirtualBox executable locations and `WindowsSandbox.exe` were absent.
- No running `vmwp`, `vmware-vmx`, `VirtualBoxVM` or `qemu-system-x86_64` process was found.
- No usable/authorized disposable VM, checkpoint or guest console access was identified. This is discovery of accessible tools, not proof that no VM exists elsewhere.

Validation command:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Checks/VmValidation/Validate.ps1
git diff --check
```

Results:

- Windows PowerShell 5.1.26100.9549 parsed all five `.ps1` files successfully.
- .NET SDK 10.0.203 built the harness and referenced production launcher assembly: **0 warnings, 0 errors**. NuGet sources were cleared; no dependency downloads were needed.
- Pure predicate tests accepted a **simulated** designated VM and rejected physical hardware, domain membership, connected networking, mismatched/empty/build-host UUIDs, non-fixture/network roots, expired designation, wrong run/computer identities and missing consent.
- Package SHA-256 manifest matched the staged files; the package did not contain a real `HytaleAccounts.exe` launcher.
- The compiled validation executable was invoked only with `identity C:\Windows`. It returned nonzero with the specific guest-guard refusal, before identity/test work. This was not a UAC attempt or a privileged scenario.
- The temporary build/guard-output fixture was removed. `git diff --check` passed (existing line-ending conversion notices only).

Not established: actual UAC consent/credential prompts, different-user kernel file access, elevated/SYSTEM staging, real scheduled-task execution, guest cleanup under attack, or OTS source compatibility. There are no actual VM result JSON files or task XML artifacts to cite. The runbook states expected results and requires complete evidence before `Verify` can report success.

The host was not elevated; no host accounts, registry entries or tasks were created, and no real credentials, Hytale data or installed addon were accessed. Scripts and runtime fixtures still require their first actual guest execution; host compilation is not a substitute.
