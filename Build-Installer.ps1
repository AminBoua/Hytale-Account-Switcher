param([switch]$ChecksOnly, [switch]$SecurityValidated, [string]$VmEvidence, [switch]$RegressionControls)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $security = & ./Checks/Run-SecurityChecks.ps1 -VmEvidence $VmEvidence -RegressionControls:$RegressionControls
    if ($SecurityValidated -and !$security.SecurityValidated) { throw ('Release is UNVERIFIED: ' + $security.VmStatus.Reason) }
    if ($ChecksOnly) { return }
    # Native self-extraction is retained; the embedded pre-apphost elevation bootstrap supplies its protected cache.
    dotnet publish Launcher/Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o dist/build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $runtime = & dotnet msbuild Launcher/Launcher.csproj -nologo -target:ResolveReferences -p:Configuration=Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -getItem:ResolvedRuntimePack,RuntimePackAsset | Out-String
    if ($LASTEXITCODE) { throw 'Cannot identify bundled runtime licenses.' }
    $items = ($runtime | ConvertFrom-Json).Items
    $used = @($items.RuntimePackAsset.NuGetPackageId | Sort-Object -Unique)
    . ./Write-RuntimeNotices.ps1
    Write-RuntimeNotices @($items.ResolvedRuntimePack | Where-Object NuGetPackageId -In $used) (Join-Path $PSScriptRoot 'dist/THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath LICENSE -Destination dist/LICENSE -Force
    Copy-Item -LiteralPath dist/build/HytaleAccounts.exe -Destination dist/HytaleAccounts-Setup.exe -Force
    $hash = (Get-FileHash -LiteralPath dist/HytaleAccounts-Setup.exe -Algorithm SHA256).Hash.ToLowerInvariant()
    . ./Checks/SecurityEvidence.ps1
    if ((Get-SecuritySource $PSScriptRoot).Fingerprint -ne $security.Source.Fingerprint) { throw 'Source changed after checks; artifact is not validated.' }
    @{ Artifact='HytaleAccounts-Setup.exe'; SHA256=$hash; Source=$security.Source; SecurityValidated=$security.SecurityValidated;
        SafeReport=$security.ReportPath; SafeReportHash=(Get-FileHash -LiteralPath $security.ReportPath).Hash; VmStatus=$security.VmStatus } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath dist/SECURITY-STATUS.json -Encoding UTF8
    Compress-Archive -LiteralPath dist/HytaleAccounts-Setup.exe,dist/LICENSE,dist/THIRD-PARTY-NOTICES.txt -DestinationPath dist/HytaleAccounts-Setup.zip -Force
    $inspection = & dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --runtime-package dist/HytaleAccounts-Setup.exe dist/HytaleAccounts-Setup.zip $PSScriptRoot
    if ($LASTEXITCODE) { throw 'Release runtime/package inspection failed.' }
    $inspection | Set-Content -LiteralPath dist/RUNTIME-PACKAGE.json -Encoding UTF8
    $sums = @('HytaleAccounts-Setup.exe','HytaleAccounts-Setup.zip','LICENSE','THIRD-PARTY-NOTICES.txt') | ForEach-Object {
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "dist/$_") -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_
    }
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'dist/SHA256SUMS.txt'), ($sums -join "`n") + "`n")
    if (!$security.SecurityValidated) { Write-Warning 'Build is NOT security-validated: actual VM validation is unverified.' }
    Write-Output 'Ready: dist/HytaleAccounts-Setup.zip (installer and licenses)'
} finally { Pop-Location }
