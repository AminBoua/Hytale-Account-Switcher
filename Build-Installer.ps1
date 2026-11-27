param([switch]$ChecksOnly)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Checks/ReleaseChecks.ps1')
Push-Location $PSScriptRoot
try {
    $source = Get-RebuildSource
    Invoke-SafeChecks
    if ((Get-RebuildSource) -ne $source) { throw 'Source changed during the checks.' }
    if ($ChecksOnly) { return }

    $dist = Join-Path $PSScriptRoot 'dist'
    [void](New-Item -ItemType Directory -Force -Path $dist)
    $stage = Join-Path $dist ('publish-' + [Guid]::NewGuid().ToString('N'))
    try {
        Invoke-Checked dotnet @('publish', 'Launcher/Launcher.csproj', '-c', 'Release', '-r', 'win-x64',
            '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=None', '-o', $stage)
        $resolve = @('msbuild', 'Launcher/Launcher.csproj', '-nologo', '-target:ResolveReferences',
            '-p:Configuration=Release', '-p:RuntimeIdentifier=win-x64', '-p:SelfContained=true',
            '-getItem:ResolvedRuntimePack,RuntimePackAsset')
        $resolved = & dotnet @resolve | Out-String
        if ($LASTEXITCODE) { throw 'Cannot identify the bundled runtime.' }
        $items = ($resolved | ConvertFrom-Json).Items
        $packIds = @($items.RuntimePackAsset.NuGetPackageId | Sort-Object -Unique)
        $packs = @($items.ResolvedRuntimePack | Where-Object NuGetPackageId -In $packIds)
        if (!$packs.Count) { throw 'Cannot find the license for the bundled runtime.' }
        $notices = foreach ($pack in $packs) {
            $texts = @(Get-ChildItem -LiteralPath $pack.PackageDirectory -File |
                Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\..*)?$' | Sort-Object Name)
            if (!@($texts | Where-Object Name -Match '^LICENSE(\..*)?$').Count) { throw "Runtime license missing for $($pack.NuGetPackageId)." }
            foreach ($file in $texts) {
                "=== $($pack.NuGetPackageId) $($pack.NuGetPackageVersion) / $($file.Name) ==="
                [IO.File]::ReadAllText($file.FullName)
            }
        }
        [IO.File]::WriteAllLines((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), [string[]]$notices)
        Copy-Item -LiteralPath LICENSE -Destination $stage

        $payload = @('HytaleAccounts.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.txt') +
            @(Get-ChildItem -LiteralPath $stage -File -Filter '*.dll' | Sort-Object Name | ForEach-Object Name)
        $version = (([xml](Get-Content -LiteralPath Launcher/Launcher.csproj -Raw)).Project.PropertyGroup.Version).Trim()
        $manifest = [ordered]@{
            Version = $version
            Files = @(foreach ($name in $payload) {
                [ordered]@{ Name = $name; Sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
            })
        }
        [IO.File]::WriteAllText((Join-Path $stage 'addon-payload.json'), ($manifest | ConvertTo-Json -Depth 4))
        Rename-Item -LiteralPath (Join-Path $stage 'HytaleAccounts.exe') -NewName 'HytaleAccounts-Setup.exe'
        $names = @('HytaleAccounts-Setup.exe', 'addon-payload.json') + @($payload | Where-Object { $_ -ne 'HytaleAccounts.exe' })
        $zip = Join-Path $stage 'HytaleAccounts-Setup.zip'
        Compress-Archive -LiteralPath @($names | ForEach-Object { Join-Path $stage $_ }) -DestinationPath $zip

        Invoke-Checked dotnet @('run', '--project', 'Checks/Checks.csproj', '-c', 'Release', '--no-build', '--', '--package', $zip)
        if ((Get-RebuildSource) -ne $source) { throw 'Source changed during package generation.' }

        $archiveHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        $status = [ordered]@{
            SourceSha256 = $source; ArchiveSha256 = $archiveHash; Version = $version
            SafeChecks = 'passed'; StaticPackageInspection = 'passed'
            ActualVm = 'unverified'; LiveHytale = 'unverified'; SecurityValidated = $false
        }
        $output = Join-Path $dist 'HytaleAccounts-Setup.zip'
        if (Test-Path -LiteralPath $output) { [IO.File]::Replace($zip, $output, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($zip, $output) }
        $status | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $dist 'SECURITY-STATUS.json')
        [IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), "$archiveHash  HytaleAccounts-Setup.zip" + [Environment]::NewLine)

        Write-Output 'Created dist/HytaleAccounts-Setup.zip. Actual VM and live Hytale checks are unverified.'
    } finally {
        if (Test-Path -LiteralPath $stage) {
            foreach ($file in Get-ChildItem -LiteralPath $stage -File) { Remove-Item -LiteralPath $file.FullName -Force }
            if (!(Get-ChildItem -LiteralPath $stage -Force)) { Remove-Item -LiteralPath $stage }
        }
    }
} finally { Pop-Location }
