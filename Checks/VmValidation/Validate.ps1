$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot) 'SecurityEvidence.ps1')
# Host-safe validation only: parse, compile, verify package hashes and exercise refusal predicates.
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null; $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if ($errors) { throw ($errors | Out-String) }
    Write-Output "PASS PowerShell parser: $($file.Name)"
}
. (Join-Path $PSScriptRoot 'Guard.ps1')
function Reject([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (!$rejected) { throw "Guard accepted $Name" }
    Write-Output "PASS refusal: $Name"
}
$guest = [pscustomobject]@{ Manufacturer='Microsoft Corporation'; Model='Virtual Machine'; PartOfDomain=$false }
$uuid = '00000001-0002-0003-0004-000000000005'; $hostUuid = '00000009-0008-0007-0006-000000000005'
Assert-VmIdentity $guest $uuid $uuid $hostUuid $false
Write-Output 'PASS simulated designated VM predicate (not VM execution)'
Reject { Assert-VmIdentity ([pscustomobject]@{Manufacturer='GIGABYTE';Model='Physical';PartOfDomain=$false}) $uuid $uuid $hostUuid $false } 'physical machine'
Reject { Assert-VmIdentity ([pscustomobject]@{Manufacturer='Microsoft Corporation';Model='Virtual Machine';PartOfDomain=$true}) $uuid $uuid $hostUuid $false } 'domain membership'
Reject { Assert-VmIdentity $guest $uuid $uuid $hostUuid $true } 'connected guest network'
Reject { Assert-VmIdentity $guest $uuid $hostUuid $hostUuid $false } 'wrong designated UUID'
Reject { Assert-VmIdentity $guest $uuid $uuid $uuid $false } 'build-host UUID'
Reject { Assert-VmIdentity $guest ([Guid]::Empty.ToString()) ([Guid]::Empty.ToString()) $hostUuid $false } 'empty UUID'
Reject { Get-Guard 'C:\Windows' } 'non-fixture root'
Reject { Get-Guard '\\server\share\fixture' } 'network root'
$designation = @{ Id='fixture'; ComputerName='VM'; Consent='DISPOSABLE VM ONLY; SNAPSHOT MAY BE DESTROYED'; ExpiresUtc='2026-01-02T00:00:00Z' }
Assert-Designation $designation 'fixture' 'VM' ([DateTime]::Parse('2026-01-01T00:00:00Z'))
Reject { Assert-Designation $designation 'fixture' 'VM' ([DateTime]::Parse('2026-01-03T00:00:00Z')) } 'expired designation'
Reject { Assert-Designation $designation 'another-run' 'VM' ([DateTime]::Parse('2026-01-01T00:00:00Z')) } 'wrong run id'
Reject { Assert-Designation $designation 'fixture' 'HOST' ([DateTime]::Parse('2026-01-01T00:00:00Z')) } 'wrong computer name'
$designation.Consent = ''
Reject { Assert-Designation $designation 'fixture' 'VM' ([DateTime]::Parse('2026-01-01T00:00:00Z')) } 'missing explicit consent'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('vm-harness-build-' + [Guid]::NewGuid().ToString('N'))
$generated = Join-Path $PSScriptRoot 'package'
$createdGenerated = !(Test-Path -LiteralPath $generated)
if ($createdGenerated) { $null = New-Item -ItemType Directory -Path $generated }
$probe = Join-Path $generated ('compile-exclusion-' + [Guid]::NewGuid().ToString('N') + '.cs')
try {
    '#error Generated package source must not compile' | Set-Content -LiteralPath $probe
    $inputs = & dotnet msbuild (Join-Path $PSScriptRoot 'VmValidation.csproj') -nologo -getItem:Compile | Out-String
    if ($LASTEXITCODE) { throw 'Cannot inspect VM harness compilation inputs.' }
    $compile = @(($inputs | ConvertFrom-Json).Items.Compile)
    if ($compile.Count -ne 1 -or $compile[0].Identity -ne 'Program.cs') { throw 'VM harness must compile only Program.cs, excluding generated packages.' }
    Write-Output 'PASS generated package C# is excluded from harness compilation'
    & (Join-Path $PSScriptRoot 'Build.ps1') -Destination $fixture
    $package = Get-Content -LiteralPath (Join-Path $fixture 'package.json') -Raw | ConvertFrom-Json
    if ($package.Schema -ne 1 -or $package.Kind -ne 'vm-validation-package' -or $package.Source.Fingerprint -ne (Get-SecuritySource (Split-Path (Split-Path $PSScriptRoot))).Fingerprint -or !$package.BuildEnvironment.DotnetSdk) { throw 'Missing/current source and SDK provenance.' }
    Write-Output 'PASS package records current source fingerprint, revision and build environment'
    foreach ($entry in $package.Files) {
        if ((Get-FileHash -LiteralPath (Join-Path $fixture $entry.Name)).Hash -ne $entry.Hash) { throw "Package mismatch: $($entry.Name)" }
    }
    if (Test-Path -LiteralPath (Join-Path $fixture 'HytaleAccounts.exe')) { throw 'Real addon executable was packaged.' }
    foreach ($name in @('Runtime.ps1','RuntimeEvidence.ps1','ElevatedRuntime.ps1','RuntimeProbe.exe','D3DCompiler_47_cor3.dll','PenImc_cor3.dll','PresentationNative_cor3.dll','vcruntime140_cor3.dll','wpfgfx_cor3.dll')) {
        if (@($package.Files | Where-Object Name -eq $name).Count -ne 1) { throw "Missing runtime evidence package input: $name" }
    }
    Write-Output 'PASS runtime evidence collector, contract, production bootstrap and harmless single-file probe are hashed package inputs'
    Write-Output 'PASS package hashes and validation-only executable'
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $process = Start-Process -FilePath $dotnet -ArgumentList @('exec',(Join-Path $fixture 'Checks.dll'),'identity','C:\Windows') -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardError (Join-Path $fixture 'guard-error.txt') -RedirectStandardOutput (Join-Path $fixture 'guard-output.txt')
    if ($process.ExitCode -eq 0 -or (Get-Content -LiteralPath (Join-Path $fixture 'guard-error.txt') -Raw) -notlike '*VM guard refused execution*Only the designated fixed VM fixture root is allowed*') { throw 'Compiled guard refusal was not established.' }
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + (Join-Path $fixture 'Runtime.ps1') + '"'),'-Root','C:\Windows','-Mode','same-user','-Action','Worker') -WindowStyle Hidden -Wait -PassThru -RedirectStandardError (Join-Path $fixture 'runtime-refusal.txt')
    if ($process.ExitCode -eq 0 -or (Get-Content -LiteralPath (Join-Path $fixture 'runtime-refusal.txt') -Raw) -notlike '*Only the designated fixed VM fixture root is allowed*') { throw 'Runtime collector did not refuse host execution before work.' }
    Write-Output 'PASS runtime collector refuses non-designated host before launching apphost or requesting elevation'
    Write-Output 'PASS compiled runner rejects host/non-designated execution before test work'
} finally {
    if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe }
    if ($createdGenerated -and !(Get-ChildItem -LiteralPath $generated -Force)) { [IO.Directory]::Delete($generated) }
    $full = [IO.Path]::GetFullPath($fixture)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$full.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $full -Leaf) -notmatch '^vm-harness-build-[a-f0-9]{32}$') { throw 'Invalid build cleanup root.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
Write-Output 'HOST VALIDATION ONLY. No UAC, elevated staging, cross-user access or scheduled-task result was established.'
