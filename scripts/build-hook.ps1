param(
    [string]$Compiler = 'gcc',
    [string]$OutputDirectory = 'artifacts/windows-hook',
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $target = & $Compiler -dumpmachine
    if ($LASTEXITCODE -ne 0 -or $target -notmatch 'x86_64') { throw 'Use an x86_64 MinGW-w64 compiler; Mercury is a 64-bit process.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $dll = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path 'brokencca-mercuryio.dll'
    & $Compiler -std=c11 -O2 -Wall -Wextra -Werror -D_WIN32_WINNT=0x0602 -shared `
        native/mercuryio/mercuryio.c native/mercuryio/mercuryio.def -static-libgcc -o $dll -luser32
    if ($LASTEXITCODE -ne 0) { throw 'MercuryIO DLL build failed.' }
    Write-Output "Built $dll (MercuryIO API 1.0, Windows x64)"
    if ($Test) {
        New-Item -ItemType Directory -Path artifacts/tests -Force | Out-Null
        & $Compiler -std=c11 -Wall -Wextra -Werror tests/ios-core-test.c -lm -o artifacts/tests/ios-core-test.exe
        if ($LASTEXITCODE -ne 0) { throw 'Portable iOS tests failed to compile.' }
        & ./artifacts/tests/ios-core-test.exe
        if ($LASTEXITCODE -ne 0) { throw 'Portable iOS tests failed.' }
        $dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
        if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
        $previous = $env:BROKENCCA_TEST_DLL
        $previousHost = $env:BROKENCCA_TEST_HOST
        try {
            $env:BROKENCCA_TEST_DLL = $dll
            $packagedHost = Join-Path (Split-Path $dll -Parent) 'Brokencca.Host.exe'
            if (Test-Path -LiteralPath $packagedHost) { $env:BROKENCCA_TEST_HOST = $packagedHost }
            & $dotnet run --project tests/Brokencca.Tests -c Release --no-build
            if ($LASTEXITCODE -ne 0) { throw 'Hook/native regression suite failed.' }
        } finally { $env:BROKENCCA_TEST_DLL = $previous; $env:BROKENCCA_TEST_HOST = $previousHost }
    }
} finally { Pop-Location }
