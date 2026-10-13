$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot) 'Write-RuntimeNotices.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('runtime-notice-check-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
try {
    $pack = @{NuGetPackageId='synthetic.runtime';NuGetPackageVersion='1.2.3';PackageDirectory=$root}
    $output = Join-Path $root 'combined.txt'
    'Synthetic license' | Set-Content -LiteralPath (Join-Path $root 'LICENSE')
    'Synthetic third-party terms' | Set-Content -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.TXT')
    Write-RuntimeNotices @($pack) $output
    $body = [IO.File]::ReadAllText($output)
    if (!$body.Contains('synthetic.runtime 1.2.3') -or !$body.Contains('Synthetic license') -or !$body.Contains('Synthetic third-party terms') -or $body.Contains($root)) { throw 'Runtime notices lost identity/content or exposed a local path.' }
    Write-Output 'PASS runtime notices retain exact component identity and license/notice text without local paths'
    Remove-Item -LiteralPath (Join-Path $root 'LICENSE')
    $rejected = $false
    try { Write-RuntimeNotices @($pack) $output } catch { $rejected = $true }
    if (!$rejected) { throw 'Missing runtime license was accepted.' }
    $rejected = $false
    try { Write-RuntimeNotices @() $output } catch { $rejected = $true }
    if (!$rejected) { throw 'Empty runtime pack set was accepted.' }
    Write-Output 'PASS missing runtime license or pack set stops packaging'
} finally {
    foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.TXT','combined.txt')) {
        $path = Join-Path $root $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    [IO.Directory]::Delete($root)
}
