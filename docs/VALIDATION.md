# Implementation validation

## User-reported hardware acceptance

The user has tested the prototype and considers it suitable as a prototype release.
These are user-reported results, not new measurements made by the assistant:

| Check | Reported result |
| --- | --- |
| IPA installation and operation | Working |
| Multitouch and dry-run input | Working |
| Apple device services and iproxy on the development laptop | Working |
| Host on a separate WACCA machine, with COM5/COM6 | Game communication and touch recognition working |
| Actual gameplay | Latency usually imperceptible; noticeable delay with more contacts, faster slides, and rapid taps |
| Extreme ten-contact rapid movement | Reported host "touch overflow", followed by successful recovery after a 1-second retry |

Tested host command:

```text
Brokencca.Host.exe --iproxy "./iproxy/iproxy.exe" --serial --left COM5 --right COM6
```

This establishes a playable wired-input prototype on the tested setup. Device
models, exact build IDs, quantitative timing, all-zone coverage, and session
duration were not supplied. The report does not identify the exact overflow
message or conclusively localize its source. In the current source, the serial
sink has a 64-state overflow guard, the iOS sender has a 64-pending-write guard,
and the dry-run console sink has no touch queue. Capture the exact message and
mode in the next diagnostic build rather than attributing all delays to serial.

Those were the initial reports; the paired measurements below supersede the
uncertainty about the overflow location and device/build metadata.
Next work is defined in [NEXT-STEPS.md](NEXT-STEPS.md).

## Paired serial/game baseline reviewed 2026-10-01

