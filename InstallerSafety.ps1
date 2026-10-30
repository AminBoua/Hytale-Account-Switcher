$ErrorActionPreference = 'Stop'
function Wait-RemovalFinalization([int]$ParentPid, [string]$Attempt, [int]$TimeoutMilliseconds = 30000) {
    if ($ParentPid -le 0 -or $Attempt -cnotmatch '^[a-f0-9]{32}$') { throw 'Removal requires per-user finalization confirmation; files preserved.' }
    $parent = $null; $confirmed = $null; $received = $null; $exited = $null
    try {
        $parent = [Diagnostics.Process]::GetProcessById($ParentPid)
        $null = $parent.Handle # Pin this process identity across exit/PID reuse.
        $name = 'Local\HytaleAccounts-Uninstall-' + $Attempt
        $confirmed = [Threading.EventWaitHandle]::OpenExisting($name + '-confirmed', [Security.AccessControl.EventWaitHandleRights]::Synchronize)
        $received = [Threading.EventWaitHandle]::OpenExisting($name + '-received', [Security.AccessControl.EventWaitHandleRights]::Modify)
        $exited = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset)
        $exited.SafeWaitHandle = [Microsoft.Win32.SafeHandles.SafeWaitHandle]::new($parent.Handle, $false)
        if ([Threading.WaitHandle]::WaitAny([Threading.WaitHandle[]]@($exited, $confirmed), $TimeoutMilliseconds) -ne 1) {
            throw 'Per-user finalization was not confirmed before parent exit or timeout; files preserved.'
        }
        $null = $received.Set()
        if (!$parent.WaitForExit($TimeoutMilliseconds)) { throw 'Removal parent did not exit; files preserved.' }
        if ($parent.ExitCode -ne 0) { throw 'Removal parent failed; files preserved.' }
    } finally {
        foreach ($handle in @($exited, $received, $confirmed, $parent)) { if ($handle) { $handle.Dispose() } }
    }
}
function Stop-InstalledLauncher([string]$Installed, [switch]$IncludeOfficial) {
    $helpers = @(Get-CimInstance Win32_Process -Filter "Name='HytaleAccounts.exe'" | Where-Object { $_.ExecutablePath -eq $Installed })
    $official = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
    $natives = @(Get-CimInstance Win32_Process -Filter "Name='hytale-launcher.exe'" | Where-Object { $_.ParentProcessId -in @($helpers.ProcessId) -or ($IncludeOfficial -and $_.ExecutablePath -eq $official) })
    if (!$helpers.Count -and !$natives.Count) { return }
    if (Get-Process -Name HytaleClient -ErrorAction SilentlyContinue) { throw 'Close the game before installing or removing the addon.' }
    foreach ($native in $natives) {
            $process = Get-Process -Id $native.ProcessId -ErrorAction SilentlyContinue
            if ($process) { $null = $process.CloseMainWindow(); if (!$process.WaitForExit(15000)) { throw 'Close Hytale before continuing; no process was forcibly stopped.' } }
    }
    foreach ($helper in $helpers) {
        $process = Get-Process -Id $helper.ProcessId -ErrorAction SilentlyContinue
        if ($process) {
            # A failed startup can leave the invisible helper waiting on an error dialog.
            $null = $process.CloseMainWindow()
            if (!$process.WaitForExit(15000)) { throw 'Close Hytale before continuing; no process was forcibly stopped.' }
        }
    }
}
function Assert-RegularPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Path contains a link; files preserved: $current" }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Get-LauncherSnapshot([string]$Path) {
    Assert-RegularPath $Path
    $item = Get-Item -LiteralPath $Path -Force
    [pscustomobject]@{
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        Length = $item.Length
        Permissions = (Get-Acl -LiteralPath $Path).Sddl
    }
}
function Assert-LauncherSnapshot([string]$Path, $Before) {
    $after = Get-LauncherSnapshot $Path
    if ($Before.Sha256 -cne $after.Sha256 -or $Before.Length -ne $after.Length -or $Before.Permissions -cne $after.Permissions) {
        throw 'Official launcher bytes or permissions changed during the operation. Files preserved; inspect the official update before retrying.'
    }
}
