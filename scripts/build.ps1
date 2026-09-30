$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $root
try {
    & $dotnet build Brokencca.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $dotnet run --project tests/Brokencca.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Regression suite failed.' }
} finally { Pop-Location }
