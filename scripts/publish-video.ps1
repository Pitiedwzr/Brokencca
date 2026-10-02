param([string]$Compiler = 'g++', [string]$OutputDirectory = 'artifacts/windows')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $root
try {
    & $dotnet publish src/Brokencca.Host -c Release -r win-x64 --self-contained true -o $OutputDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
    & $dotnet publish tools/Brokencca.VideoProbe -c Release -r win-x64 --self-contained true -o $OutputDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Video probe publish failed.' }
    & "$PSScriptRoot/build-video.ps1" -Compiler $Compiler -OutputDirectory $OutputDirectory
    Copy-Item -LiteralPath README.md,LICENSE,NOTICE -Destination $OutputDirectory -Force
    $videoDocsDirectory = Join-Path $OutputDirectory 'docs'
    New-Item -ItemType Directory -Path $videoDocsDirectory -Force | Out-Null
    Copy-Item -Path (Join-Path $root 'docs/*') -Destination $videoDocsDirectory -Recurse -Force
    $videoRuntimeRoot = Split-Path $dotnet
    foreach ($videoRuntimeNotice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $videoRuntimeNoticePath = Join-Path $videoRuntimeRoot $videoRuntimeNotice
        if (Test-Path -LiteralPath $videoRuntimeNoticePath) {
            Copy-Item -LiteralPath $videoRuntimeNoticePath -Destination (Join-Path $OutputDirectory "DOTNET-$videoRuntimeNotice") -Force
        }
    }
} finally { Pop-Location }
