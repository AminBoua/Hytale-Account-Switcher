# Runs in the protected Windows PowerShell before any bundled code starts.
$ErrorActionPreference = 'Stop'
$env:PSModulePath = [IO.Path]::Combine([Environment]::SystemDirectory, 'WindowsPowerShell\v1.0\Modules')

function Assert-MachineDirectory([IO.DirectoryInfo]$Directory, [bool]$Ancestor) {
    $Directory.Refresh()
    if (!$Directory.Exists -or ($Directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe setup directory.' }
    $acl = [Security.AccessControl.RawSecurityDescriptor]::new($Directory.GetAccessControl().GetSecurityDescriptorBinaryForm(), 0)
    $trusted = @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    if ($acl.Owner.Value -notin $trusted -or $null -eq $acl.DiscretionaryAcl) { throw 'Untrusted setup directory permissions.' }
    $mask = 0x500d0150L
    if (!$Ancestor) { $mask = $mask -bor 6 }
    foreach ($ace in $acl.DiscretionaryAcl) {
        if ([int]$ace.AceFlags -band [int][Security.AccessControl.AceFlags]::InheritOnly) { continue }
        if ($ace -isnot [Security.AccessControl.CommonAce] -or $ace.IsCallback -or $ace.AceQualifier -notin @('AccessAllowed', 'AccessDenied')) { throw 'Unsupported setup directory permissions.' }
        $sid = $ace.SecurityIdentifier.Value
        if ($sid -in $trusted -or $sid -eq 'S-1-3-4' -or $sid.StartsWith('S-1-15-2-') -or $sid.StartsWith('S-1-15-3-')) { continue }
        if ($ace.AceQualifier -eq 'AccessAllowed' -and ([long]$ace.AccessMask -band $mask)) { throw 'A lower-privilege account can modify the setup directory.' }
    }
}

function Copy-CheckedFile([string]$Source, [string]$Destination, [string]$Digest) {
    $attributes = [IO.File]::GetAttributes($Source)
    if ($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory)) { throw 'Unexpected setup file type.' }
    $inputFile = [IO.File]::Open($Source, 'Open', 'Read', 'Read')
    try {
        $outputFile = [IO.File]::Open($Destination, 'CreateNew', 'ReadWrite', 'None')
        try {
            $inputFile.CopyTo($outputFile)
            $outputFile.Flush($true)
            $outputFile.Position = 0
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($sha.ComputeHash($outputFile)).Replace('-', '').ToLowerInvariant() } finally { $sha.Dispose() }
            if ($actual -cne $Digest) { throw 'The setup package changed after verification.' }
        } finally { $outputFile.Dispose() }
    } finally { $inputFile.Dispose() }
    $file = [IO.FileInfo]::new($Destination)
    $acl = $file.GetAccessControl()
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $file.SetAccessControl($acl)
}

