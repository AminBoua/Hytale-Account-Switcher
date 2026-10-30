param([ValidateSet('OriginalHandoff')][string]$Mutation = 'OriginalHandoff')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('uninstall-regression-build-' + [Guid]::NewGuid().ToString('N'))
try {
    $launcher = Join-Path $fixture 'Launcher'; $checks = Join-Path $fixture 'Checks'
    [IO.Directory]::CreateDirectory($launcher) | Out-Null
    [IO.Directory]::CreateDirectory($checks) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repository 'Launcher') -File | Copy-Item -Destination $launcher
    foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $fixture
    }
    foreach ($name in @('UninstallFinalizationChecks.cs','UninstallFinalizationFixture.ps1','UninstallFinalizationScriptChecks.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $checks
    }
    $path = Join-Path $fixture 'Uninstall.ps1'
    $source = Get-Content -LiteralPath $path -Raw
    $guard = 'if (!$UserOnly) { Wait-RemovalFinalization $WaitForPid $RemovalAttempt }'
    if ([regex]::Matches($source, [regex]::Escape($guard)).Count -ne 1) { throw 'Finalization guard boundary changed.' }
    # Recreate the original parent-exit-only handoff in this disposable copy.
    [IO.File]::WriteAllText($path, $source.Replace($guard, 'if ($WaitForPid) { Wait-Process -Id $WaitForPid -Timeout 30 -ErrorAction SilentlyContinue }'))
    'await UninstallFinalizationChecks.Run(args);' | Set-Content -LiteralPath (Join-Path $checks 'Program.cs')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="../Launcher/Launcher.csproj" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $checks 'Checks.csproj')
    '<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath (Join-Path $fixture 'NuGet.Config')
    dotnet build (Join-Path $checks 'Checks.csproj') -c Release --nologo -p:NuGetAudit=false "-p:RestorePackagesPath=$fixture/packages"
    if ($LASTEXITCODE) { throw 'Uninstall regression fixture build failed.' }
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $checks 'UninstallFinalizationScriptChecks.ps1')
    if ($LASTEXITCODE) { throw 'Original uninstall handoff failed the regression as expected; inspect the exact assertion.' }
    throw 'Original uninstall handoff survived the regression.'
} finally {
    $full = [IO.Path]::GetFullPath($fixture)
    if ((Split-Path $full) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or (Split-Path $full -Leaf) -cnotmatch '^uninstall-regression-build-[a-f0-9]{32}$') { throw 'Cleanup escaped regression build.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
