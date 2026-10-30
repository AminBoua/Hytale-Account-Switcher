param([string]$ChecksAssembly = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Checks.dll'))
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$driver = Join-Path $PSScriptRoot 'UninstallFinalizationFixture.ps1'
function Check($Condition, [string]$Message) { if (!$Condition) { throw "FAIL $Message" }; Write-Host "PASS $Message" }
function Invoke-Fixture([string]$Mode) {
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $driver -Root $root -Repository $repository -Mode $Mode
    if ($LASTEXITCODE) { throw "Fixture $Mode failed." }
}
function Wait-Worker {
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath (Join-Path $root 'Worker.result')) -and $deadline.Elapsed.TotalSeconds -lt 40) { Start-Sleep -Milliseconds 50 }
    Check (Test-Path -LiteralPath (Join-Path $root 'Worker.result')) 'hidden removal worker terminates within its bound'
}
function Snapshot-Recovery {
    $files = @{}
    foreach ($folder in @($destination,$userState)) {
        foreach ($file in Get-ChildItem -LiteralPath $folder -File) { $files[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
    }
    @{ Files=$files; Entry=(Get-ItemProperty -LiteralPath $registry | ConvertTo-Json -Compress);
        UserRecords=((Get-Content -LiteralPath (Join-Path $userState 'shortcuts.json') -Raw | ConvertFrom-Json).Shortcuts | Select-Object Path,Backup,Arguments,FileId | ConvertTo-Json -Compress);
        Tasks=(@((Import-Clixml -LiteralPath (Join-Path $root 'tasks.xml')).Values | Sort-Object TaskName) | ConvertTo-Json -Depth 6 -Compress) }
}
function Check-Recovery($Before) {
    foreach ($path in $Before.Files.Keys) {
        # Repair legitimately refreshes AppliedHash and JSON ordering; retain its usable original records and backup bytes.
        if ($path -eq (Join-Path $userState 'shortcuts.json')) { continue }
        Check ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path).Hash -eq $Before.Files[$path]) "finalization refusal preserves installed files, journals and backups: $path"
    }
    Check (((Get-Content -LiteralPath (Join-Path $userState 'shortcuts.json') -Raw | ConvertFrom-Json).Shortcuts | Select-Object Path,Backup,Arguments,FileId | ConvertTo-Json -Compress) -ceq $Before.UserRecords) 'finalization refusal retains usable user restoration records'
    Check ((Get-ItemProperty -LiteralPath $registry | ConvertTo-Json -Compress) -ceq $Before.Entry) 'finalization refusal retains the uninstall entry'
    Check ((@((Import-Clixml -LiteralPath (Join-Path $root 'tasks.xml')).Values | Sort-Object TaskName) | ConvertTo-Json -Depth 6 -Compress) -ceq $Before.Tasks) 'finalization refusal retains both repair tasks'
}
foreach ($scenario in @('Locked','Cancelled','StartupFailure','NoConfirmation')) {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('uninstall-finalization-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $registry = 'HKCU:\Software\HytaleAccounts-Finalization-' + (Split-Path $root -Leaf)
    $destination = Join-Path $root 'program-files\Hypixel Studios\Hytale Launcher\.hytale-accounts-addon'
    $userState = Join-Path $root 'local\HytaleAccounts\ShortcutIntegration'
    try {
        Invoke-Fixture Initialize
        $before = Snapshot-Recovery
        & dotnet $ChecksAssembly --uninstall-finalization-child $root $repository $scenario
        Check ($LASTEXITCODE -eq 0) "$scenario exercises production Setup.Remove and production shortcut scripts"
        if ($scenario -in @('Locked','NoConfirmation')) {
            Wait-Worker
            $outcome = Get-Content -LiteralPath (Join-Path $root 'Worker.result') -Raw
            $helperExists = Test-Path -LiteralPath (Join-Path $destination 'HytaleAccounts.exe')
            Write-Host "Worker outcome: $outcome; installed helper exists: $helperExists"
            Check ($outcome -like 'failure|*' -and $helperExists) 'machine removal is prevented after failed or missing finalization'
        }
        Check-Recovery $before
        Invoke-Fixture Worker
        Check ((Get-Content -LiteralPath (Join-Path $root 'Worker.result') -Raw) -like '*requires per-user finalization confirmation*') 'machine script refuses a missing handoff before changing installed state'
        Check-Recovery $before
        # Exercise the production wait's refusal paths without waiting 30 seconds per timeout fixture.
        . (Join-Path $repository 'InstallerSafety.ps1')
        $attempt = [Guid]::NewGuid().ToString('N')
        $name = 'Local\HytaleAccounts-Uninstall-' + $attempt
        $confirmed = [Threading.EventWaitHandle]::new($false, 'ManualReset', $name + '-confirmed')
        $received = [Threading.EventWaitHandle]::new($false, 'ManualReset', $name + '-received')
        try {
            foreach ($probe in @(@{Pid=$PID;Attempt=$attempt;Error='timeout'},@{Pid=[int]::MaxValue;Attempt=$attempt;Error='missing parent'},
                @{Pid=$PID;Attempt=([Guid]::NewGuid().ToString('N'));Error='different attempt'},@{Pid=0;Attempt='';Error='missing confirmation'})) {
                $failure = $null
                try { Wait-RemovalFinalization $probe.Pid $probe.Attempt 50 } catch { $failure = $_ }
                Check ($null -ne $failure -and !$received.WaitOne(0)) "$($probe.Error) does not acknowledge success"
                Check-Recovery $before
            }
            $null = $confirmed.Set()
            $failure = $null
            try { Wait-RemovalFinalization $PID $attempt 50 } catch { $failure = $_ }
            Check ($failure -and $failure.Exception.Message -like '*parent did not exit*') 'confirmed finalization still requires parent exit before machine mutation'
            $null = $received.Reset()
            $command = "[Threading.EventWaitHandle]::OpenExisting('$name-received').WaitOne(30000) | Out-Null; exit 23"
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
            $failedParent = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',$encoded) -WindowStyle Hidden -PassThru
            try {
                $failure = $null
                try { Wait-RemovalFinalization $failedParent.Id $attempt 5000 } catch { $failure = $_ }
                Check ($failure -and $failure.Exception.Message -like '*parent failed*') 'a nonzero parent exit cannot authorize machine removal'
            } finally {
                $null = $received.Set()
                if (!$failedParent.WaitForExit(5000)) { $failedParent.Kill(); $failedParent.WaitForExit() }
                $failedParent.Dispose()
            }
            Check-Recovery $before
        } finally { $confirmed.Dispose(); $received.Dispose() }
        $workerResult = Join-Path $root 'Worker.result'
        if (Test-Path -LiteralPath $workerResult) { [IO.File]::Delete($workerResult) }
        & dotnet $ChecksAssembly --uninstall-finalization-child $root $repository Success
        Check ($LASTEXITCODE -eq 0) 'releasing the sharing lock permits a successful production retry'
        Wait-Worker
        $workerOutcome = Get-Content -LiteralPath $workerResult -Raw
        Check ($workerOutcome -eq 'success') "confirmed retry completes machine removal: $workerOutcome"
        Check (!(Test-Path -LiteralPath (Join-Path $destination 'HytaleAccounts.exe')) -and !(Test-Path -LiteralPath $registry)) 'successful uninstall removes addon payload and uninstall entry'
        Check (!(Import-Clixml -LiteralPath (Join-Path $root 'tasks.xml')).Count) 'successful uninstall removes only mocked addon tasks'
        Check (!(Test-Path -LiteralPath (Join-Path $userState 'shortcuts.json')) -and !@(Get-ChildItem -LiteralPath $userState -Filter 'shortcut-*.lnk').Count) 'successful finalization removes per-user recovery data'
        Check ((Get-ChildItem -LiteralPath $destination -File).Count -eq 1 -and (Get-Content -LiteralPath (Join-Path $destination 'foreign.txt') -Raw) -eq 'unrelated addon-directory file') 'successful uninstall preserves unrelated files'
        foreach ($folder in @('programs','common')) {
            $path = Join-Path $root ($folder + '\Hytale Launcher.lnk')
            Check ((Get-FileHash -LiteralPath $path).Hash -eq (Get-FileHash -LiteralPath ($path + '.original')).Hash) 'successful uninstall restores exact user and common shortcut bytes'
        }
        Check ((Get-Content -LiteralPath (Join-Path $root 'program-files\Hypixel Studios\Hytale Launcher\hytale-launcher.exe') -Raw) -eq 'non-executable official fixture') 'official launcher fixture remains unchanged'
    } finally {
        if (Test-Path -LiteralPath $registry) { Remove-Item -LiteralPath $registry }
        $full = [IO.Path]::GetFullPath($root)
        if ((Split-Path $full) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or (Split-Path $full -Leaf) -cnotmatch '^uninstall-finalization-[a-f0-9]{32}$') { throw 'Cleanup escaped fixture.' }
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    }
}
