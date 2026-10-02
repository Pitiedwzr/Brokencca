param([string]$OutputDirectory = 'artifacts/windows-capture')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $root
try {
    foreach ($tool in @('Preview', 'Fixture')) {
        $destination = Join-Path $OutputDirectory $tool.ToLowerInvariant()
        & $dotnet publish "tools/Brokencca.Capture$tool" -c Release -r win-x64 --self-contained true -o $destination --nologo
        if ($LASTEXITCODE -ne 0) { throw "Capture $tool publish failed." }
    }
    Copy-Item -LiteralPath LICENSE,NOTICE -Destination $OutputDirectory
    Copy-Item -LiteralPath docs/CAPTURE-USAGE.md -Destination (Join-Path $OutputDirectory 'README.md')
    $captureDocsDestination = Join-Path $OutputDirectory 'docs'
    New-Item -ItemType Directory -Path $captureDocsDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $root 'docs/*') -Destination $captureDocsDestination -Recurse -Force
    $captureRuntimeRoot = Split-Path $dotnet
    foreach ($captureRuntimeNotice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $captureRuntimeNoticePath = Join-Path $captureRuntimeRoot $captureRuntimeNotice
        if (Test-Path -LiteralPath $captureRuntimeNoticePath) {
            Copy-Item -LiteralPath $captureRuntimeNoticePath -Destination (Join-Path $OutputDirectory "DOTNET-$captureRuntimeNotice") -Force
        }
    }
    Write-Output "Self-contained Windows x64 capture tools: $OutputDirectory"
} finally { Pop-Location }
