param([string]$ValidateRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-VmIdentity($Computer, [string]$Uuid, [string]$ExpectedUuid, [string]$HostUuid, [bool]$NetworkUp) {
    if ($Computer.PartOfDomain -or ($Computer.Manufacturer + ' ' + $Computer.Model) -notmatch 'Virtual Machine|VMware|VirtualBox|KVM|QEMU|Xen|Parallels') { throw 'Refusing non-disposable/physical or domain-joined machine.' }
    if (!$Uuid -or $Uuid -eq [Guid]::Empty.ToString() -or $Uuid -ne $ExpectedUuid -or $Uuid -eq $HostUuid) { throw 'VM UUID does not match explicit designation, or matches the build host.' }
    if ($NetworkUp) { throw 'Disconnect all guest network adapters before validation.' }
}
function Assert-Designation($Run, [string]$Id, [string]$ComputerName, [DateTime]$Now) {
    if ($Run.Id -ne $Id -or $Run.ComputerName -ne $ComputerName -or $Run.Consent -ne 'DISPOSABLE VM ONLY; SNAPSHOT MAY BE DESTROYED' -or $Now.ToUniversalTime() -gt [DateTime]::Parse($Run.ExpiresUtc).ToUniversalTime()) { throw 'Missing, expired or mismatched VM designation.' }
}
function Assert-PlainPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.StartsWith('\\') -or $full.IndexOf(':', 2) -ge 0) { throw 'Only local paths are allowed.' }
    for ($item = Get-Item -LiteralPath $full -Force; $null -ne $item; $item = $item.Parent) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point refused: $($item.FullName)" }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory; if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked parent refused.' } }
    }
}
function Assert-AdminOwned([string]$Path) {
    Assert-PlainPath $Path
    $acl = Get-Acl -LiteralPath $Path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')) { throw "Guard path has an untrusted owner: $Path" }
    $raw = [Security.AccessControl.RawSecurityDescriptor]::new($acl.Sddl)
    if ($null -eq $raw.DiscretionaryAcl) { throw 'Guard path has a null DACL.' }
    foreach ($ace in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($ace.AccessControlType -eq 'Allow' -and !($ace.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            $ace.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544') -and ([long]$ace.FileSystemRights -band 0x500D0156)) { throw "Guard path permits untrusted mutation: $Path" }
    }
}
function Get-Guard([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full -notmatch '^C:\\Program Files\\ModLauncherSecurityVM-([a-f0-9]{32})$') { throw 'Only the designated fixed VM fixture root is allowed.' }
    $id = $Matches[1]
    Assert-AdminOwned $full
    Assert-AdminOwned (Join-Path $full 'run.json')
    Assert-AdminOwned (Join-Path $full 'bin')
    $run = Get-Content -LiteralPath (Join-Path $full 'run.json') -Raw | ConvertFrom-Json
    $computer = Get-CimInstance Win32_ComputerSystem
    $uuid = (Get-CimInstance Win32_ComputerSystemProduct).UUID
    Assert-VmIdentity $computer $uuid $run.VmUuid $run.HostUuid (@(Get-NetAdapter | Where-Object Status -eq 'Up').Count -gt 0)
    Assert-Designation $run $id $env:COMPUTERNAME ([DateTime]::UtcNow)
    if ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System').EnableLUA -ne 1) { throw 'UAC must be enabled.' }
    foreach ($entry in $run.Files) {
        if ($entry.Name -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid package filename.' }
        $file = Join-Path (Join-Path $full 'bin') $entry.Name
        Assert-AdminOwned $file
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Hash) { throw "Harness/production source changed: $($entry.Name)" }
    }
    return $run
}
if ($ValidateRoot) { $null = Get-Guard $ValidateRoot }
