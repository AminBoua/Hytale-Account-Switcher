param([ValidateSet('None','CacheEnvironment','OrdinaryExtraction')][string]$Mutation = 'None')
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
if ([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run runtime fixtures unelevated.' }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$root = Join-Path $env:LOCALAPPDATA ('runtime-extraction-check-' + [Guid]::NewGuid().ToString('N'))
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($sid); $acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
[IO.DirectoryInfo]::new($root).Create($acl)
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw "FAIL $Message" }; Write-Output "PASS $Message" }
function Reject([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Check $failed $Message
}
$oldExtract = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
try {
    # Only the disposable copy substitutes this fixture user for Administrators; no production bypass switch.
    $source = [IO.File]::ReadAllText((Join-Path $repository 'Launcher/ElevatedRuntime.ps1'))
    if ($Mutation -eq 'CacheEnvironment') {
        $old = "`$start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = `$Path"
        if ([regex]::Matches($source, [regex]::Escape($old)).Count -ne 1) { throw 'Runtime environment mutation boundary changed.' }
        $source = $source.Replace($old, '# Deliberately inherit the poisoned cache in this disposable copy.')
    }
    . ([scriptblock]::Create($source.Replace("'S-1-5-32-544'", "'$($sid.Value)'").Replace("`$trusted = @(", "`$trusted = @('S-1-5-32-544', ")))
    $stage = Join-Path $root 'protected-runtime'
    New-RuntimeDirectory $stage
    $actual = [IO.DirectoryInfo]::new($stage).GetAccessControl()
    Check ($actual.AreAccessRulesProtected -and $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq $sid.Value -and
        @($actual.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
            $_.IdentityReference.Value -notin @($sid.Value, 'S-1-5-18') -and !($_.IdentityReference.Value -eq 'S-1-3-4' -and
                $_.FileSystemRights -eq [Security.AccessControl.FileSystemRights]'ReadPermissions, Synchronize')
        }).Count -eq 0) 'runtime directory starts protected with only designated trusted mutation principals'
    [IO.File]::WriteAllText((Join-Path $stage 'victim.txt'), 'preserve')
    Reject { New-RuntimeDirectory $stage } 'pre-existing cache refused without adoption'
    Check ([IO.File]::ReadAllText((Join-Path $stage 'victim.txt')) -ceq 'preserve') 'pre-existing cache bytes preserved'
    [IO.Directory]::Delete($stage, $true)

    $unsafe = Join-Path $root 'unsafe-parent'
    [IO.DirectoryInfo]::new($unsafe).Create($acl)
    foreach ($rights in @('WriteData','AppendData','Delete','DeleteSubdirectoriesAndFiles','ChangePermissions','TakeOwnership','WriteAttributes')) {
        $broad = [Security.AccessControl.DirectorySecurity]::new()
        $broad.SetSecurityDescriptorBinaryForm($acl.GetSecurityDescriptorBinaryForm(), [Security.AccessControl.AccessControlSections]'Owner, Access')
        $broad.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), $rights, 'Allow'))
        [IO.DirectoryInfo]::new($unsafe).SetAccessControl($broad)
        Reject { New-RuntimeDirectory (Join-Path $unsafe 'cache') } "lower-privilege $rights grant refused before worker start"
        Check (![IO.Directory]::Exists((Join-Path $unsafe 'cache'))) 'unsafe parent is not changed'
    }
    [IO.DirectoryInfo]::new($unsafe).SetAccessControl($acl)
    $link = Join-Path $root 'junction'
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path ([Environment]::SystemDirectory) 'cmd.exe'), "/c mklink /J `"$link`" `"$unsafe`"")
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $child = [Diagnostics.Process]::Start($start)
    try { $child.WaitForExit(); Check ($child.ExitCode -eq 0) 'synthetic junction created' } finally { $child.Dispose() }
    Reject { New-RuntimeDirectory (Join-Path $link 'cache') } 'linked runtime ancestor refused'
    [IO.Directory]::Delete($link)

    $project = Join-Path $root 'probe'
    [IO.Directory]::CreateDirectory($project) | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RuntimeExtraction/RuntimeExtraction.csproj'),(Join-Path $PSScriptRoot 'RuntimeExtraction/Program.cs') -Destination $project
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'EffectiveAccessChecks.cs') -Destination $root
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'VmValidation/NuGet.Config') -Destination (Join-Path $root 'NuGet.Config')
    dotnet publish (Join-Path $project 'RuntimeExtraction.csproj') -c Release -o (Join-Path $root 'publish') --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw 'Synthetic apphost publish failed.' }
    $poison = Join-Path $root 'untrusted-cache'
    [IO.Directory]::CreateDirectory($poison) | Out-Null
    [IO.File]::WriteAllText((Join-Path $poison 'victim.txt'), 'preserve poisoned cache')
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $poison
    $before = @{}; foreach ($name in @('APPDATA','LOCALAPPDATA','USERPROFILE','TEMP','TMP')) { $before[$name] = [Environment]::GetEnvironmentVariable($name) }
    $report = Join-Path $root 'probe.json'
    $arguments = @("quote's & `$literal", 'trailing\', 'space and "quote"', '')
    function Run-Probe([string]$Executable, [string]$Cache, [string]$Report) {
        $start = [Diagnostics.ProcessStartInfo]::new($Executable)
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $Cache
        # Reuse production's native argument quoting, including an empty argument.
        $quoted = foreach ($argument in (@($Report) + $arguments)) {
            '"' + [regex]::Replace([regex]::Replace($argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
        }
        $start.Arguments = $quoted -join ' '
        $child = [Diagnostics.Process]::Start($start)
        try { $child.WaitForExit(); return $child.ExitCode } finally { $child.Dispose() }
    }
    $unsafeCache = Join-Path $root 'unsafe-ordinary-cache'
    [IO.DirectoryInfo]::new($unsafeCache).Create($acl)
    $broad = [IO.DirectoryInfo]::new($unsafeCache).GetAccessControl()
    $broad.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'), 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    [IO.DirectoryInfo]::new($unsafeCache).SetAccessControl($broad)
    Check ((Run-Probe (Join-Path $root 'publish/RuntimeExtraction.exe') $unsafeCache $report) -eq 23) 'unsafe ordinary control runs only disposable probe'
    $unsafeProbe = [IO.File]::ReadAllText($report) | ConvertFrom-Json
    Check ($unsafeProbe.extractedBeforeApplication.Count -gt 0) 'unsafe ordinary control extracts before managed entry'
    foreach ($file in $unsafeProbe.extractedBeforeApplication) {
        $allows = [IO.FileInfo]::new($file).GetAccessControl().GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
        Check (@($allows | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-32-545' -and $_.IsInherited -and ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify) -eq [Security.AccessControl.FileSystemRights]::Modify }).Count -gt 0) 'disposable extracted library inherits foreign Modify grant (ACL observation, not a second user)'
    }
    $ordinary = Join-Path $root 'ordinary'
    $extractNative = if ($Mutation -eq 'OrdinaryExtraction') { 'true' } else { 'false' }
    dotnet publish (Join-Path $project 'RuntimeExtraction.csproj') -c Release -o $ordinary --nologo -p:NuGetAudit=false "-p:IncludeNativeLibrariesForSelfExtract=$extractNative"
    if ($LASTEXITCODE) { throw 'Ordinary no-extraction probe publish failed.' }
    $cacheSnapshot = @(Get-ChildItem -LiteralPath $unsafeCache -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join "`n"
    foreach ($name in @('HytaleAccounts-Setup.exe','HytaleAccounts.exe')) {
        Copy-Item -LiteralPath (Join-Path $ordinary 'RuntimeExtraction.exe') -Destination (Join-Path $ordinary $name)
        Check ((Run-Probe (Join-Path $ordinary $name) $unsafeCache $report) -eq 23) "ordinary $name relays nonzero exit with pre-existing foreign-writable cache"
        $probe = [IO.File]::ReadAllText($report) | ConvertFrom-Json
        Check ($probe.wpfModules.Count -gt 0 -and @($probe.wpfModules | Where-Object { (Split-Path $_) -cne $ordinary }).Count -eq 0) 'ordinary WPF loads published sidecars instead of cached native libraries'
        Check (($probe.arguments | ConvertTo-Json -Compress) -ceq ($arguments | ConvertTo-Json -Compress)) 'ordinary apphost forwards literal arguments'
        foreach ($key in $before.Keys) { Check ($probe.environment.$key -ceq $before[$key]) "ordinary startup retains normal $key" }
        Check ($probe.environment.DOTNET_BUNDLE_EXTRACT_BASE_DIR -ceq $unsafeCache) 'ordinary startup requires no cache environment override'
    }
    $absentCache = Join-Path $root 'never-created-cache'
    Check ((Run-Probe (Join-Path $ordinary 'HytaleAccounts.exe') $absentCache $report) -eq 23 -and ![IO.Directory]::Exists($absentCache)) 'ordinary helper creates no extraction cache on a fresh start'
    $missingNative = Join-Path $root 'missing-native'
    [IO.Directory]::CreateDirectory($missingNative) | Out-Null
    Get-ChildItem -LiteralPath $ordinary -File | Where-Object Name -ne 'PresentationNative_cor3.dll' | Copy-Item -Destination $missingNative
    Check ((Run-Probe (Join-Path $missingNative 'HytaleAccounts-Setup.exe') $absentCache $report) -ne 23 -and ![IO.Directory]::Exists($absentCache)) 'missing native sidecar fails startup without extracting a fallback cache'
    Check ((@(Get-ChildItem -LiteralPath $unsafeCache -Recurse -File | ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join "`n") -ceq $cacheSnapshot) 'ordinary starts neither use nor alter pre-existing extracted library bytes'
    Reject { Run-Probe (Join-Path $ordinary 'missing.exe') $unsafeCache $report } 'ordinary startup failure reported without a cache or wrapper to clean'
    Check ($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ceq $poison) 'ordinary and elevated fixture environments remain separate'
    $code = Invoke-ProtectedRuntime $stage (Join-Path $root 'publish/RuntimeExtraction.exe') (@($report) + $arguments)
    Check ($code -eq 23) 'worker exit status relayed'
    $probe = [IO.File]::ReadAllText($report) | ConvertFrom-Json
    Check ($probe.environment.DOTNET_BUNDLE_EXTRACT_BASE_DIR -ceq $stage -and $probe.modules.Count -ge 1 -and
        $probe.extractedBeforeApplication.Count -ge 1 -and
        @($probe.modules | Where-Object { $_ -notin $probe.extractedBeforeApplication }).Count -eq 0) 'apphost extracts native libraries before application startup and WPF loads those protected files'
    Check ($probe.ownerChecks.Count -eq 3 * $probe.extractedBeforeApplication.Count) 'native DLLs inherit OWNER RIGHTS; simulated ordinary owners cannot change permissions and the positive control detects its removal'
    foreach ($name in $before.Keys) { Check ($probe.environment.$name -ceq $before[$name]) "worker retains normal $name" }
    Check (($probe.arguments | ConvertTo-Json -Compress) -ceq ($arguments | ConvertTo-Json -Compress)) 'native argument quoting preserves literal paths and trailing backslashes'
    Check ($env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -ceq $poison) 'parent extraction environment unchanged'
    Check (@([IO.Directory]::EnumerateFileSystemEntries($poison)).Count -eq 1 -and
        [IO.File]::ReadAllText((Join-Path $poison 'victim.txt')) -ceq 'preserve poisoned cache') 'poisoned inherited cache never used or modified'
    Check (![IO.Directory]::Exists($stage)) 'runtime cache cleaned after nonzero worker exit'
    Reject { Invoke-ProtectedRuntime $stage (Join-Path $root 'missing.exe') @() } 'worker startup failure reported'
    Check (![IO.Directory]::Exists($stage)) 'runtime cache cleaned after worker startup failure'
    if ($Mutation -ne 'None') { throw "Mutation survived: $Mutation" }
} finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $oldExtract
    $resolved = [IO.Path]::GetFullPath($root)
    $parent = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\'
    if (!$resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase) -or !(Split-Path $resolved -Leaf).StartsWith('runtime-extraction-check-')) { throw 'Cleanup escaped runtime fixture.' }
    if ([IO.Directory]::Exists($resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
