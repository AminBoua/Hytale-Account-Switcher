$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project Checks/Checks.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Native checks failed.' }
    node Checks/NativeLoginChecks.cjs
    if ($LASTEXITCODE -ne 0) { throw 'Frontend checks failed.' }
    & ./Checks/InstallerScriptChecks.ps1
    dotnet publish Launcher/Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o dist/build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath dist/build/HytaleAccounts.exe -Destination dist/HytaleAccounts-Setup.exe -Force
    $hash = (Get-FileHash -LiteralPath dist/HytaleAccounts-Setup.exe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'dist\SHA256SUMS.txt'), "$hash  HytaleAccounts-Setup.exe`n")
    Write-Output 'Ready: dist/HytaleAccounts-Setup.exe'
} finally { Pop-Location }
