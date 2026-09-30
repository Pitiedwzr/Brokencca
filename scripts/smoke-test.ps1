param([string]$HostExecutable)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $HostExecutable) { $HostExecutable = Join-Path $root 'artifacts/windows/Brokencca.Host.exe' }
if (-not (Test-Path -LiteralPath $HostExecutable)) { throw 'Publish the Windows host to artifacts/windows first.' }
$logDirectory = Join-Path $root ('artifacts/smoke/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$outputLog = Join-Path $logDirectory 'host.out.log'
$errorLog = Join-Path $logDirectory 'host.err.log'
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$process = $null
$peer = $null
function Accept-Peer {
    $pending = $listener.AcceptTcpClientAsync()
    if (-not $pending.Wait(10000)) { throw 'Host did not connect/reconnect.' }
    $client = $pending.Result
    $client.NoDelay = $true
    $client.ReceiveTimeout = 3000
    $header = [byte[]]::new(28)
    $client.GetStream().ReadExactly($header, 0, $header.Length)
    if ([System.Text.Encoding]::ASCII.GetString($header, 0, 4) -ne 'BCCA') { throw 'Invalid host handshake.' }
    return $client
}
function Send-Frame([System.Net.Sockets.NetworkStream]$stream, [byte]$type, [uint32]$seq, [byte[]]$payload) {
    $frame = [byte[]]::new(24 + $payload.Length)
    [System.Text.Encoding]::ASCII.GetBytes('BCCA').CopyTo($frame, 0)
    $frame[4] = 1
    $frame[5] = $type
    [BitConverter]::GetBytes([uint32]$payload.Length).CopyTo($frame, 8)
    [BitConverter]::GetBytes($seq).CopyTo($frame, 12)
    $payload.CopyTo($frame, 24)
    $stream.Write($frame, 0, $frame.Length)
}
try {
    $process = Start-Process -FilePath $HostExecutable -ArgumentList @('--dry-run', '--port', $port) `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput $outputLog -RedirectStandardError $errorLog
    $peer = Accept-Peer
    # Malformed peer must not kill the host or prevent a later connection.
    $badHeader = [byte[]]::new(24)
    $peer.GetStream().Write($badHeader, 0, $badHeader.Length)
    $peer.Dispose()
    $peer = Accept-Peer
    Send-Frame $peer.GetStream() 1 0 ([byte[]]@(240, 0, 30, 0))
    $held = [byte[]]::new(30)
    $held[0] = 1
    Send-Frame $peer.GetStream() 2 1 $held
    Start-Sleep -Milliseconds 50
    Send-Frame $peer.GetStream() 2 2 ([byte[]]::new(30))
    Start-Sleep -Milliseconds 50
    Send-Frame $peer.GetStream() 2 3 $held
    Start-Sleep -Milliseconds 50
    $peer.Dispose()
    $peer = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 50
        $lines = @(Get-Content -LiteralPath $outputLog | Where-Object { $_ -like 'Zones:*' })
    } while ($lines.Count -lt 4 -and [DateTime]::UtcNow -lt $deadline)
    if (($lines -join '|') -ne 'Zones: [0]|Zones: []|Zones: [0]|Zones: []') {
        throw "Unexpected transitions: $($lines -join '|')"
    }
    if ($process.HasExited) { throw 'Host exited instead of waiting for reconnect.' }
    Write-Output 'PASS published host: malformed handshake recovery, ordered transitions, disconnect release'
} finally {
    if ($peer) { $peer.Dispose() }
    $listener.Stop()
    # Only the dry-run child started by this test is terminated; no serial ports were opened.
    if ($process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
