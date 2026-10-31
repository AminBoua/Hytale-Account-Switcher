# This resource runs in system Windows PowerShell, before the bundled .NET apphost starts.
$ErrorActionPreference = 'Stop'
function Assert-RuntimeDirectory([string]$Path, [switch]$Ancestor) {
    $item = [IO.DirectoryInfo]::new($Path)
    if (!$item.Exists -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe runtime directory.' }
    $acl = [Security.AccessControl.RawSecurityDescriptor]::new($item.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0)
    $trusted = @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if (!$acl.Owner -or $acl.Owner.Value -notin $trusted -or !$acl.DiscretionaryAcl) { throw 'Untrusted runtime directory owner or permissions.' }
    # Ancestors may permit sibling creation, but cannot permit replacement of an existing child.
    $mutation = [long]0x000d0150
    if (!$Ancestor) { $mutation = $mutation -bor 6 }
    foreach ($ace in $acl.DiscretionaryAcl) {
        if (([int]$ace.AceFlags -band [int][Security.AccessControl.AceFlags]::InheritOnly) -ne 0) { continue }
        if ($ace -isnot [Security.AccessControl.CommonAce] -or $ace.IsCallback -or
            $ace.AceQualifier -notin @([Security.AccessControl.AceQualifier]::AccessAllowed, [Security.AccessControl.AceQualifier]::AccessDenied)) {
            throw 'Unsupported runtime directory access rule.'
        }
        $sid = $ace.SecurityIdentifier.Value
        # As in StorageSafety, package/capability grants also require the normal user side of an AppContainer token.
        if ($ace.AceQualifier -ne [Security.AccessControl.AceQualifier]::AccessAllowed -or $sid -in $trusted -or
            $sid -eq 'S-1-3-4' -or $sid.StartsWith('S-1-15-2-') -or $sid.StartsWith('S-1-15-3-')) { continue }
        # Conservatively reject mutation allows, including generic access, even when a deny might mask them.
        $rights = [long]$ace.AccessMask -band 0xffffffffL
        if (($rights -band ($mutation -bor 0x50000000L)) -ne 0) { throw "Runtime directory permits lower-privilege mutation: $Path ($($ace.SecurityIdentifier.Value))." }
    }
}
function New-RuntimeDirectory([string]$Path) {
    $parent = [IO.Directory]::GetParent($Path)
    for ($item = $parent; $item; $item = $item.Parent) { Assert-RuntimeDirectory $item.FullName -Ancestor:($item.FullName -ne $parent.FullName) }
    if ([IO.File]::Exists($Path) -or [IO.Directory]::Exists($Path)) { throw 'Runtime directory already exists; refusing reuse.' }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-32-544', 'S-1-5-18')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    # Descendants may be owned by the elevated user's SID; suppress its unelevated token's implicit WRITE_DAC.
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-3-4'),
        [Security.AccessControl.FileSystemRights]::ReadPermissions,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
        [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    # The validated parent excludes unprivileged creation; attach the protected DACL at creation, never repair/adopt a cache.
    $directory = [IO.DirectoryInfo]::new($Path)
    $directory.Create($acl)
    Assert-RuntimeDirectory $Path
    $actual = $directory.GetAccessControl()
    if (!$actual.AreAccessRulesProtected -or $actual.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner, Access') -cne
        $acl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Owner, Access')) { throw 'Protected runtime permissions were not applied.' }
}
function Invoke-ProtectedRuntime([string]$Path, [string]$Executable, [string[]]$WorkerArguments) {
    New-RuntimeDirectory $Path
    try {
        $start = [Diagnostics.ProcessStartInfo]::new($Executable)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.WorkingDirectory = [Environment]::SystemDirectory
        # Process-local and present before apphost extraction; TEMP and the Windows profile stay unchanged.
        $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $Path
        $quoted = foreach ($argument in $WorkerArguments) {
            '"' + [regex]::Replace([regex]::Replace($argument, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
        }
        $start.Arguments = $quoted -join ' '
        $worker = [Diagnostics.Process]::Start($start)
        if (!$worker) { throw 'Could not start installer worker.' }
        try { $worker.WaitForExit(); return $worker.ExitCode } finally { $worker.Dispose() }
    } finally {
        # A crash/locked file may leave an administrator-only directory; it is never reused.
        try { [IO.Directory]::Delete($Path, $true) } catch { }
    }
}
function Invoke-ElevatedRuntime([string]$Executable, [string]$Programs, [string]$Desktop, [string]$UserSid) {
    try {
        if (![Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Administrator elevation is required.'
        }
        $path = [IO.Path]::Combine([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles), 'HytaleAccounts-runtime-' + [Guid]::NewGuid().ToString('N'))
        exit (Invoke-ProtectedRuntime $path $Executable @('--install-elevated', $Programs, $Desktop, $UserSid))
    } catch { exit 125 }
}
