param([ValidateSet('None','DestinationPin')][string]$Mutation = 'None')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('setup-upgrade-build-' + [Guid]::NewGuid().ToString('N'))
try {
    $launcher = Join-Path $fixture 'Launcher'; $checks = Join-Path $fixture 'Checks'
    [IO.Directory]::CreateDirectory($launcher) | Out-Null
    [IO.Directory]::CreateDirectory($checks) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repository 'Launcher') -File | Copy-Item -Destination $launcher
    foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $fixture
    }
    foreach ($name in @('SetupUpgradeChecks.cs','SecurityFixture.cs')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $launcher
    }
    function Replace-Once([string]$Old, [string]$New) {
        if ([regex]::Matches($script:source, [regex]::Escape($Old)).Count -ne 1) { throw "Setup fixture boundary changed: $Old" }
        $script:source = $script:source.Replace($Old, $New)
    }
    $path = Join-Path $launcher 'Setup.cs'; $source = Get-Content -LiteralPath $path -Raw
    # Only this disposable worker maps elevation, system locations and embedded resources to user-owned fixtures.
    Replace-Once 'new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)' 'true'
    Replace-Once 'var executable = Environment.ProcessPath!;' 'var executable = SetupUpgradeChecks.Executable;'
    Replace-Once 'var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);' 'var programs = SetupUpgradeChecks.Programs;'
    Replace-Once 'var launcherDirectory = Path.GetDirectoryName(IntegrationSafety.OfficialPath)!;' 'var launcherDirectory = Path.GetDirectoryName(SetupUpgradeChecks.OfficialPath)!;'
    Replace-Once 'name => Assembly.GetExecutingAssembly().GetManifestResourceStream("HytaleAccounts." + name) ?? throw new IOException("Setup resource is missing: " + name),' 'SetupUpgradeChecks.Resource,'
    Replace-Once 'SetupPayload.Permissions());' 'SetupUpgradeChecks.Permissions());'
    if ($Mutation -eq 'DestinationPin') {
        Replace-Once 'new SetupPayload.PinnedDirectories(destination, requireTrusted: true, forMutation: true)' 'new SetupPayload.PinnedDirectories(destination, requireTrusted: true)'
    }
    [IO.File]::WriteAllText($path, $source)
    $path = Join-Path $launcher 'StorageSafety.cs'; $source = Get-Content -LiteralPath $path -Raw
    # The fixture owner stands in for Administrators; retain the actual ACL, link and handle validation.
    Replace-Once '(!administratorsOnly && sid == User)' '(sid == User)'
    [IO.File]::WriteAllText($path, $source)
    'await HytaleAccounts.SetupUpgradeChecks.Run(args[0]);' | Set-Content -LiteralPath (Join-Path $checks 'Program.cs')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="../Launcher/Launcher.csproj" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $checks 'Checks.csproj')
    '<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath (Join-Path $fixture 'NuGet.Config')
    dotnet build (Join-Path $checks 'Checks.csproj') -c Release --nologo -p:NuGetAudit=false "-p:RestorePackagesPath=$fixture/packages"
    if ($LASTEXITCODE -ne 0) { throw 'Setup upgrade fixture build failed.' }
    dotnet (Join-Path $checks 'bin/Release/net10.0-windows/Checks.dll') $repository
    if ($LASTEXITCODE -ne 0) { throw "Setup upgrade checks failed (mutation: $Mutation)." }
    if ($Mutation -ne 'None') { throw "Mutation survived: $Mutation" }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('setup-upgrade-build-')) { throw 'Cleanup escaped build fixture.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
