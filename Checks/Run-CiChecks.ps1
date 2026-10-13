param([switch]$Worker, [string]$ToolPath)
$ErrorActionPreference = 'Stop'
if (!$Worker -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) { throw 'This script is only for disposable GitHub-hosted Windows runners.' }
$repository = Split-Path $PSScriptRoot
$administrator = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Worker) {
    if ($administrator) { throw 'CI worker must be a standard user.' }
    # Windows creates this user's environment; pass only the tool directories selected by setup actions.
    if (!$ToolPath) { throw 'CI worker needs the selected SDK, Node and Git directories.' }
    $env:PATH = $ToolPath + ';' + $env:PATH
    if (!$env:USERPROFILE -or !$env:APPDATA -or !$env:LOCALAPPDATA) { throw 'CI user profile was not loaded.' }
    $env:DOTNET_CLI_HOME = $env:USERPROFILE
    $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
    $env:GIT_CONFIG_GLOBAL = Join-Path $env:USERPROFILE '.gitconfig'
    $null = New-Item -ItemType Directory -Path $env:TEMP -Force
    git config --global --add safe.directory ($repository.Replace('\','/'))
    if ($LASTEXITCODE) { throw 'Cannot trust the CI checkout for the test user.' }
    Write-Output 'CI tests run with a standard-user token and a separate disposable profile.'
    & (Join-Path $repository 'Build-Installer.ps1') -RegressionControls
    exit 0
}
if (!$administrator) { throw 'Runner bootstrap requires its normal administrator token.' }
$name = 'HyaCi' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$password = ConvertTo-SecureString ('aA1!' + [Guid]::NewGuid().ToString('N')) -AsPlainText -Force
$account = $null
$process = $null
$logs = Join-Path $repository 'dist/ci'
$null = New-Item -ItemType Directory -Path $logs -Force
try {
    $account = New-LocalUser -Name $name -Password $password -Description 'Disposable launcher CI checks'
    Add-LocalGroupMember -SID 'S-1-5-32-545' -Member $account
    & icacls.exe $repository /grant ('*' + $account.SID.Value + ':(OI)(CI)M') /T /Q
    if ($LASTEXITCODE) { throw 'Cannot grant the CI user access to the checkout.' }
    $credential = [Management.Automation.PSCredential]::new("$env:COMPUTERNAME\$name", $password)
    $tools = (@('dotnet','node','git') | ForEach-Object { Split-Path (Get-Command $_ -ErrorAction Stop).Source } | Select-Object -Unique) -join ';'
    $process = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"' + $PSCommandPath + '"'),'-Worker','-ToolPath',('"' + $tools + '"')) `
        -Credential $credential -LoadUserProfile -WindowStyle Hidden -WorkingDirectory $repository -PassThru `
        -RedirectStandardOutput (Join-Path $logs 'stdout.txt') -RedirectStandardError (Join-Path $logs 'stderr.txt')
    $null = $process.Handle # Retain the native handle so PowerShell can read ExitCode after the process exits.
    if (!$process.WaitForExit(900000)) { $process.Kill(); throw 'Standard-user CI checks timed out.' }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Standard-user CI checks exited $($process.ExitCode)." }
} finally {
    foreach ($file in @('stdout.txt','stderr.txt')) {
        $path = Join-Path $logs $file
        if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path }
    }
    if ($process) { $process.Dispose() }
    if ($account) { Remove-LocalUser -SID $account.SID }
}
