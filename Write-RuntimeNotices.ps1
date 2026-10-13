function Write-RuntimeNotices($Packs, [string]$Destination) {
    $ErrorActionPreference = 'Stop'
    if (!@($Packs).Count) { throw 'No resolved runtime packs; refusing to package without notices.' }
    $sections = @('Third-party components bundled with Hytale Accounts',
        'These licenses apply to their respective components, separately from the addon source license.')
    foreach ($pack in $Packs | Sort-Object NuGetPackageId) {
        if (!$pack.NuGetPackageId -or !$pack.NuGetPackageVersion -or !$pack.PackageDirectory) { throw 'Incomplete runtime pack identity.' }
        $files = @(Get-ChildItem -LiteralPath $pack.PackageDirectory -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\..*)?$' } | Sort-Object Name)
        if (!@($files | Where-Object Name -Match '^LICENSE(\..*)?$').Count) { throw "Runtime license missing: $($pack.NuGetPackageId)" }
        foreach ($file in $files) {
            $body = [IO.File]::ReadAllText($file.FullName)
            if ([string]::IsNullOrWhiteSpace($body)) { throw "Empty runtime notice: $($file.FullName)" }
            $sections += "=== $($pack.NuGetPackageId) $($pack.NuGetPackageVersion) / $($file.Name) ===`r`n$body"
        }
    }
    [IO.File]::WriteAllText($Destination, ($sections -join "`r`n`r`n"), [Text.UTF8Encoding]::new($false))
}
