param([string]$Destination = (Join-Path $PSScriptRoot 'package'))
$ErrorActionPreference = 'Stop'
# Building is host-safe. No VM designation, accounts, tasks, UAC or registry writes occur here.
if (Test-Path -LiteralPath $Destination) { throw 'Use a new, empty output directory.' }
$repository = Split-Path (Split-Path $PSScriptRoot)
. (Join-Path $repository 'Checks/SecurityEvidence.ps1')
$source = Get-SecuritySource $repository
$environment = Get-SecurityEnvironment
dotnet build (Join-Path $PSScriptRoot 'VmValidation.csproj') -c Release --nologo --configfile (Join-Path $PSScriptRoot 'NuGet.Config') -p:NuGetAudit=false
if ($LASTEXITCODE) { throw 'Harness build failed.' }
$null = New-Item -ItemType Directory -Path $Destination
$binary = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows'
foreach ($name in @('Checks.exe','Checks.dll','Checks.deps.json','Checks.runtimeconfig.json','HytaleAccounts.dll')) { Copy-Item -LiteralPath (Join-Path $binary $name) -Destination $Destination }
foreach ($name in @('Guard.ps1','Run.ps1','RepairProbe.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $Destination }
Copy-Item -LiteralPath (Join-Path $repository 'Checks/SecurityEvidence.ps1') -Destination $Destination
foreach ($name in @('InstallerSafety.ps1','ShortcutIntegration.ps1','ShortcutFile.cs','Repair-Integration.ps1')) { Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $Destination }
if ((Get-SecuritySource $repository).Fingerprint -ne $source.Fingerprint) { throw 'Source changed during VM package build.' }
@{ Schema=1; Kind='vm-validation-package'; HostUuid = (Get-CimInstance Win32_ComputerSystemProduct).UUID; BuiltUtc = [DateTime]::UtcNow.ToString('o'); Source=$source; BuildEnvironment=$environment;
    Files = @(Get-ChildItem -LiteralPath $Destination -File | ForEach-Object { @{ Name = $_.Name; Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Destination 'package.json') -Encoding UTF8
Write-Output "Prepared $Destination. This is a validation runner, not an addon installer."
