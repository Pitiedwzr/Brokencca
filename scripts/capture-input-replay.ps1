param([ValidateSet('off','on')][string]$CaptureState = 'off', [int]$Rate = 480, [int]$Seconds = 10, [int]$Port = 24974)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$output = Join-Path $root "artifacts/capture-input-$CaptureState"
New-Item -ItemType Directory -Force -Path $output | Out-Null
function Start-ReplayProcess([string[]]$ProcessArgs) {
    $info = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($processArg in $ProcessArgs) { $info.ArgumentList.Add($processArg) }
    $proc = [Diagnostics.Process]::new(); $proc.StartInfo = $info
    if (-not $proc.Start()) { throw 'Could not launch replay.' }
    return [pscustomobject]@{ Process = $proc; Out = $proc.StandardOutput.ReadToEndAsync(); Err = $proc.StandardError.ReadToEndAsync() }
}
$sim = $null; $hostProc = $null
try {
    $sim = Start-ReplayProcess @((Join-Path $root 'tools/Brokencca.Simulator/bin/Release/net8.0/Brokencca.Simulator.dll'), '--port', $Port.ToString(), '--workload', 'all', '--rate', $Rate.ToString(), '--duration', $Seconds.ToString())
    $hostProc = Start-ReplayProcess @((Join-Path $root 'src/Brokencca.Host/bin/Release/net8.0/Brokencca.Host.dll'), '--port', $Port.ToString(), '--dry-run', '--quiet', '--diagnostics')
    if (-not $sim.Process.WaitForExit(($Seconds + 15) * 1000)) { throw 'Input replay timeout.' }
    if ($sim.Process.ExitCode -ne 0) { throw "Simulator failed: $($sim.Err.GetAwaiter().GetResult())" }
    Start-Sleep -Milliseconds 1100
    if ($hostProc.Process.HasExited) { throw "Input host exited: $($hostProc.Err.GetAwaiter().GetResult())" }
    $hostProc.Process.Kill($true); $hostProc.Process.WaitForExit(5000) | Out-Null
    $text = $hostProc.Out.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $output 'host.log'), $text)
    [IO.File]::WriteAllText((Join-Path $output 'host.stderr.log'), $hostProc.Err.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $output 'simulator.log'), $sim.Out.GetAwaiter().GetResult())
    $reports = @($text -split '\r?\n' | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.kind -eq 'diagnostics' })
    if ($reports.Count -eq 0) { throw 'No diagnostic input reports.' }
    if (($reports | Measure-Object queue_overflows -Sum).Sum -ne 0 -or ($reports | Measure-Object sequence_gaps -Sum).Sum -ne 0) { throw 'Input overflow/sequence gap during replay.' }
    $summary = [pscustomobject]@{ capture = $CaptureState; rate = $Rate; seconds = $Seconds; frames = ($reports | Measure-Object frames -Sum).Sum; queue_overflows = 0; sequence_gaps = 0; reports = $reports.Count; sink = 'dry-run-quiet (not serial/game acceptance)' }
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'summary.json')
    $summary | ConvertTo-Json -Compress
} finally {
    foreach ($owned in @($sim, $hostProc)) {
        if ($null -eq $owned) { continue }
        if (-not $owned.Process.HasExited) { $owned.Process.Kill($true); $owned.Process.WaitForExit(5000) | Out-Null }
        $owned.Process.Dispose()
    }
}
