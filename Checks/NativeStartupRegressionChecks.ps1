param([switch]$SwallowInitialFailure)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('native-startup-build-' + [Guid]::NewGuid().ToString('N'))
try {
    $launcher = Join-Path $fixture 'Launcher'; $checks = Join-Path $fixture 'Checks'
    [IO.Directory]::CreateDirectory($launcher) | Out-Null
    [IO.Directory]::CreateDirectory($checks) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repository 'Launcher') -File | Copy-Item -Destination $launcher
    foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $fixture
    }
    foreach ($name in @('NativeStartupFailureChecks.cs','NativeStartupRecoveryChecks.cs','SecurityFixture.cs')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $launcher
    }
    $path = Join-Path $launcher 'NativeSession.cs'
    $source = Get-Content -LiteralPath $path -Raw
    $originalSource = $source
    # Keep OpenInitial/OpenCore/recovery unchanged; replace only the native process boundaries in this disposable copy.
    $start = '(?s)    private async Task<bool> Start\(NativeStore store, bool commit\).*?(?=    private async Task ObserveExit)'
    $stop = '(?s)    private async Task Stop\(\).*?(?=\r?\n\})'
    if ([regex]::Matches($source, $start).Count -ne 1 -or [regex]::Matches($source, $stop).Count -ne 1) { throw 'Startup fixture boundaries changed.' }
    $source = [regex]::Replace($source, $start, @'
    private Task<bool> Start(NativeStore store, bool commit)
    {
        active = store;
        var authenticated = NativeStartupFailureChecks.Start(store);
        launcher = Process.GetCurrentProcess();
        return Task.FromResult(authenticated);
    }

'@)
    $source = [regex]::Replace($source, $stop, @'
    private Task Stop()
    {
        launcher?.Dispose(); launcher = null; active = null;
        return Task.CompletedTask;
    }
'@)
    if ($SwallowInitialFailure) {
        $anchor = 'if (initial) throw;'
        if ([regex]::Matches($source, [regex]::Escape($anchor)).Count -ne 1) { throw 'Startup mutation anchor changed.' }
        $source = $source.Replace($anchor, '')
    }
    [IO.File]::WriteAllText($path, $source)
    'await NativeStartupFailureChecks.Run();' | Set-Content -LiteralPath (Join-Path $checks 'Program.cs')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="../Launcher/Launcher.csproj" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $checks 'Checks.csproj')
    '<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath (Join-Path $fixture 'NuGet.Config')
    dotnet build (Join-Path $checks 'Checks.csproj') -c Release --nologo -p:NuGetAudit=false "-p:RestorePackagesPath=$fixture/packages"
    if ($LASTEXITCODE -ne 0) { throw 'Startup fixture build failed.' }
    dotnet (Join-Path $checks 'bin/Release/net10.0-windows/Checks.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Startup propagation checks failed.' }
    if ($SwallowInitialFailure) { throw 'Mutation survived: swallowed initial failure' }
    # Exercise production Start, including verification, storage and fence release; replace only launching/exit observation.
    $launch = '(?s)        var prepared = await stores\.PrepareIntegrated\(store, bridge\);.*?(?=        var process = launcher;)'
    $completion = '        var profile = await verified;'
    $observe = '        _ = ObserveExit(process);'
    foreach ($anchor in @($launch, [regex]::Escape($completion), [regex]::Escape($observe))) {
        if ([regex]::Matches($originalSource, $anchor).Count -ne 1) { throw 'Startup recovery fixture boundary changed.' }
    }
    $source = [regex]::Replace($originalSource, $launch, "        launcher = NativeStartupRecoveryChecks.Launch(this);`n")
    $source = $source.Replace($completion, $completion + "`n        NativeStartupRecoveryChecks.BeforeComplete?.Invoke(this);").Replace($observe, '')
    [IO.File]::WriteAllText($path, $source)
    'await NativeStartupRecoveryChecks.Run();' | Set-Content -LiteralPath (Join-Path $checks 'Program.cs')
    dotnet build (Join-Path $checks 'Checks.csproj') -c Release --nologo -p:NuGetAudit=false "-p:RestorePackagesPath=$fixture/packages"
    if ($LASTEXITCODE -ne 0) { throw 'Startup recovery fixture build failed.' }
    dotnet (Join-Path $checks 'bin/Release/net10.0-windows/Checks.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Startup recovery fence checks failed.' }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('native-startup-build-')) { throw 'Cleanup escaped build fixture.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
