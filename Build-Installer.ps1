param([switch]$ChecksOnly, [switch]$SecurityValidated, [string]$VmEvidence, [switch]$RegressionControls)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $security = & ./Checks/Run-SecurityChecks.ps1 -VmEvidence $VmEvidence -RegressionControls:$RegressionControls
    if ($SecurityValidated -and !$security.SecurityValidated) { throw ('Release is UNVERIFIED: ' + $security.VmStatus.Reason) }
    if ($ChecksOnly) { return }
    # Native WPF files are sidecars; ordinary apphost startup has no extraction route.
    dotnet publish Launcher/Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=None -p:DebugSymbols=false -o dist/build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $runtime = & dotnet msbuild Launcher/Launcher.csproj -nologo -target:ResolveReferences -p:Configuration=Release -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -getItem:ResolvedRuntimePack,RuntimePackAsset | Out-String
    if ($LASTEXITCODE) { throw 'Cannot identify bundled runtime licenses.' }
    $items = ($runtime | ConvertFrom-Json).Items
    $used = @($items.RuntimePackAsset.NuGetPackageId | Sort-Object -Unique)
    . ./Write-RuntimeNotices.ps1
    Write-RuntimeNotices @($items.ResolvedRuntimePack | Where-Object NuGetPackageId -In $used) (Join-Path $PSScriptRoot 'dist/THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath LICENSE -Destination dist/LICENSE -Force
    Copy-Item -LiteralPath dist/build/HytaleAccounts.exe -Destination dist/HytaleAccounts-Setup.exe -Force
    $sidecars = @('D3DCompiler_47_cor3.dll','PenImc_cor3.dll','PresentationNative_cor3.dll','vcruntime140_cor3.dll','wpfgfx_cor3.dll','LICENSE','THIRD-PARTY-NOTICES.txt')
    foreach ($name in $sidecars | Where-Object { $_ -like '*.dll' }) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot "dist/build/$name") -Destination dist -Force }
    $hash = (Get-FileHash -LiteralPath dist/HytaleAccounts-Setup.exe -Algorithm SHA256).Hash.ToLowerInvariant()
    . ./Checks/SecurityEvidence.ps1
    if ((Get-SecuritySource $PSScriptRoot).Fingerprint -ne $security.Source.Fingerprint) { throw 'Source changed after checks; artifact is not validated.' }
    Compress-Archive -LiteralPath (@('dist/HytaleAccounts-Setup.exe') + @($sidecars | ForEach-Object { Join-Path 'dist' $_ })) -DestinationPath dist/HytaleAccounts-Setup.zip -Force
    @{ Artifact='HytaleAccounts-Setup.exe'; SHA256=$hash; ArchiveSHA256=(Get-FileHash -LiteralPath dist/HytaleAccounts-Setup.zip -Algorithm SHA256).Hash;
        RuntimeFiles=@($sidecars | ForEach-Object { @{ Name=$_; SHA256=(Get-FileHash -LiteralPath (Join-Path 'dist' $_) -Algorithm SHA256).Hash } }); Source=$security.Source; SecurityValidated=$security.SecurityValidated;
        SafeReport=$security.ReportPath; SafeReportHash=(Get-FileHash -LiteralPath $security.ReportPath).Hash; VmStatus=$security.VmStatus } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath dist/SECURITY-STATUS.json -Encoding UTF8
    $inspection = & dotnet run --project Checks/Checks.csproj -c Release --no-build --no-restore -- --runtime-package dist/HytaleAccounts-Setup.exe dist/HytaleAccounts-Setup.zip $PSScriptRoot
    if ($LASTEXITCODE) { throw 'Release runtime/package inspection failed.' }
    $inspection | Set-Content -LiteralPath dist/RUNTIME-PACKAGE.json -Encoding UTF8
    $sums = (@('HytaleAccounts-Setup.exe','HytaleAccounts-Setup.zip') + $sidecars) | ForEach-Object {
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "dist/$_") -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_
    }
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'dist/SHA256SUMS.txt'), ($sums -join "`n") + "`n")
    if (!$security.SecurityValidated) { Write-Warning 'Build is NOT security-validated: actual VM validation is unverified.' }
    Write-Output 'Ready: dist/HytaleAccounts-Setup.zip (installer, native runtime sidecars and licenses)'
} finally { Pop-Location }