function Invoke-AddonWorker([string]$Source, [string]$Executable, [string]$ManifestHash, [string]$Mode, [string]$Sid) {
    $stage = $null
    $copied = @()
    # Setup.cs maps exit codes 125 through 129 to bootstrap phases.
    $result = 125
    try {
        if ($Mode -cnotin @('--install-elevated', '--uninstall-elevated') -or $Executable -cnotin @('HytaleAccounts.exe', 'HytaleAccounts-Setup.exe') -or
            $ManifestHash -cnotmatch '^[a-f0-9]{64}$' -or ![Security.Principal.SecurityIdentifier]::new($Sid).IsAccountSid()) { throw 'Invalid addon worker parameters.' }
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if ($identity.User.Value -cne $Sid -or ![Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator elevation from the same Windows account is required.' }
        $result = 126
        if ($Source -notmatch '^[a-z]:\\' -or [IO.Path]::GetFullPath($Source) -ne $Source) { throw 'Invalid setup source path.' }
        for ($item = [IO.DirectoryInfo]::new($Source); $null -ne $item; $item = $item.Parent) {
            if (!$item.Exists -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe setup source path.' }
        }
        $result = 127
        $parent = [IO.DirectoryInfo]::new([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles))
        for ($item = $parent; $null -ne $item; $item = $item.Parent) { Assert-MachineDirectory $item ($item.FullName -ne $parent.FullName) }
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        $acl.SetAccessRuleProtection($true, $false)
        foreach ($rule in @(@('S-1-5-32-544','FullControl'), @('S-1-5-18','FullControl'), @('S-1-5-32-545','ReadAndExecute'), @('S-1-3-4','ReadPermissions'))) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($rule[0]),
                [Security.AccessControl.FileSystemRights]$rule[1], [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
                [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
        }
        $newStage = [IO.DirectoryInfo]::new([IO.Path]::Combine($parent.FullName, 'HytaleAccounts-stage-' + [Guid]::NewGuid().ToString('N')))
        if ($newStage.Exists -or [IO.File]::Exists($newStage.FullName)) { throw 'The setup stage already exists.' }
        $newStage.Create($acl)
        $stage = $newStage
        Assert-MachineDirectory $stage $false
        $result = 128
        $manifestName = 'addon-payload.json'
        $manifest = [IO.Path]::Combine($stage.FullName, $manifestName)
        $copied += $manifestName
        Copy-CheckedFile ([IO.Path]::Combine($Source, $manifestName)) $manifest $ManifestHash
        $payload = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($manifest))
        $required = @('HytaleAccounts.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')
        if (@($payload.Files).Count -gt 100) { throw 'Unexpected package file count.' }
        foreach ($name in $required) {
            if (@($payload.Files | Where-Object { $_.Name -ceq $name }).Count -ne 1) { throw 'Missing required setup file.' }
        }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $payload.Files) {
            $name = $file.Name
            $sidecar = $name -match '^[a-z0-9][a-z0-9_.-]{0,95}\.dll$' -and $name -notmatch '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\.' -and !$name.Contains('..')
            if (($name -cnotin $required -and !$sidecar) -or $file.Sha256 -cnotmatch '^[a-f0-9]{64}$' -or !$seen.Add($name)) { throw 'Unexpected setup package manifest.' }
            $sourceName = if ($name -ceq 'HytaleAccounts.exe') { $Executable } else { $name }
            $copied += $name
            Copy-CheckedFile ([IO.Path]::Combine($Source, $sourceName)) ([IO.Path]::Combine($stage.FullName, $name)) $file.Sha256
        }
        $result = 129
        $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::Combine($stage.FullName, 'HytaleAccounts.exe'))
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = 'Hidden'
        $start.WorkingDirectory = [Environment]::SystemDirectory
        foreach ($name in @($start.EnvironmentVariables.Keys)) {
            if ($name -match '^(DOTNET_|COMPlus_|CORECLR_|COR_)') { $start.EnvironmentVariables.Remove($name) }
        }
        $start.EnvironmentVariables['PATH'] = [Environment]::SystemDirectory
        $start.Arguments = $Mode + ' ' + $Sid
        $worker = [Diagnostics.Process]::Start($start)
        if (!$worker) { throw 'Could not start the protected addon worker.' }
        try { $worker.WaitForExit(); $result = $worker.ExitCode } finally { $worker.Dispose() }
    } catch {
        if ($Mode -eq '--uninstall-elevated') {
            [void][Reflection.Assembly]::LoadWithPartialName('PresentationFramework')
            [void][Windows.MessageBox]::Show("Could not prepare the protected removal worker (setup code $result). Try removal again from Windows Settings.", 'Hytale Accounts')
        }
    } finally {
        if ($stage) {
            foreach ($name in $copied) { try { [IO.File]::Delete([IO.Path]::Combine($stage.FullName, $name)) } catch { } }
            try { $stage.Delete($false) } catch { }
        }
    }
    exit $result
}
