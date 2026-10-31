param([ValidateSet('None','InstallModules','UninstallModules','PublisherModules')][string]$Mutation = 'None')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('powershell-modules-build-' + [Guid]::NewGuid().ToString('N'))
try {
    $launcher = Join-Path $fixture 'Launcher'; $checks = Join-Path $fixture 'Checks'
    [IO.Directory]::CreateDirectory($launcher) | Out-Null
    [IO.Directory]::CreateDirectory($checks) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repository 'Launcher') -File | Copy-Item -Destination $launcher
    foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $fixture
    }
    foreach ($name in @('PowerShellModuleChecks.cs','SecurityFixture.cs')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $launcher
    }
    function Replace-Once([string]$Path, [string]$Old, [string]$New) {
        $source = [IO.File]::ReadAllText($Path)
        if ([regex]::Matches($source, [regex]::Escape($Old)).Count -ne 1) { throw "Module fixture boundary changed: $Old" }
        [IO.File]::WriteAllText($Path, $source.Replace($Old, $New))
    }
    # Observe the actual publisher entry point's ProcessStartInfo and model missing-path default discovery.
    # Preserve its configuration construction and execute the actual signature-verification command.
    $publisher = Join-Path $launcher 'IntegrationSafety.cs'
    Replace-Once $publisher 'Process.Start(start)!;' 'PowerShellModuleChecks.StartPublisher(start);'
    $restriction = 'start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");'
    switch ($Mutation) {
        InstallModules { Replace-Once (Join-Path $launcher 'Setup.cs') $restriction '// Module restriction removed by fixture.' }
        PublisherModules { Replace-Once $publisher $restriction 'start.Environment.Remove("PSModulePath");' }
        UninstallModules {
            Replace-Once (Join-Path $fixture 'Uninstall.ps1') '$env:PSModulePath = [IO.Path]::Combine([Environment]::SystemDirectory, ''WindowsPowerShell\v1.0\Modules'')' '$null = $null'
        }
    }
    @'
await PowerShellModuleChecks.Run((result, message) => {
    if (!result) throw new Exception("FAIL " + message);
    Console.WriteLine("PASS " + message);
});
'@ | Set-Content -LiteralPath (Join-Path $checks 'Program.cs')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="../Launcher/Launcher.csproj" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $checks 'Checks.csproj')
    '<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath (Join-Path $fixture 'NuGet.Config')
    dotnet build (Join-Path $checks 'Checks.csproj') -c Release --nologo -p:NuGetAudit=false "-p:RestorePackagesPath=$fixture/packages"
    if ($LASTEXITCODE) { throw 'PowerShell module fixture build failed.' }
    dotnet (Join-Path $checks 'bin/Release/net10.0-windows/Checks.dll')
    if ($LASTEXITCODE) { throw "PowerShell module checks failed (mutation: $Mutation)." }
    if ($Mutation -ne 'None') { throw "Mutation survived: $Mutation" }
} finally {
    $full = [IO.Path]::GetFullPath($fixture)
    if ((Split-Path $full) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or (Split-Path $full -Leaf) -cnotmatch '^powershell-modules-build-[a-f0-9]{32}$') { throw 'Cleanup escaped module build fixture.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
