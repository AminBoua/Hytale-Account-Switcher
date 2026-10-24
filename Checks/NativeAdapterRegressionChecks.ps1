param([ValidateSet('None','AdapterJump','AdapterMetadata')][string]$Mutation = 'None')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('native-adapter-build-' + [Guid]::NewGuid().ToString('N'))
try {
    $launcher = Join-Path $fixture 'Launcher'; $checks = Join-Path $fixture 'Checks'
    [IO.Directory]::CreateDirectory($launcher) | Out-Null
    [IO.Directory]::CreateDirectory($checks) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repository 'Launcher') -File | Copy-Item -Destination $launcher
    foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $fixture
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'NativeAdapterChecks.cs') -Destination $checks
    $path = Join-Path $launcher 'NativeAdapter.cs'
    $source = Get-Content -LiteralPath $path -Raw
    $old = ''; $new = ''
    switch ($Mutation) {
        AdapterJump { $old = 'Jump(54, network)'; $new = 'Jump(54, stop)' }
        AdapterMetadata { $old = 'wrapper.Metadata + 16, 4), (uint)stack)'; $new = 'wrapper.Metadata + 16, 4), (uint)(stack + 1))' }
    }
    # Change only a disposable production-source copy; assertions and fixture stay identical.
    if ($old) {
        if ([regex]::Matches($source, [regex]::Escape($old)).Count -ne 1) { throw 'Native adapter mutation anchor changed.' }
        [IO.File]::WriteAllText($path, $source.Replace($old, $new))
    }
    'NativeAdapterChecks.Run((ok, message) => { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); });' |
        Set-Content -LiteralPath (Join-Path $checks 'Program.cs')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>Checks</AssemblyName></PropertyGroup>
  <ItemGroup><ProjectReference Include="../Launcher/Launcher.csproj" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $checks 'Checks.csproj')
    '<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath (Join-Path $fixture 'NuGet.Config')
    dotnet build (Join-Path $checks 'Checks.csproj') -c Release --nologo -p:NuGetAudit=false "-p:RestorePackagesPath=$fixture/packages"
    if ($LASTEXITCODE -ne 0) { throw 'Native adapter fixture build failed.' }
    dotnet (Join-Path $checks 'bin/Release/net10.0-windows/Checks.dll')
    if ($LASTEXITCODE -ne 0) { throw "Native adapter checks failed (mutation: $Mutation)." }
    if ($Mutation -ne 'None') { throw "Mutation survived: $Mutation" }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('native-adapter-build-')) { throw 'Cleanup escaped build fixture.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
