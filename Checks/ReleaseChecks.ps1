function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable exited with code $LASTEXITCODE." }
}

function Get-RebuildSource {
    $root = Split-Path $PSScriptRoot
    $files = @(Get-ChildItem -LiteralPath $root -File -Force | Where-Object Name -ne '.git')
    foreach ($subdirectory in @('Launcher', 'Checks', '.github', '.github/workflows')) {
        $files += @(Get-ChildItem -LiteralPath (Join-Path $root $subdirectory) -File -Force)
    }
    $records = foreach ($file in $files | Sort-Object FullName) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $relative + ' ' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($records -join [char]10))).Replace('-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

function Invoke-BootstrapCheck {
    $bootstrap = Join-Path (Split-Path $PSScriptRoot) 'Launcher/Elevation.ps1'
    $command = ". '" + $bootstrap.Replace("'", "''") + "'" + [Environment]::NewLine + @'
$acl = [Security.AccessControl.DirectorySecurity]::new()
$path = Join-Path ([IO.Path]::GetTempPath()) ('hytale-bootstrap-check-' + [Guid]::NewGuid().ToString('N'))
$directory = [IO.DirectoryInfo]::new($path)
if ($directory.Exists) { throw 'The bootstrap fixture directory already exists.' }
[void][IO.Directory]::CreateDirectory($path)
try {
    $directory | Add-Member -MemberType ScriptMethod -Name GetAccessControl -Value { $acl } -Force
    $acl.SetSecurityDescriptorSddlForm('O:BAG:SYD:P(A;OICIIO;GA;;;BU)(A;;0x1200a9;;;BU)(A;OICI;FA;;;BA)')
    Assert-MachineDirectory $directory $false
    $acl.SetSecurityDescriptorSddlForm('O:BAG:SYD:P(A;;0x1200ab;;;BU)(A;OICI;FA;;;BA)')
    try {
        Assert-MachineDirectory $directory $false
        throw 'The bootstrap accepted an active write rule for ordinary users.'
    } catch {
        if ($_.Exception.Message -ne 'A lower-privilege account can modify the setup directory.') { throw }
    }
    Write-Output 'PASS Windows PowerShell bootstrap directory permissions'
} finally {
    if ([IO.Directory]::Exists($path)) { [IO.Directory]::Delete($path) }
}
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Invoke-Checked "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
}

function Invoke-SafeChecks {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ([Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run the safe checks from a normal Windows user session.'
    }
    Invoke-Checked dotnet @('run', '--project', 'Checks/Checks.csproj', '-c', 'Release', '--', '--safe')
    Invoke-Checked node @('Checks/NativeLoginChecks.cjs')
    Invoke-BootstrapCheck
}

function Invoke-CiChecks([switch]$Worker, [string]$ToolPath) {
    if (!$Worker -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) {
        throw 'The CI user setup requires a GitHub-hosted Windows runner.'
    }
    $root = Split-Path $PSScriptRoot
    if ($Worker) {
        if (!$ToolPath) { throw 'Missing CI tool paths.' }
        $env:PATH = $ToolPath + ';' + $env:PATH
        if (!$env:USERPROFILE -or !$env:LOCALAPPDATA) { throw 'The CI user profile was not loaded.' }
        $env:DOTNET_CLI_HOME = $env:USERPROFILE
        $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget/packages'
        $env:GIT_CONFIG_GLOBAL = Join-Path $env:USERPROFILE '.gitconfig'
        Invoke-Checked git @('config', '--global', '--add', 'safe.directory', $root.Replace('\','/'))
        & (Join-Path $root 'Build-Installer.ps1')
        return
    }

    $name = 'HyaCi' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $password = ConvertTo-SecureString ('Aa1!' + [Guid]::NewGuid().ToString('N')) -AsPlainText -Force
    $account = $null
    $workerProcess = $null
    $logs = Join-Path $root 'dist/ci'
    [void](New-Item -ItemType Directory -Force -Path $logs)
    try {
        $account = New-LocalUser -Name $name -Password $password -Description 'Disposable Hytale CI user'
        Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $account
        Invoke-Checked icacls @($root, '/grant', ('*' + $account.SID.Value + ':(OI)(CI)M'), '/T', '/Q')
        $paths = (@('dotnet', 'node', 'git') | ForEach-Object { Split-Path (Get-Command $_).Source } | Select-Object -Unique) -join ';'
        $script = Join-Path $PSScriptRoot 'ReleaseChecks.ps1'
        $command = ". '" + $script.Replace("'", "''") + "'; Invoke-CiChecks -Worker -ToolPath '" + $paths.Replace("'", "''") + "'"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('$ErrorActionPreference=''Stop''; ' + $command))
        $credentials = [Management.Automation.PSCredential]::new("$env:COMPUTERNAME\$name", $password)
        $start = @{
            FilePath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
            ArgumentList = @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
            Credential = $credentials; LoadUserProfile = $true; WindowStyle = 'Hidden'; WorkingDirectory = $root; PassThru = $true
            RedirectStandardOutput = (Join-Path $logs 'stdout.txt'); RedirectStandardError = (Join-Path $logs 'stderr.txt')
        }
        $workerProcess = Start-Process @start
        $null = $workerProcess.Handle
        if (!$workerProcess.WaitForExit(900000)) { $workerProcess.Kill(); $workerProcess.WaitForExit(); throw 'CI checks timed out.' }
        $workerProcess.WaitForExit()
        if ($workerProcess.ExitCode -ne 0) { throw "CI checks exited with code $($workerProcess.ExitCode)." }
    } finally {
        foreach ($name in @('stdout.txt', 'stderr.txt')) {
            $path = Join-Path $logs $name
            if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path }
        }
        if ($workerProcess) { $workerProcess.Dispose() }
        if ($account) { Remove-LocalUser -SID $account.SID }
    }
}
