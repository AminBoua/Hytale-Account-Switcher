param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][ValidateSet('ordinary','same-user','alternate-admin')][string]$Mode,
    [Parameter(Mandatory)][ValidateSet('Begin','Worker','Attack','OwnerAttack')][string]$Action
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Guard.ps1')
$run = Get-Guard $Root
if ([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') -ne (Join-Path $Root 'bin')) { throw 'Use the immutable designated package.' }
function Check($Value, [string]$Message) { if (!$Value) { throw $Message } }
function Now { [DateTime]::UtcNow.ToString('o') }
function Write-New($Path, $Value) {
    Check (!(Test-Path -LiteralPath $Path)) 'Runtime phases are single-use.'
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Path -Encoding UTF8
}
function Wait-File($Path) {
    $deadline = [DateTime]::UtcNow.AddMinutes(4)
    while (!(Test-Path -LiteralPath $Path) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    Check (Test-Path -LiteralPath $Path) "Runtime peer timeout: $Path"
}
$identity = (& (Join-Path $PSScriptRoot 'Checks.exe') identity $Root | ConvertFrom-Json)
Check ($LASTEXITCODE -eq 0) 'Actual token capture failed.'
$identity.Pid = $PID
$elevated = $Mode -ne 'ordinary'
$initiator = if ($Mode -eq 'same-user') { $run.AdminSid } else { $run.UserSid }
$worker = if ($elevated) { $run.AdminSid } else { $run.UserSid }
$channel = Join-Path $Root "channels\$worker"
$ready = Join-Path $channel "runtime-ready-$Mode.json"
$stop = Join-Path $channel "runtime-stop-$Mode"
$outcome = Join-Path $channel "runtime-worker-$Mode.json"
$otherAttack = Join-Path $Root "channels\$($run.AttackerSid)\runtime-attack-$Mode.json"
$ownerAttack = Join-Path $Root "channels\$($run.AdminSid)\runtime-owner-attack-$Mode.json"
if ($Action -in @('Attack','OwnerAttack')) {
    $owner = $Action -eq 'OwnerAttack'
    Check (!$owner -or $elevated) 'No owner attack against ordinary same-user data.'
    Check ($identity.Sid -eq $(if ($owner) {$run.AdminSid} else {$run.AttackerSid}) -and !$identity.Administrator -and $identity.ElevationType -eq $(if ($owner) {3} else {1})) 'Wrong independent runtime actor token.'
    Wait-File $ready
    $observation = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
    $allowed = if ($elevated) { Join-Path $Root "runtime-cache-$Mode" } else { Join-Path $observation.Profile.TEMP '.net\RuntimeProbe' }
    if ($elevated) { Check ($observation.Cache.Path -eq $allowed) 'Runtime target escaped designated cache.' }
    else {
        Check ($observation.Profile.USERPROFILE -match '^C:\\Users\\MLSV-User-[a-f0-9]{6}$' -and
            $allowed.StartsWith($observation.Profile.USERPROFILE+'\',[StringComparison]::OrdinalIgnoreCase) -and
            $observation.Cache.Path.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase)) 'Ordinary probe target escaped disposable profile.'
    }
    Check ([IO.Path]::GetFullPath($observation.Cache.Path) -eq $observation.Cache.Path) 'Non-canonical runtime cache target.'
    Check (@($observation.Files).Count -gt 0 -and $observation.Files[0].Path.StartsWith($observation.Cache.Path+'\',[StringComparison]::OrdinalIgnoreCase)) 'Runtime leaf escaped cache.'
    Check ([IO.Path]::GetFullPath($observation.Files[0].Path) -eq $observation.Files[0].Path) 'Non-canonical runtime file target.'
    $hash = (Get-FileHash -LiteralPath $ready).Hash
    $control = Join-Path $Root "channels\$($identity.Sid)\runtime-control-$Mode"
    Check (!(Test-Path -LiteralPath $control)) 'Existing runtime positive control.'
    New-Item -ItemType Directory -Path $control | Out-Null
    $leaf = Join-Path $control 'probe.dll'
    [IO.File]::WriteAllText($leaf,'FAKE DLL')
    # Exercise the same mutation operations successfully before testing protected targets.
    function Mutate($Directory, $File, $Denied) {
        $rows = @()
        foreach ($operation in @('write','replace','file-write-dac','directory-write-dac','insert-file','delete-leaf','rename-directory','delete-directory')) {
            $errorCode = 0
            try {
                switch ($operation) {
                    'write' { [IO.File]::WriteAllText($File,'FAKE MUTATION') }
                    'replace' {
                        $replacement = Join-Path $control 'replacement'
                        [IO.File]::WriteAllText($replacement,'FAKE REPLACEMENT')
                        try { [IO.File]::Replace($replacement,$File,$null) } finally { [IO.File]::Delete($replacement) }
                    }
                    'file-write-dac' {
                        $acl = [Security.AccessControl.FileSecurity]::new()
                        $acl.SetAccessRuleProtection($true,$false)
                        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity.Sid,'FullControl','Allow'))
                        [IO.FileInfo]::new($File).SetAccessControl($acl)
                    }
                    'directory-write-dac' {
                        $acl = [Security.AccessControl.DirectorySecurity]::new()
                        $acl.SetAccessRuleProtection($true,$false)
                        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity.Sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
                        [IO.DirectoryInfo]::new($Directory).SetAccessControl($acl)
                    }
                    'insert-file' { [IO.File]::WriteAllText((Join-Path $Directory 'inserted.txt'),'FAKE INSERT') }
                    'delete-leaf' { [IO.File]::Delete($File) }
                    'rename-directory' { [IO.Directory]::Move($Directory,$Directory+'-moved'); [IO.Directory]::Move($Directory+'-moved',$Directory) }
                    'delete-directory' { [IO.Directory]::Delete($Directory,$true) }
                }
            } catch {
                $errorCode = $_.Exception.HResult -band 0xffff
                if ($_.Exception.InnerException) { $errorCode = $_.Exception.InnerException.HResult -band 0xffff }
                Check ($Denied -and $errorCode -in @(5,32,33)) "Unexpected runtime operation error: $operation ($errorCode)"
            }
            Check (!$Denied -or $errorCode -in @(5,32,33)) "Runtime mutation succeeded: $operation"
            $rows += @{Operation=$operation;Denied=$Denied;WindowsError=$errorCode}
        }
        $rows
    }
    $null = Mutate $control $leaf $false
    # Recreate the private replacement scratch directory removed by the positive control.
    New-Item -ItemType Directory -Path $control | Out-Null
    try {
        $rows = @(Mutate $observation.Cache.Path $observation.Files[0].Path $true)
        Write-New $(if ($owner) {$ownerAttack} else {$otherAttack}) @{Success=$true;RunId=$run.Id;Mode=$Mode;Identity=$identity;Utc=(Now);WorkerPid=$observation.Identity.Pid;ReadyHash=$hash;PositiveControl=$true;Operations=$rows}
    } finally { if (Test-Path -LiteralPath $control) { [IO.Directory]::Delete($control,$true) } }
    exit 0
}
if ($Action -eq 'Begin') {
    Check ($identity.Sid -eq $initiator -and !$identity.Administrator -and $identity.ElevationType -eq $(if ($Mode -eq 'same-user') {3} else {1})) 'Wrong runtime initiator.'
    $started = Now
    $result = Join-Path $Root "channels\$initiator\runtime-$Mode.json"
    Check (!(Test-Path -LiteralPath $result) -and !(Test-Path -LiteralPath $ready)) 'Use a fresh runtime run.'
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Root `"$Root`" -Mode $Mode -Action Worker"
    $start = @{FilePath=(Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe');ArgumentList=$arguments;WindowStyle='Hidden';PassThru=$true}
    if ($elevated) { $start.Verb = 'RunAs' }
    $child = Start-Process @start
    Wait-File $ready
    Wait-File $otherAttack
    if ($elevated) { Wait-File $ownerAttack }
    [IO.File]::WriteAllText($stop,'STOP HARMLESS PROBE')
    Check ($child.WaitForExit(60000) -and $child.ExitCode -eq 0) 'Runtime worker did not complete.'
    $data = Get-Content -LiteralPath $outcome -Raw | ConvertFrom-Json
    $data | Add-Member NoteProperty Identity $identity
    $data | Add-Member NoteProperty StartedUtc $started
    $data | Add-Member NoteProperty CompletedUtc (Now)
    Write-New $result $data
    exit 0
}
Check ($identity.Sid -eq $worker -and $identity.Administrator -eq $elevated -and $identity.ElevationType -eq $(if ($elevated) {2} else {1})) 'Wrong runtime worker token.'
$probe = Join-Path $PSScriptRoot 'RuntimeProbe.exe'
$profile = @{}; foreach ($name in @('APPDATA','LOCALAPPDATA','USERPROFILE','TEMP','TMP')) { $profile[$name] = [Environment]::GetEnvironmentVariable($name) }
$arguments = @($ready,$run.Id,$Mode,$run.Source.Fingerprint,$stop)
$cache = Join-Path $Root "runtime-cache-$Mode"
$poison = Join-Path $channel "runtime-poison-$Mode"
Check (!(Test-Path -LiteralPath $poison)) 'Existing poison fixture.'
New-Item -ItemType Directory -Path $poison | Out-Null
[IO.File]::WriteAllText((Join-Path $poison 'sentinel'),'FAKE POISON')
if ($elevated) {
    . (Join-Path $PSScriptRoot 'ElevatedRuntime.ps1')
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $poison
    $exitCode = Invoke-ProtectedRuntime $cache $probe $arguments
    $removed = !(Test-Path -LiteralPath $cache)
    $startupFailed = $false
    try { $null = Invoke-ProtectedRuntime $cache (Join-Path $PSScriptRoot 'nonexistent-probe.exe') @() } catch { $startupFailed = $true }
    $failureRemoved = $startupFailed -and !(Test-Path -LiteralPath $cache)
} else {
    # This base's ordinary entry is a direct single-file apphost. Do not pretend it has a protected bootstrap.
    Check (!$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR) 'Ordinary baseline requires the normal default extraction environment.'
    $ordinaryRoot = Join-Path $env:TEMP '.net\RuntimeProbe'
    Check (!(Test-Path -LiteralPath $ordinaryRoot)) 'Refusing a warm/pre-existing ordinary probe cache.'
    $start = [Diagnostics.ProcessStartInfo]::new($probe)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $child = [Diagnostics.Process]::Start($start)
    try { Check ($child.WaitForExit(300000)) 'Ordinary runtime timeout.'; $exitCode=$child.ExitCode } finally { $child.Dispose() }
    # Delete only this previously absent, harmless probe's default cache, never the .net parent.
    Assert-PlainPath $ordinaryRoot
    [IO.Directory]::Delete($ordinaryRoot,$true)
    $removed = !(Test-Path -LiteralPath $ordinaryRoot)
    $failureRemoved = $false # No ordinary bootstrap exists at this base; integration must supply and exercise it.
}
$observation = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
$preserved = $true
foreach ($name in $profile.Keys) { if ($observation.Profile.$name -cne $profile[$name]) { $preserved=$false } }
Write-New $outcome @{Contract='runtime-bootstrap-v1';RunId=$run.Id;Mode=$Mode;SourceFingerprint=$run.Source.Fingerprint;Success=($exitCode -eq 23);WorkerPid=$observation.Identity.Pid;ReadyHash=(Get-FileHash -LiteralPath $ready).Hash;ExitCode=$exitCode;CacheRemoved=$removed;StartFailureCacheRemoved=$failureRemoved;PoisonPreserved=(@(Get-ChildItem -LiteralPath $poison).Count -eq 1 -and [IO.File]::ReadAllText((Join-Path $poison 'sentinel')) -ceq 'FAKE POISON');ProfilePreserved=$preserved}
