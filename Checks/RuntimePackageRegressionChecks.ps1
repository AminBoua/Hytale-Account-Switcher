param([string]$PackageDirectory = (Join-Path (Split-Path $PSScriptRoot) 'dist'))
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$package = (Resolve-Path -LiteralPath $PackageDirectory).ProviderPath
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('package-inspection-' + [Guid]::NewGuid().ToString('N'))
$inspector = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows/Checks.dll'
function Inspect([string]$Source, [string]$Archive, [string]$ExpectedFailure) {
    try {
        $ErrorActionPreference = 'Continue' # Drain expected failing inspector stderr before checking its assertion.
        $output = (& dotnet $inspector --runtime-package (Join-Path $package 'HytaleAccounts-Setup.exe') $Archive $Source 2>&1 | ForEach-Object { $_.ToString() }) -join "`n"
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = 'Stop' }
    if ($ExpectedFailure) {
        if (!$code -or !$output.Contains($ExpectedFailure)) { throw "Package control missed '$ExpectedFailure': $output" }
        Write-Host "PASS package rejects $ExpectedFailure"
    } else {
        if ($code) { throw "Package baseline failed: $output" }
        Write-Host 'PASS package baseline matches actual source, versions, resources and ZIP bytes'
    }
}
try {
    $null = New-Item -ItemType Directory -Path (Join-Path $fixture 'Launcher')
    foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $fixture
    }
    foreach ($name in @('Launcher.csproj','ElevatedRuntime.ps1','NativeDropdown.js')) {
        Copy-Item -LiteralPath (Join-Path $repository "Launcher/$name") -Destination (Join-Path $fixture 'Launcher')
    }
    $assembly = 'Launcher/obj/Release/net10.0-windows/win-x64/HytaleAccounts.dll'
    $null = New-Item -ItemType Directory -Path (Split-Path (Join-Path $fixture $assembly)) -Force
    Copy-Item -LiteralPath (Join-Path $repository $assembly) -Destination (Join-Path $fixture $assembly)
    $archive = Join-Path $package 'HytaleAccounts-Setup.zip'
    Inspect $fixture $archive
    $compiled = Join-Path $fixture $assembly
    $original = [IO.File]::ReadAllBytes($compiled)
    try {
        $changed = [byte[]]$original.Clone()
        $changed[0] = $changed[0] -bxor 1
        [IO.File]::WriteAllBytes($compiled, $changed)
        Inspect $fixture $archive 'Packaged helper differs from current published assembly.'
    } finally { [IO.File]::WriteAllBytes($compiled, $original) }
    foreach ($case in @(
        @('Launcher/Launcher.csproj','<Version>[^<]+</Version>','<Version>99.0.0</Version>','Packaged assembly version differs from project.'),
        @('Install.ps1',"DisplayVersion = '[^']+'","DisplayVersion = '99.0.0'",'Windows Apps version differs from project.'),
        @('Launcher/NativeDropdown.js','\z',"`n// stale frontend",'Packaged resource differs from checked source: HytaleAccounts.NativeDropdown.js')
    )) {
        $path = Join-Path $fixture $case[0]
        $original = [IO.File]::ReadAllBytes($path)
        try {
            [IO.File]::WriteAllText($path, [regex]::Replace([IO.File]::ReadAllText($path), $case[1], $case[2]))
            Inspect $fixture $archive $case[3]
        } finally { [IO.File]::WriteAllBytes($path, $original) }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($mutation in @('MissingSidecar','ChangedSidecar','DuplicateEntry')) {
        $changed = Join-Path $fixture ($mutation + '.zip')
        Copy-Item -LiteralPath $archive -Destination $changed
        $zip = [IO.Compression.ZipFile]::Open($changed, 'Update')
        try {
            $zip.GetEntry('PenImc_cor3.dll').Delete()
            if ($mutation -ne 'MissingSidecar') {
                $name = if ($mutation -eq 'DuplicateEntry') { 'LICENSE' } else { 'PenImc_cor3.dll' }
                $stream = $zip.CreateEntry($name).Open()
                try { $stream.WriteByte(0) } finally { $stream.Dispose() }
            }
        } finally { $zip.Dispose() }
        $expected = if ($mutation -eq 'ChangedSidecar') { 'Release ZIP bytes differ: PenImc_cor3.dll' } else { 'Unexpected release ZIP contents.' }
        Inspect $fixture $changed $expected
    }
    Inspect $fixture $archive
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('package-inspection-')) { throw 'Cleanup escaped package fixture.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
