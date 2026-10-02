param(
    [switch]$BootBlack,
    [switch]$Borderless,
    [switch]$Lifecycle,
    [switch]$DeviceLoss,
    [switch]$StartStop,
    [int]$SlowConsumerMs = 0,
    [int]$Seconds = 8,
    [string]$OutputDirectory = 'artifacts/capture-smoke',
    [string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$fixtureDll = Join-Path $root 'tools/Brokencca.CaptureFixture/bin/Release/net8.0-windows10.0.19041.0/Brokencca.CaptureFixture.dll'
$previewDll = Join-Path $root 'tools/Brokencca.CapturePreview/bin/Release/net8.0-windows10.0.19041.0/Brokencca.CapturePreview.dll'
if ($PackageDirectory) {
    $fixtureDll = Join-Path $root "$PackageDirectory/fixture/Brokencca.CaptureFixture.exe"
    $previewDll = Join-Path $root "$PackageDirectory/preview/Brokencca.CapturePreview.exe"
}
if (-not (Test-Path -LiteralPath $fixtureDll) -or -not (Test-Path -LiteralPath $previewDll)) { throw 'Build the capture projects in Release before smoke testing.' }
if ($Lifecycle -and $Seconds -lt 16) { $Seconds = 16 }
if ($StartStop -and $Seconds -lt 60) { $Seconds = 60 }
$outputPath = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
function Start-CaptureProcess([string[]]$ProcessArgs) {
    $info = [Diagnostics.ProcessStartInfo]::new($dotnet)
    if ($ProcessArgs[0].EndsWith('.exe')) { $info.FileName = $ProcessArgs[0]; $ProcessArgs = $ProcessArgs[1..($ProcessArgs.Length-1)] }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.WorkingDirectory = $root
    foreach ($processArg in $ProcessArgs) { $info.ArgumentList.Add($processArg) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw 'Could not launch capture process.' }
    return [pscustomobject]@{ Process = $process; Out = $process.StandardOutput.ReadToEndAsync(); Err = $process.StandardError.ReadToEndAsync() }
}
$fixture = $null; $preview = $null
try {
    $fixtureArgs = @($fixtureDll, '--title', 'Mercury  ', '--seconds', ($Seconds + 15).ToString())
    if ($BootBlack) { $fixtureArgs += '--boot-black' }
    if ($Borderless) { $fixtureArgs += '--borderless' }
    if ($Lifecycle) { $fixtureArgs += '--exercise-lifecycle' }
    $fixture = Start-CaptureProcess $fixtureArgs
    # Use only the fixture we own; never select an unrelated real Mercury by title.
    $hwnd = [IntPtr]::Zero
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $fixture.Process.Refresh()
        if ($fixture.Process.HasExited) { throw "Fixture exited: $($fixture.Err.GetAwaiter().GetResult())" }
        $hwnd = $fixture.Process.MainWindowHandle
        if ($hwnd -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 100 }
    } while ($hwnd -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($hwnd -eq [IntPtr]::Zero) { throw 'Fixture has no visible window; interactive desktop test unavailable.' }
    $previewArgs = @($previewDll, '--hwnd', ('0x{0:X}' -f $hwnd.ToInt64()), '--seconds', $Seconds.ToString(), '--smoke-test', '--diagnostics')
    if ($BootBlack) { $previewArgs += '--calibrate-black' }
    if ($DeviceLoss) { $previewArgs += @('--inject-device-loss-at', '2') }
    if ($StartStop) { $previewArgs += @('--capture-cycles', '20') }
    if ($SlowConsumerMs -gt 0) { $previewArgs += @('--slow-consumer-ms', $SlowConsumerMs.ToString()) }
    $preview = Start-CaptureProcess $previewArgs
    if (-not $preview.Process.WaitForExit(($Seconds + 15) * 1000)) { throw 'Preview smoke test timed out.' }
    $previewOut = $preview.Out.GetAwaiter().GetResult()
    $previewErr = $preview.Err.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $outputPath 'preview.jsonl'), $previewOut)
    [IO.File]::WriteAllText((Join-Path $outputPath 'preview.stderr.log'), $previewErr)
    if ($preview.Process.ExitCode -ne 0 -or $previewOut -notmatch '"capture_smoke":"passed"') { throw "Capture smoke failed (exit $($preview.Process.ExitCode)): $previewErr" }
    if ($StartStop -and $previewOut -notmatch '"capture_cycles_passed":20') { throw 'Capture did not complete all 20 start/stop cycles.' }
    Write-Output "PASS capture smoke: boot=$BootBlack borderless=$Borderless lifecycle=$Lifecycle deviceLoss=$DeviceLoss slowConsumerMs=$SlowConsumerMs; logs: $outputPath"
} finally {
    foreach ($owned in @($preview, $fixture)) {
        if ($null -eq $owned) { continue }
        if (-not $owned.Process.HasExited) { $owned.Process.Kill($true); $owned.Process.WaitForExit(5000) | Out-Null }
        if ($owned -eq $fixture) {
            [IO.File]::WriteAllText((Join-Path $outputPath 'fixture.jsonl'), $owned.Out.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $outputPath 'fixture.stderr.log'), $owned.Err.GetAwaiter().GetResult())
        }
        $owned.Process.Dispose()
    }
}