Sources: `log/game_30mins.log` and `log/ios_game_30mins.log`. The user ran ten-
finger stress, press/release/repress (matched the game's input display), then
30-minute normal play without perceptible growing delay or stuck input. The
boundary-expansion feature was present and was reported to improve playability.

Metadata: host `0.2.0-diagnostics+0db34a8`, Windows `10.0.26200`,
`DESKTOP-1U1C6L9`, AMD RX6650 XT, iPad mini (5th generation), iproxy 2.1.1,
COM5/COM6 at 115200. Host log contains 2,267 diagnostics reports spanning about
37m47s and 56,241 frames; iOS has 2,133 `BCCA_DIAG` reports. Different coverage
and unsynchronized clocks prevent treating these as aligned per-frame traces.

| Measurement | Observed baseline |
| --- | --- |
| Host queue overflows | 6, high-water 64; seven connected sessions |
| Worst dequeued queue age | 733.269 ms |
| Typical queue timing | Median of populated 1-second window medians: 8.810 ms; median window p95: 15.325 ms |
| Serial write max in the six overflow windows | 0.317, 0.190, 0.300, 0.334, 0.198, 0.244 ms |
| Serial write worst across entire run | 29.778 ms; occasional write/scheduling outliers still need monitoring |
| iOS maximum contacts / pending-write high-water | 10 / 4 (guard is 64) |
| iOS worst sampled oldest-pending age | 4.521 ms; periodic sampling is not a worst-ever bound |
| Host after final reconnect | 2,093 reports, 49,778 frames, zero overflows/sequence gaps; maximum depth 23 and queue age 346.823 ms |

The last segment includes any post-stress/repress activity as well as normal
play; exact workload boundaries were not separately marked. The supplied
percentiles are summaries of reporting windows, **not** pooled session
percentiles. Some PowerShell-merged stderr is wrapped/truncated; the overflow
count comes from the complete diagnostics counters, not six intact error JSONs.

Conclusion: overflow is confirmed in the **Windows serial worker's FIFO**.
The old worker dequeued at most one state before `Thread.Sleep(1)`; its writes
also held the producer lock. Growing queue age despite fast writes strongly
implicates worker scheduling/service rate. This does not prove that virtual COM
baud rate, USB bandwidth, or game polling is the limiting factor. There is no
evidence of iOS's 64-write guard overflowing in this run. Normal play is a good
stability result, but occasional host backlog remains visible in the counters.

## Serial worker revision

Implemented: dedicated I/O thread woken by input/serial arrival/cancellation,
bounded FIFO draining with no sleep between ready states, producer-lock-free
port writes, and generation-checked reset ordering. An already committed pair
finishes before the reset release; stale queued/dequeued states cannot replay
after that release. Both ports get independent best-effort shutdown release.
No queue enlargement, edge merging, baud change, protocol change, or iOS change.

Local Release build: zero warnings/errors. Self-contained Windows x64 publish
and packaged socket smoke test passed. Regression runner: **26/26 passed**,
including stale dequeued/heartbeat invalidation, duplicates at capacity,
64-transition ordered bursts and paired counters/checksums, reset/overflow
during a blocked write, command fairness, independent scan readiness, keepalive,
cancellation, persistent port failure, arrival timestamp propagation, and
diagnostic window reset. These fake-
endpoint tests validate ordering/fail-safety, not game-PC throughput or sampling.

### Serial-worker retest

Use the complete self-contained folder `artifacts/windows-serial-worker` and
the existing IPA. Start the host before the game as usual. Confirm `run_info`
contains `0.2.0-serial-worker`; it distinguishes these uncommitted changes from
the diagnostic baseline even if the appended Git revision is the same.

All workloads can be performed during **one game launch**:

1. Repeat the exact reproducing ten-finger slide for at least 60 seconds; then
   release everything. Require zero queue overflows/reconnects and no growing
   queue age. Note the approximate elapsed time of this segment.
2. Repeat rapid press/release/repress and chords in the game's input display.
   Require the same visible edges; quicker writes alone do not prove sampling.
3. While holding a touch, suspend/foreground the app or unplug/reconnect USB.
   Require both halves to release and recover without replaying old holds.
4. Play normally for 30 minutes, with a stress repeat near the end. Require no
   stuck input or increasing backlog. Compare `receive_to_serial_ms` p95/p99
   against provisional 5/10 ms host-only targets in active windows, plus queue
   age/depth and per-port write/driver-buffer statistics.
5. If convenient, restart the game with the host still running to check board
   startup/parser behavior; this need not mean restarting every workload.

Record paired host/iOS logs and segment times. If the fixed worker still grows
a queue, inspect per-port write and pending-driver-byte counters before choosing
another fix. If queues stay low but rapid taps disappear, investigate game
sampling separately rather than silently dropping or stretching edges.

To avoid PowerShell's wrapped native-error records, capture raw stdout/stderr
with CMD redirection (from PowerShell, with the usual paths adjusted):

```powershell
cmd /c '.\Brokencca.Host.exe --serial --left COM5 --right COM6 --iproxy .\iproxy\iproxy.exe --diagnostics --device-model "iPad mini (5th generation)" --gpu "AMD RX6650 XT" > .\log\game_serial_worker.log 2>&1'
```

Ensure `log` exists in the launch directory; this command logs instead of showing
live console output. The real-hardware acceptance gate is still **pending**.

## Initial automated checks

Local checks performed on Windows, 2026-09-30:

| Check | Result |
| --- | --- |
| Full solution Release build, .NET SDK 8.0.425 | Passed, zero warnings/errors |
| Dependency-free C# regression runner | 16/16 passed |
| iOS portable C geometry/wire tests, MinGW GCC with `-Wall -Wextra -Werror` | Passed |
| Self-contained Windows x64 publish | Passed |
| Published executable socket smoke test | Passed |
| iOS Info.plist XML parsing | Passed |

The C# suite covers fragmented/coalesced control frames, malformed and oversized
headers, truncated frames, immutable state, every serial zone, checksums/startup
fixtures, geometry, ordered press/release, reset, disconnect, heartbeat timeout,
stale sequence, sequence wrap, partial-message timeout, and cancellation.

The portable C test compiles the **same headers used by the iOS application**.
It verifies all 240 sector centres, selected radius/centre boundary cases, and
the shared golden wire header. It does not compile UIKit or Network.framework.

The executable smoke test uses a loopback simulated iOS peer in dry-run mode.
It first sends a malformed handshake and verifies that the host reconnects.
It then sends press/release/press and disconnects while held; the observed
sequence is `[0]`, `[]`, `[0]`, `[]`. The host remains alive awaiting reconnection.

## Still needing validation or improvement

- Exact artifact/build provenance and installation method for reproducibility;
  successful installation is established, but workflow logs were not reviewed.
- Cable removal, background/foreground, rotation, device selection, and game
  restart behavior beyond the reported overflow recovery.
- Exhaustive all-240-zone validation and startup on other game/driver versions.
- Burst-based serial request parsing under fragmented/coalesced game traffic;
  successful gameplay does not establish every possible command boundary.
- Quantitative latency/jitter, sustained input rate, overload source, and
  preservation/game sampling of very short taps **after the worker fix**.
  The baseline now identifies the host FIFO as the overflow source; hardware
  confirmation of the fix and end-to-end game latency remain outstanding.
- Capture, encoding, video streaming, or audio: not implemented in this milestone.

The automated checks establish behavior under simulation; the user's device and
game tests additionally establish practical compatibility on their setup. Neither
is a quantitative latency guarantee. See PLAN.md for the remaining device checklist.
