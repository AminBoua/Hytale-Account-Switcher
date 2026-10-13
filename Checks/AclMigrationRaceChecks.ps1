param([switch]$BuildOnly, [switch]$PinsOnly, [switch]$Storage, [switch]$StorageSafety, [ValidateSet('None','Membership','Propagation','Pins','Policy','PrivateReplacement')][string]$Mutation = 'None')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('acl-race-build-' + [Guid]::NewGuid().ToString('N'))
try {
    $launcher = Join-Path $fixture 'Launcher'; $checks = Join-Path $fixture 'Checks'
    [IO.Directory]::CreateDirectory($launcher) | Out-Null
    [IO.Directory]::CreateDirectory($checks) | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repository 'Launcher') -File | Copy-Item -Destination $launcher
    foreach ($name in @('Install.ps1','Uninstall.ps1','InstallerSafety.ps1','Repair-Integration.ps1','ShortcutIntegration.ps1','ShortcutFile.cs')) {
        Copy-Item -LiteralPath (Join-Path $repository $name) -Destination $fixture
    }
    $testSource = if ($StorageSafety) { 'StorageSafetyChecks.cs' } elseif ($Storage) { 'StorageRaceChecks.cs' } else { 'AclMigrationRaceChecks.cs' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $testSource) -Destination $checks
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'SecurityFixture.cs') -Destination $checks
    $path = Join-Path $launcher 'PrivateFiles.cs'
    $source = Get-Content -LiteralPath $path -Raw
    function Replace-Once([string]$Old, [string]$New) {
        if ([regex]::Matches($script:source, [regex]::Escape($Old)).Count -ne 1) { throw "Race instrumentation anchor changed: $Old" }
        $script:source = $script:source.Replace($Old, $New)
    }
    # Hooks exist only in this temporary copy; the shipped launcher has no test callbacks.
    if ($StorageSafety) {
        if ($Storage -or $PinsOnly -or $Mutation -notin @('None','Policy','PrivateReplacement')) { throw 'Invalid storage policy mutation options.' }
        'StorageSafetyChecks.Run((ok, message) => { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); });' |
            Set-Content -LiteralPath (Join-Path $checks 'Program.cs')
        if ($Mutation -eq 'Policy') {
            $path = Join-Path $launcher 'StorageSafety.cs'; $source = Get-Content -LiteralPath $path -Raw
            Replace-Once 'if ((effective & mutation) != 0)' 'if (false)'
        } elseif ($Mutation -eq 'PrivateReplacement') {
            $path = Join-Path $launcher 'NativeStores.cs'; $source = Get-Content -LiteralPath $path -Raw
            Replace-Once 'AtomicWrite(target, ReadFile(source), privateFile: true)' 'AtomicWrite(target, ReadFile(source), privateFile: false)'
        }
    } elseif ($Storage) {
        if ($Mutation -ne 'None') { throw 'Storage races do not accept ACL mutations.' }
        Replace-Once 'SetupPayload.CreateDirectory(path, acl);' 'StorageRaceProbe.Checkpoint?.Invoke("create", path); SetupPayload.CreateDirectory(path, acl);'
        [IO.File]::WriteAllText($path, $source)
        $path = Join-Path $launcher 'NativeStores.cs'; $source = Get-Content -LiteralPath $path -Raw
        Replace-Once 'file.Write(bytes); file.Flush(true);' 'StorageRaceProbe.Checkpoint?.Invoke("write", path); file.Write(bytes); file.Flush(true);'
        Replace-Once 'using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();' 'StorageRaceProbe.Checkpoint?.Invoke("read", path); using var buffer = new MemoryStream(); source.CopyTo(buffer); return buffer.ToArray();'
        'namespace HytaleAccounts; internal static class StorageRaceProbe { internal static Action<string, string>? Checkpoint; }' |
            Set-Content -LiteralPath (Join-Path $launcher 'StorageRaceProbe.cs')
    } else {
    if ($Mutation -in @('Policy','PrivateReplacement')) { throw 'Storage policy mutations require -StorageSafety.' }
    Replace-Once 'private static readonly SecurityIdentifier[] Owners =' 'internal static Action<string, string>? MigrationCheckpoint; private static readonly SecurityIdentifier[] Owners ='
    Replace-Once 'entries.Add(path);' 'MigrationCheckpoint?.Invoke("before-pin", path); entries.Add(path);'
    Replace-Once 'Inspect(root);' 'Inspect(root); MigrationCheckpoint?.Invoke("inspected", root);'
    Replace-Once 'SetDirectoryPermissions(path, acl);' 'MigrationCheckpoint?.Invoke("directory-acl", path); SetDirectoryPermissions(path, acl);'
    if ($Mutation -eq 'Membership') {
        if ([regex]::Matches($source, 'RequireUnchangedTree\(\);').Count -ne 2) { throw 'Membership mutation anchors changed.' }
        $source = $source.Replace('RequireUnchangedTree();', '// Membership check deliberately disabled by regression mutation.')
    }
    if ($Mutation -eq 'Propagation') {
        $source = $source.Replace('SetDirectoryPermissions(path, acl);', 'directory.SetAccessControl(acl);')
    }
    if ($Mutation -eq 'Pins') {
        Replace-Once 'Inspect(root); MigrationCheckpoint' 'Inspect(root); foreach (var lease in leases) lease.Dispose(); leases.Clear(); MigrationCheckpoint'
    }
    }
    [IO.File]::WriteAllText($path, $source)
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>Checks</AssemblyName></PropertyGroup>
  <ItemGroup><ProjectReference Include="../Launcher/Launcher.csproj" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $checks 'Checks.csproj')
    # No package sources or downloads: both projects use only installed SDK/framework assemblies.
    '<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath (Join-Path $fixture 'NuGet.Config')
    dotnet build (Join-Path $checks 'Checks.csproj') -c Release --nologo -p:NuGetAudit=false "-p:RestorePackagesPath=$fixture/packages"
    if ($LASTEXITCODE -ne 0) { throw 'ACL race fixture build failed.' }
    if (!$BuildOnly) {
        $arguments = @((Join-Path $checks 'bin/Release/net10.0-windows/Checks.dll'))
        if ($PinsOnly) { $arguments += '--pins-only' }
        dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "ACL race checks failed (mutation: $Mutation)." }
        if ($Mutation -ne 'None') { throw "Mutation survived: $Mutation" }
    }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('acl-race-build-')) { throw 'Cleanup escaped build fixture.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
