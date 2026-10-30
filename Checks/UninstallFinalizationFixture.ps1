param([string]$Root, [string]$Repository, [string]$Mode, [int]$WaitForPid, [string]$RemovalAttempt)
$ErrorActionPreference = 'Stop'
$full = [IO.Path]::GetFullPath($Root)
if ((Split-Path $full) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or (Split-Path $full -Leaf) -cnotmatch '^uninstall-finalization-[a-f0-9]{32}$') { throw 'Not a disposable uninstall fixture.' }
$env:ProgramFiles = Join-Path $Root 'program-files'
$env:LOCALAPPDATA = Join-Path $Root 'local'
$official = Join-Path $env:ProgramFiles 'Hypixel Studios\Hytale Launcher\hytale-launcher.exe'
$destination = Join-Path (Split-Path $official) '.hytale-accounts-addon'
$package = Join-Path $Root 'package'
$programs = Join-Path $Root 'programs'
$common = Join-Path $Root 'common'
$registry = 'HKCU:\Software\HytaleAccounts-Finalization-' + (Split-Path $Root -Leaf)
$options = @{ ProgramsDirectory=$programs; DesktopDirectory=(Join-Path $Root 'desktop'); CommonProgramsDirectory=$common;
    CommonDesktopDirectory=(Join-Path $Root 'common-desktop'); RegistryPath=$registry; UserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
# Task persistence crosses fixture processes; no real task or process discovery is permitted.
Import-Module ScheduledTasks
$taskFile = Join-Path $Root 'tasks.xml'
$global:HytaleFixtureTasks = @{}
if (Test-Path -LiteralPath $taskFile) { $global:HytaleFixtureTasks = Import-Clixml -LiteralPath $taskFile }
function Register-ScheduledTask { param($TaskName,$Action,$Trigger,$Settings,$User,$RunLevel,$Principal,[switch]$Force)
    $global:HytaleFixtureTasks[$TaskName] = [pscustomobject]@{ TaskName=$TaskName; Actions=@($Action | Select-Object Execute,Arguments); State='Ready' }
}
function Get-ScheduledTask { param($TaskName,$ErrorAction) @($global:HytaleFixtureTasks.Values | Where-Object TaskName -like $TaskName) }
function Disable-ScheduledTask { param($TaskName) }
function Stop-ScheduledTask { param($TaskName) }
function Unregister-ScheduledTask { param($TaskName,$Confirm) $global:HytaleFixtureTasks.Remove($TaskName) }
function Get-CimInstance { param($ClassName,$Filter) }
$result = 'success'
try {
    switch ($Mode) {
        Initialize {
            foreach ($path in @($package,$programs,$common,(Split-Path $official))) { [IO.Directory]::CreateDirectory($path) | Out-Null }
            [IO.File]::WriteAllText($official, 'non-executable official fixture')
            [IO.File]::WriteAllText((Join-Path $package 'HytaleAccounts.exe'), 'non-executable addon fixture')
            foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs','Repair-Integration.ps1')) {
                Copy-Item -LiteralPath (Join-Path $Repository $name) -Destination $package
            }
            $shell = New-Object -ComObject WScript.Shell
            foreach ($path in @((Join-Path $programs 'Hytale Launcher.lnk'),(Join-Path $common 'Hytale Launcher.lnk'))) {
                $link = $shell.CreateShortcut($path); $link.TargetPath=$official; $link.Description='Original fixture'; $link.Save()
                Copy-Item -LiteralPath $path -Destination ($path + '.original')
            }
            & (Join-Path $package 'Install.ps1') @options
            & (Join-Path $package 'Install.ps1') @options -UserOnly
            [IO.File]::WriteAllText((Join-Path $destination 'foreign.txt'), 'unrelated addon-directory file')
        }
        Prepare { & (Join-Path $destination 'Uninstall.ps1') @options -UserOnly -KeepJournal }
        Finalize { & (Join-Path $destination 'Uninstall.ps1') @options -UserOnly }
        Recover { & (Join-Path $destination 'Install.ps1') @options -UserOnly }
        Repair {
            & (Join-Path $destination 'Repair-Integration.ps1') -UserOnly
            if ((New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs 'Hytale Launcher.lnk')).TargetPath -ne (Join-Path $destination 'HytaleAccounts.exe')) { throw 'Approval-window repair did not retarget the shortcut.' }
        }
        Worker { & (Join-Path $destination 'Uninstall.ps1') @options -WaitForPid $WaitForPid -RemovalAttempt $RemovalAttempt | Out-Null }
        default { throw 'Unknown fixture mode.' }
    }
} catch {
    $result = 'failure|' + $_.Exception.GetBaseException().NativeErrorCode + '|' + $_.Exception.Message
    if ($Mode -ne 'Worker') { throw }
} finally {
    # Only machine/initialization phases change task state; user phases can run concurrently with a waiting worker.
    if ($Mode -in @('Initialize','Worker')) { $global:HytaleFixtureTasks | Export-Clixml -LiteralPath $taskFile }
    [IO.File]::WriteAllText((Join-Path $Root ($Mode + '.result')), $result)
}
