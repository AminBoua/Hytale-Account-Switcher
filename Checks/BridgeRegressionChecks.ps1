param([ValidateSet('None','BodyTimeout','HandlerTimeout','HandlerSlots')][string]$Mutation = 'None')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('bridge-regression-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BridgeLimitChecks.cs') -Destination $fixture
    $source = Get-Content -LiteralPath (Join-Path $repository 'Launcher/LocalAccountBridge.cs') -Raw
    $old = ''; $new = ''
    switch ($Mutation) {
        BodyTimeout { $old = 'ReadExactlyAsync(payload, timeout.Token).AsTask().WaitAsync(timeout.Token)'; $new = 'ReadExactlyAsync(payload, timeout.Token)' }
        HandlerTimeout { $old = 'await work.WaitAsync(token)'; $new = 'await work' }
        HandlerSlots { $old = 'if (!commandSlots.Wait(0))'; $new = 'if (false && !commandSlots.Wait(0))' }
    }
    if ($old) {
        if ([regex]::Matches($source, [regex]::Escape($old)).Count -ne 1) { throw 'Bridge mutation anchor changed.' }
        $source = $source.Replace($old, $new)
    }
    [IO.File]::WriteAllText((Join-Path $fixture 'LocalAccountBridge.cs'), $source)
    'await BridgeLimitChecks.Run((ok, message) => { if (!ok) throw new Exception("FAIL " + message); Console.WriteLine("PASS " + message); });' |
        Set-Content -LiteralPath (Join-Path $fixture 'Program.cs')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $fixture 'Checks.csproj')
    '<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath (Join-Path $fixture 'NuGet.Config')
    dotnet build (Join-Path $fixture 'Checks.csproj') -c Release --nologo -p:NuGetAudit=false "-p:RestorePackagesPath=$fixture/packages"
    if ($LASTEXITCODE -ne 0) { throw 'Bridge fixture build failed.' }
    dotnet (Join-Path $fixture 'bin/Release/net10.0-windows/Checks.dll')
    if ($LASTEXITCODE -ne 0) { throw "Bridge checks failed (mutation: $Mutation)." }
    if ($Mutation -ne 'None') { throw "Mutation survived: $Mutation" }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('bridge-regression-')) { throw 'Cleanup escaped build fixture.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
