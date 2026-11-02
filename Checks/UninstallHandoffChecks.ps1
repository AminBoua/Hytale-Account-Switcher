param([switch]$RunElevated, [switch]$LinkedToken)
# Opt-in native UAC check. The only child payload is hidden system cmd.exe /c exit 0.
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('uninstall-handoff-' + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    $source = Get-Content -LiteralPath (Join-Path $repository 'Launcher\UninstallHandoff.cs') -Raw
    # Keep native identity/session checks and launch unchanged; bypass only installed payload validation.
    $begin = $source.IndexOf('        using var directories =')
    $end = $source.IndexOf('            if (!CreateEnvironmentBlock', $begin)
    if ($begin -lt 0 -or $end -le $begin) { throw 'Handoff payload boundary changed.' }
    $source = $source.Substring(0, $begin) + "        try`n        {`n" + $source.Substring($end)
    $boundaries = @{
        'Path.Combine(destination, "HytaleAccounts.exe")' = 'Path.Combine(Environment.SystemDirectory, "cmd.exe")'
        '\" --uninstall' = '\" /c exit 0'
        '0x08000400, environment' = '0x08000404, environment' # Suspend only to inspect the actual child token.
        'CloseHandle(process.Thread); CloseHandle(process.Process);' = 'try { HandoffProbe.Verify(process.Process, process.Thread); } finally { CloseHandle(process.Thread); CloseHandle(process.Process); }'
        'finally { foreach (var file in files) file.Dispose(); }' = 'finally { }'
    }
    if ($LinkedToken) { $boundaries['CreateProcessWithTokenW(launchToken, 1,'] = 'CreateProcessWithTokenW(linked, 1,' }
    foreach ($boundary in $boundaries.Keys) {
        if ([regex]::Matches($source, [regex]::Escape($boundary)).Count -ne 1) { throw "Handoff fixture seam changed: $boundary" }
        $source = $source.Replace($boundary, $boundaries[$boundary])
    }
    [IO.File]::WriteAllText((Join-Path $fixture 'UninstallHandoff.cs'), $source)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UninstallHandoffChecks.cs') -Destination (Join-Path $fixture 'Program.cs')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $fixture 'Probe.csproj')
    dotnet build (Join-Path $fixture 'Probe.csproj') -c Release --nologo --configfile (Join-Path $PSScriptRoot 'VmValidation\NuGet.Config') -p:NuGetAudit=false
    if ($LASTEXITCODE) { throw 'Native handoff fixture build failed.' }
    $result = Join-Path $fixture 'result.txt'
    $options = @{ FilePath=(Join-Path $fixture 'bin\Release\net10.0-windows\Probe.exe'); ArgumentList=('"' + $result + '"'); WindowStyle='Hidden'; PassThru=$true }
    if ($RunElevated) { $options.Verb = 'RunAs' }
    $process = Start-Process @options
    try {
        if (!$process.WaitForExit(60000)) { throw 'Native handoff probe did not exit within 60 seconds.' }
        Get-Content -LiteralPath $result
        if ($process.ExitCode -ne 0) { throw 'Native handoff regression failed.' }
    } finally { $process.Dispose() }
} finally {
    $full = [IO.Path]::GetFullPath($fixture)
    if ((Split-Path $full) -ne [IO.Path]::GetTempPath().TrimEnd('\') -or (Split-Path $full -Leaf) -cnotmatch '^uninstall-handoff-[a-f0-9]{32}$') { throw 'Cleanup escaped handoff fixture.' }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
