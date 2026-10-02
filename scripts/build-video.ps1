param([string]$Compiler = 'g++', [string]$OutputDirectory = 'artifacts/windows-video')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $target = & $Compiler -dumpmachine
    if ($LASTEXITCODE -ne 0 -or $target -notmatch 'x86_64') { throw 'Use an x86_64 MinGW-w64 C++ compiler.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $dll = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path 'brokencca-video.dll'
    & $Compiler -std=c++17 -O2 -Wall -Wextra -Werror -D_WIN32_WINNT=0x0602 -shared `
        native/video/video.cpp -static -static-libgcc -static-libstdc++ -o $dll `
        -lmfplat -lmfuuid -lmf -levr -ld3d11 -ldxgi -lole32 -loleaut32 -luuid
    if ($LASTEXITCODE -ne 0) { throw 'Hardware video DLL build failed.' }
    Write-Output "Built $dll (hardware Media Foundation H.264 and GPU NV12)"
} finally { Pop-Location }
