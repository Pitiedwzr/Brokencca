param([string]$LogPath = 'artifacts/capture-soak/preview.jsonl', [int]$WarmupReports = 10)
$ErrorActionPreference = 'Stop'
$records = @(Get-Content -LiteralPath $LogPath | Where-Object { $_.StartsWith('{') } | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $null -ne $_.capture })
if ($records.Count -le $WarmupReports + 1) { throw 'Not enough steady-state capture diagnostics.' }
$steady = @($records | Select-Object -Skip $WarmupReports)
$rates = @($steady | ForEach-Object { $_.presents_per_second })
$windowP95 = @($steady | ForEach-Object { $_.qpc_to_present_submit_p95_ms } | Where-Object { $null -ne $_ })
$memory = @($steady | ForEach-Object { $_.working_set_bytes / 1MB })
$ordered = @($windowP95 | Sort-Object)
$first = $steady[0]; $last = $steady[-1]
$summary = [pscustomobject]@{
    diagnostic_reports = $records.Count
    steady_reports = $steady.Count
    average_present_rate = ($rates | Measure-Object -Average).Average
    minimum_window_present_rate = ($rates | Measure-Object -Minimum).Minimum
    median_rolling_window_p95_ms = $ordered[[int][Math]::Floor(($ordered.Count-1)/2)]
    max_rolling_window_p95_ms = ($windowP95 | Measure-Object -Maximum).Maximum
    working_set_first_mb = $memory[0]
    working_set_last_mb = $memory[-1]
    working_set_min_mb = ($memory | Measure-Object -Minimum).Minimum
    working_set_max_mb = ($memory | Measure-Object -Maximum).Maximum
    pending_max = ($steady | ForEach-Object { $_.capture.pending } | Measure-Object -Maximum).Maximum
    leased_max = ($steady | ForEach-Object { $_.capture.leased } | Measure-Object -Maximum).Maximum
    submissions = $last.capture.submitted - $first.capture.submitted
    presentations = $last.presented - $first.presented
    rate_drops = $last.capture.dropped_rate - $first.capture.dropped_rate
    slot_drops = $last.capture.dropped_slots - $first.capture.dropped_slots
    busy_callbacks = $last.capture.dropped_busy - $first.capture.dropped_busy
    replaced_pending = $last.capture.replaced - $first.capture.replaced
    failures = @($steady | Where-Object { $_.capture.state -eq 'Faulted' }).Count
    stale_windows = @($steady | Where-Object { $_.capture.stale }).Count
    provisional_rate_gate_passed = (($rates | Measure-Object -Average).Average -ge 59)
    provisional_timing_gate_passed = (($windowP95 | Measure-Object -Maximum).Maximum -le 33.3)
    note = 'Rolling 120-frame window p95 summaries, not a pooled session percentile. Arrival/submission/presentation counts do not prove unique source images or visible latency.'
}
$summary | ConvertTo-Json
