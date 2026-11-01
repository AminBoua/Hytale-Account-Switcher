# Compile production entry logic in a disposable copy with fail-closed boundary substitutions.
# These substitutions are test seams, not an implementation of user-context handoff.
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('settings-entry-build-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $source = Get-Content -LiteralPath (Join-Path $repository 'Launcher\Setup.cs') -Raw
    $boundaries = @{
        'public static class Setup' = 'public static class SettingsSetup'
        'private static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);' = 'private static bool IsAdministrator => SettingsEntryProbe.Elevated;'
        'var destination = Path.Combine(Path.GetDirectoryName(IntegrationSafety.OfficialPath)!, ".hytale-accounts-addon");' = 'var destination = SettingsEntryProbe.Destination;'
        'await Remove(destination, IntegrateUser, start =>' = 'await SettingsEntryProbe.Remove(destination, IntegrateUser, start =>'
    }
    foreach ($boundary in $boundaries.Keys) {
        if ([regex]::Matches($source, [regex]::Escape($boundary)).Count -ne 1) { throw "Entry fixture seam changed: $boundary" }
        $source = $source.Replace($boundary, $boundaries[$boundary])
    }
    # A missed interception fails immediately; never start a real installer/remover/browser.
    $source = 'using System.IO;' + [Environment]::NewLine + $source.Replace('Process.Start(', 'SettingsEntryProbe.Start(')
    [IO.File]::WriteAllText((Join-Path $fixture 'Setup.cs'), $source)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SettingsUninstallEntryChecks.cs') -Destination $fixture
    'return await HytaleAccounts.SettingsEntryProbe.Run();' | Set-Content (Join-Path $fixture 'Program.cs')
    $project = [Security.SecurityElement]::Escape((Join-Path $repository 'Launcher\Launcher.csproj'))
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><AssemblyName>Checks</AssemblyName><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$project" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $fixture 'Checks.csproj')
    dotnet build (Join-Path $fixture 'Checks.csproj') -c Release --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw 'Entry fixture build failed.' }
    dotnet (Join-Path $fixture 'bin\Release\net10.0-windows\Checks.dll')
    $result = $LASTEXITCODE
} finally {
    $full = [IO.Path]::GetFullPath($fixture)
    if ((Split-Path $full) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or (Split-Path $full -Leaf) -cnotmatch '^settings-entry-build-[a-f0-9]{32}$') { throw 'Cleanup escaped entry fixture.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
exit $result
