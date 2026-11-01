# Schema contract shared by the release gate and guest exporter; no privileged actions.
function Test-RuntimeEvidence($Run, $Summary) {
    function Runtime-Time($Value) {
        Require ($Value -is [string] -and $Value.EndsWith('Z')) 'Missing UTC runtime timestamp.'
        [DateTime]::Parse($Value).ToUniversalTime()
    }
    function Runtime-Identity($Identity, $Sid, $Type, $Admin) {
        Require ($Identity.Sid -eq $Sid -and $Identity.ElevationType -eq $Type -and $Identity.Administrator -eq $Admin -and $Identity.Pid -gt 0) 'Wrong runtime process/token identity.'
    }
    function Runtime-Acl($Record, $Sid, $Elevated, $Directory) {
        Require ($Record.Path -and $Record.Sddl) 'Missing runtime path/SDDL.'
        $acl = [Security.AccessControl.RawSecurityDescriptor]::new($Record.Sddl)
        $trusted = @('S-1-5-18','S-1-5-32-544')
        if (!$Elevated) { $trusted += $Sid }
        Require ($acl.Owner.Value -in ($trusted + $Sid) -and $null -ne $acl.DiscretionaryAcl) 'Untrusted runtime owner or missing DACL.'
        if ($Directory) { Require (($acl.ControlFlags -band 0x1000) -ne 0 -and (!$Elevated -or $acl.Owner.Value -eq 'S-1-5-32-544')) 'Runtime root must have a protected DACL and expected owner.' }
        foreach ($principal in $trusted) {
            Require (@($acl.DiscretionaryAcl | Where-Object {
                $_ -is [Security.AccessControl.CommonAce] -and !$_.IsCallback -and $_.AceQualifier -eq 'AccessAllowed' -and
                $_.SecurityIdentifier.Value -eq $principal -and ([int]$_.AceFlags -band [int][Security.AccessControl.AceFlags]::InheritOnly) -eq 0 -and
                ($_.AccessMask -band 0x1f01ff) -eq 0x1f01ff
            }).Count -gt 0) 'Runtime lacks required trusted full access.'
        }
        $ownerRights = $false
        foreach ($ace in $acl.DiscretionaryAcl) {
            Require ($ace -is [Security.AccessControl.CommonAce] -and !$ace.IsCallback) 'Unsupported runtime ACE.'
            if ($ace.SecurityIdentifier.Value -eq 'S-1-3-4' -and $ace.AceQualifier -eq 'AccessAllowed') {
                Require (($ace.AccessMask -band 0x500d0156) -eq 0) 'OWNER RIGHTS permits runtime mutation.'
                $ownerRights = $true
            }
            if ($ace.AceQualifier -eq 'AccessAllowed' -and $ace.SecurityIdentifier.Value -notin $trusted) {
                Require (($ace.AccessMask -band 0x500d0156) -eq 0) 'Runtime permits lower-privilege mutation.'
            }
        }
        if ($Elevated -and $acl.Owner.Value -eq $Sid) { Require $ownerRights 'Elevated user-owned cache lacks OWNER RIGHTS suppression.' }
    }
    foreach ($mode in @('ordinary','same-user','alternate-admin')) {
        $elevated = $mode -ne 'ordinary'
        $initiator = if ($mode -eq 'same-user') { $Run.AdminSid } else { $Run.UserSid }
        $worker = if ($elevated) { $Run.AdminSid } else { $Run.UserSid }
        $readyName = "channels\$worker\runtime-ready-$mode.json"
        $ready = Read-Evidence $readyName
        $done = Read-Evidence "channels\$initiator\runtime-$mode.json"
        Require ($ready.Contract -eq 'runtime-bootstrap-v1' -and $done.Contract -eq $ready.Contract -and $ready.Mode -eq $mode -and $done.Mode -eq $mode -and $ready.RunId -eq $Run.Id -and $done.RunId -eq $Run.Id -and $done.Success -eq $true) 'Missing successful matching runtime contract row.'
        Require ($ready.SourceFingerprint -eq $Run.Source.Fingerprint -and $done.SourceFingerprint -eq $Run.Source.Fingerprint) 'Stale runtime source coverage.'
        Runtime-Identity $ready.Identity $worker $(if ($elevated) {2} else {1}) $elevated
        Runtime-Identity $done.Identity $initiator $(if ($mode -eq 'same-user') {3} else {1}) $false
        $start = Runtime-Time $done.StartedUtc; $end = Runtime-Time $done.CompletedUtc
        $observed = Runtime-Time $ready.Utc
        Require ($start -le $observed -and $observed -le $end -and $end -le (Runtime-Time $Summary.Utc) -and $start -ge (Runtime-Time $Run.StartedUtc) -and $end -le (Runtime-Time $Run.ExpiresUtc)) 'Runtime evidence is outside this run/lifetime.'
        Require ($done.ReadyHash -eq ($Summary.Evidence | Where-Object Name -eq $readyName.Replace('\','_')).Hash -and $done.Identity.Pid -ne $ready.Identity.Pid -and $done.WorkerPid -eq $ready.Identity.Pid -and $done.ExitCode -eq 23 -and $done.CacheRemoved -eq $true -and $done.StartFailureCacheRemoved -eq $true -and $done.PoisonPreserved -eq $true -and $done.ProfilePreserved -eq $true) 'Incomplete runtime lifetime/cleanup/environment outcomes.'
        Require ($ready.Cache.Path -match '^[A-Za-z]:\\' -and @($ready.Files).Count -gt 0 -and @($ready.Modules).Count -gt 0 -and @($ready.BeforeApplication).Count -gt 0) 'Missing native extraction/module inventory.'
        Runtime-Acl $ready.Cache $worker $elevated $true
        $prefix = $ready.Cache.Path.TrimEnd('\') + '\'
        Require (@($ready.Files.Path | Select-Object -Unique).Count -eq @($ready.Files).Count) 'Duplicate native file records.'
        Require (@($ready.Modules | Where-Object { [IO.Path]::GetFileName($_) -ieq 'coreclr.dll' }).Count -eq 1) 'Missing actual loaded CoreCLR path.'
        foreach ($directory in $ready.Directories) {
            $parent = [IO.Path]::GetDirectoryName($directory.Path)
            Require ($parent -eq $ready.Cache.Path -or $parent -in @($ready.Directories | ForEach-Object Path)) 'Missing runtime directory ancestor ACL.'
            Require ($directory.Path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFullPath($directory.Path) -eq $directory.Path) 'Runtime descendant outside cache.'
            Runtime-Acl $directory $worker $elevated $false
        }
        Require ($null -ne $ready.Directories -and @($ready.BeforeApplication).Count -eq @($ready.Files).Count -and @($ready.BeforeApplication | Select-Object -Unique).Count -eq @($ready.Files).Count) 'Incomplete pre-application native inventory.'
        foreach ($file in $ready.Files) {
            Require ($file.Path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFullPath($file.Path) -eq $file.Path -and $file.Hash -match '^[A-F0-9]{64}$') 'Native file outside cache or missing hash.'
            $parent = [IO.Path]::GetDirectoryName($file.Path)
            Require ($parent -eq $ready.Cache.Path -or $parent -in @($ready.Directories | ForEach-Object Path)) 'Missing native ancestor ACL.'
            Runtime-Acl $file $worker $elevated $false
        }
        foreach ($path in @($ready.Modules) + @($ready.BeforeApplication)) { Require ($path -in $ready.Files.Path) 'Loaded/pre-application native file missing from ACL/hash inventory.' }
        foreach ($actor in @('unrelated') + @($(if ($elevated) {'owner'}))) {
            if (!$actor) { continue }
            $sid = if ($actor -eq 'owner') { $Run.AdminSid } else { $Run.AttackerSid }
            $name = if ($actor -eq 'owner') { 'runtime-owner-attack' } else { 'runtime-attack' }
            $attack = Read-Evidence "channels\$sid\$name-$mode.json"
            Runtime-Identity $attack.Identity $sid $(if ($actor -eq 'owner') {3} else {1}) $false
            Require ($attack.Success -eq $true -and $attack.RunId -eq $Run.Id -and $attack.Mode -eq $mode -and $attack.Identity.Pid -ne $ready.Identity.Pid -and $attack.WorkerPid -eq $ready.Identity.Pid -and $attack.ReadyHash -eq $done.ReadyHash -and $attack.PositiveControl -eq $true -and (Runtime-Time $attack.Utc) -ge $observed -and (Runtime-Time $attack.Utc) -le $end) 'Missing independent runtime actor/lifetime/positive control.'
            foreach ($operation in @('write','replace','delete-leaf','rename-directory','insert-file','delete-directory','file-write-dac','directory-write-dac')) {
                $row = @($attack.Operations | Where-Object Operation -eq $operation)
                Require ($row.Count -eq 1 -and $row[0].Denied -eq $true -and $row[0].WindowsError -in @(5,32,33)) "Missing denied runtime operation: $operation"
            }
        }
    }
}
