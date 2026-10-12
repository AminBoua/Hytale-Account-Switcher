$ErrorActionPreference = 'Stop'
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
