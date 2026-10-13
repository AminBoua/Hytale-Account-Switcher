param([switch]$ChecksOnly, [switch]$SecurityValidated, [string]$VmEvidence, [switch]$RegressionControls)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $security = & ./Checks/Run-SecurityChecks.ps1 -VmEvidence $VmEvidence -RegressionControls:$RegressionControls
    if ($SecurityValidated -and !$security.SecurityValidated) { throw ('Release is UNVERIFIED: ' + $security.VmStatus.Reason) }
    if ($ChecksOnly) { return }
    dotnet publish Launcher/Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o dist/build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath dist/build/HytaleAccounts.exe -Destination dist/HytaleAccounts-Setup.exe -Force
    $hash = (Get-FileHash -LiteralPath dist/HytaleAccounts-Setup.exe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'dist\SHA256SUMS.txt'), "$hash  HytaleAccounts-Setup.exe`n")
    . ./Checks/SecurityEvidence.ps1
    if ((Get-SecuritySource $PSScriptRoot).Fingerprint -ne $security.Source.Fingerprint) { throw 'Source changed after checks; artifact is not validated.' }
    @{ Artifact='HytaleAccounts-Setup.exe'; SHA256=$hash; Source=$security.Source; SecurityValidated=$security.SecurityValidated;
        SafeReport=$security.ReportPath; SafeReportHash=(Get-FileHash -LiteralPath $security.ReportPath).Hash; VmStatus=$security.VmStatus } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath dist/SECURITY-STATUS.json -Encoding UTF8
    if (!$security.SecurityValidated) { Write-Warning 'Build is NOT security-validated: actual VM validation is unverified.' }
    Write-Output 'Ready: dist/HytaleAccounts-Setup.exe'
} finally { Pop-Location }
