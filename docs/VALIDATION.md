# Implementation validation

## Mercury window capture acceptance, 2026-10-02

**Milestone 2 is complete and accepted on the user's tested game PC.** The user
reports that capture works very well, boot-black circle detection aligns
perfectly, and the preview has no perceptible latency relative to the real
window. This is visual, user-reported acceptance, not a measured zero-latency
claim. The development device still has no runnable Mercury; the game-PC test
completes the real-game compatibility check after the local fixture proof.

Reviewed supplied evidence: `log/capture.log` and `log/capture-profile.json`.
These local files retain the original run and confirmed calibration.

| Check | Evidence |
| --- | --- |
| Build and machine | `0.2.0-capture-proof+78944fbd39cdb54649548ba7ee000437a6bc3cf1`; Windows `10.0.26200`; AMD Radeon RX 6650 XT |
| Real source | HWND `0x150E60`, PID `6536`, class `UnrealWindow`, title `Mercury  ` (two trailing spaces) |
| Capture geometry | Client `1080x1920`, `144 DPI` (150%); WGC item `1084x1967`; measured client offset `(2,45)` removes the window frame/titlebar |
| Boot-black calibration | Three identical detections, consensus reached on sample 3; confidence `0.96826`, boundary error `0.00397`; confirmed saved profile matches the detections |
| Profile | Version 1, full client crop `(0,0,1,1)`, center `(0.5007567,0.4708033)`, radius `0.4893998 * min(width,height)`, provenance `black-background`, `Confirmed: true` |
| Circle in client pixels | Center approximately `(540.82,903.94)`, radius `528.55`; user reports perfect alignment |
| Bounded capture and lifecycle | 59 state-bearing diagnostic reports, including 58 Running and a final normal `Stopped/source-closed`; maximum sampled pending depth 1, zero slot-capacity drops, zero faults/recovery attempts; window movement reflected through geometry generation 16 |
| Session totals | 3,462 arrivals, 3,429 submissions, 3,386 presentations; 11 busy-callback drops, 30 rate drops, 31 replaced pending frames |

Performance observations are retained without treating this short interactive
run as a sustained benchmark. Excluding the first 10 state-bearing reports,
49 reports average **58.01 presentations/s**, below the provisional 59/s
benchmark target. There is one stale report (last-frame age `770.707 ms`,
`13.98` presentations/s); its cause is not established by the log. Capture
subsequently resumes, and the final source closure is normal. These observations
do not change the user's functional acceptance of the tested capture setup.

The maximum reported rolling-window timing p95 after warmup is `5.676 ms`.
Most timing reports are zero; the implementation clamps negative timestamp
differences to zero, so they do not establish zero visible latency. Quantitative
latency and sustained unique-frame throughput remain measurements for the video
milestones. The ten-minute fixture benchmark below is separate evidence.

Next implementation milestone: hardware H.264/NV12, separate USB video framing,
and iOS decode/Metal presentation. Combined input/video stress and additional
hardware/rendering combinations remain part of that integration's validation.

## Local window capture proof, 2026-10-02

Implemented a standalone C#/WinRT WGC/D3D11 capture library, WinForms GPU
preview, and separate procedural D3D11 fixture. The input host, native IO DLL,
wire protocol, and iOS app were not changed for capture. The preview uses three
owned textures, one latest pending frame, disposable consumer leases, event
queries for reuse, GPU crop/aspect-fit/guide rendering, and explicit lifecycle
states. CPU readback is opt-in for pixel tests and boot-circle calibration.
See [CAPTURE-USAGE.md](CAPTURE-USAGE.md) for runnable commands.

Test environment: Windows `10.0.26200`, .NET SDK `8.0.425`, AMD Radeon(TM)
Graphics development laptop, desktop `2560x1600` at reported `120 Hz`, and
PerMonitorV2 source/preview at `144 DPI` (150%). Fixture client `1280x720` SDR.
This is **not** the user's RX6650 XT Mercury machine. No runnable Mercury or
iOS video was used in these checks.

| Check | Result |
| --- | --- |
| Release solution build | Passed, zero warnings/errors |
| C# regression runner including native hook DLL | 34/34 passed; new coverage includes crop/transform/profile rejection, bounded slot ownership, boot-black fitting/consensus, and bounded device recovery |
| Self-contained Windows x64 preview/fixture publish | Passed; package under `artifacts/windows-capture` |
| Decorated client crop at 150% DPI | Four corner color markers passed; final packaged smoke additionally checks all four one-pixel client edges |
| Borderless resize/minimize/restore | 20 resize cycles with periodic minimize/restore; 20 frame-pool recreations, no fault/deadlock, client marker checks passed across observed stable generations |
| Complete start/stop | 20 complete device/session/presenter cycles passed |
| Boot-black calibration | Three consistent samples recovered fixture center `(0.54074, 0.47149)` and radius `0.38837 * min(client width,height)`, against fixture `(0.54,0.47,0.39)`; confidence about 0.965; result remains a suggestion requiring confirmation |
| Calibration rejection | Regression coverage for all-black, rectangles, ellipses, clipped/small circles, changing observations; a continuous large outer ring is also accepted |
| Device-loss recovery | Injected `DXGI_ERROR_DEVICE_REMOVED` rebuilt the entire GPU/session/presenter and resumed verified boot calibration; this is simulation, not a physical GPU reset |
| Slow consumer | Explicit 250 ms stall passed; next acquired frame age 9.63 ms in the packaged check, latest pending depth stayed bounded, and normal presentation resumed |
| Input replay, capture off/on | Same 480/s, 10-second `all` workload: 4,797 diagnostic frames in each case, zero sequence gaps/queue overflows; quiet dry-run only, not USB/serial/game acceptance |
| Ten-minute GPU capture soak | Passed provisional average-rate and rolling timing gates; details below |

The long published-executable run lasted about 610 seconds and emitted 609
diagnostic reports plus a successful smoke result (36,223 presentations total).
The first 10 reports were excluded from the steady-state summary. The source
rendered approximately 60 presentations/s. The preview averaged **59.49
presentations/s** across 599 steady reports; the slowest one-second window was
**43.99/s**, so this does not establish perfectly uniform 60 fps delivery.

Median of reported rolling 120-frame timing p95 values: **0.370 ms**; maximum
reported rolling p95: **15.820 ms**, below the provisional 33.3 ms gate. These
are overlapping rolling-window summaries, **not** a pooled session p95 or
capture-to-visible-display measurement. Arrival/submission/presentation counts
do not independently establish unique rendered game images.

In the steady comparison span: 35,743 submissions, 35,600 presentations,
159 rate drops, 173 skipped busy callbacks, 142 replaced pending frames, and
zero slot-capacity drops. Maximum sampled pending depth was one; no capture
faults or stale reporting windows were recorded. Working set started at
139.69 MiB and ended at 22.75 MiB, with range 7.69..148.81 MiB; there was no
growing working-set trend. This is process working set, not a VRAM/leak proof.
The input-on replay and regression suite ran during the soak, so it includes
some concurrent CPU work rather than a fully isolated benchmark.

Reproducible logs: `artifacts/capture-soak`, `capture-smoke-boot`,
`capture-smoke-lifecycle`, `capture-smoke-recovery`, `capture-smoke-start-stop`,
`capture-smoke-slow-final`, and `capture-input-off`/`capture-input-on`.
Summarize the soak with `scripts/analyze-capture-log.ps1`. The soak's original
run metadata reported assembly version `0.2.0.0`; the final tools use the
distinct `0.2.0-capture-proof` informational label. Final packaged checks passed
for one-pixel edges and boot calibration with simulated device recovery after
the documentation/calibration-guard updates (`capture-smoke-final-edges` and
`capture-smoke-final-boot`).

Additional coverage: exclusive fullscreen, HDR/tone mapping, physical device reset, other DPI levels,
mixed-DPI/negative-coordinate monitor configurations, multi-GPU behavior, and
game-PC input timing with capture off/on. These combinations were not established
by the local fixture or the supplied Mercury run. Real Mercury crop/circle
alignment and boot-black calibration are accepted above. CI builds and
runs deterministic tests but has not been invoked/reviewed for this revision;
interactive GPU acceptance is not implied by a hosted CI build.

Status: **local window capture proof complete; Mercury acceptance recorded above**.
H.264/NV12, USB video framing, iOS decode/Metal presentation, and end-to-end
video latency are Milestone 3 and remain unimplemented.

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

## MercuryIO and LED revision, 2026-10-02

The user supplied segatools source `8a966b2`. Brokencca now builds its own
MercuryIO 1.0 DLL plus `--hook` host backend and an optional independent iPad
LED stream. No segatools/WACVR/Brokenithm files were modified. The user reported
a brief revised-serial stress test without overflow, but supplied no new log;
the full serial gate remains pending.

Local Release solution build and self-contained Windows x64 publish passed
with zero warnings/errors. Native DLL built with MinGW-w64 GCC using
`-Wall -Wextra -Werror`. **30/30** C# regressions passed with the native DLL,
including all 240 C#/C touch mappings and external-serial equivalence,
press/release/repress, in-flight reset,
FIFO overflow/duplicates, duplicate-host rejection, dead callback detection,
unchanged LED/alpha bytes, malformed/fragmented LED frames, and separate LED
mutex/socket failure without input reset.

The runner also launched the packaged host against a fake native game in a
separate process: touch and LED IPC, USB-peer reconnect/release, LED socket
failure isolation, and native watchdog release after actually terminating that
isolated host process passed. No real game or COM ports were involved.
Published dry-run socket smoke test passed separately.

Portable iOS C tests passed for the real geometry/wire headers, LED frame
validation, and a bijection over all 480 LED positions. **UIKit/Network.framework
compilation has not been performed locally**; the updated macOS CI workflow
must build the new unsigned IPA before device LED testing. Input-only hook
testing can use the existing IPA.

Real-game hook compatibility, foreground behavior, downstream edge sampling,
LED orientation/two-subcell ordering, LED-on/off input timing, and sustained
play remain unvalidated. Tests and configuration are in [HOOK-IO.md](HOOK-IO.md).

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

The 2026-10-02 video protocol foundation builds in Release and passes the 35/35
C# regression suite. New checks cover v1/v2 touch compatibility, random token
echo and mismatch release, fragmented/coalesced video frames, the shared AVC
header fixture, length limits, IDR/NAL validation, strict JSON fields, and crop
rejection. The portable C fixture also passes locally with `-Wall -Wextra -Werror`
and exercises the matching video header, v2 HELLO, and AVC length rules.
These initial checks establish wire compatibility only. They are superseded for
implementation/build status by the 2026-10-03 video checks below; Xcode and device
acceptance remain pending.

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
- Window capture is implemented, fixture-tested, and accepted on the user's
  Mercury setup as reported above; the encoded iOS stream is implemented but
  awaits device acceptance. Audio streaming remains deferred.

The automated checks establish behavior under simulation; the user's device and
game tests additionally establish practical compatibility on their setup. Neither
is a quantitative latency guarantee. See PLAN.md for the remaining device checklist.

## Video implementation checks — 2026-10-03

The Windows encoder/transport and iOS decoder/Metal/touch integration are now
implemented. Release builds have zero warnings/errors; the extended C# suite
passes 36/36, including real Main/4.2 SPS parsing, non-B slice validation,
generation parameter-set changes, iOS 15–16 READY compatibility, and the input
protection signal. The portable iOS C test passes with `-Wall -Wextra -Werror`,
including strict duplicate/unknown/escaped/nested JSON field rejection.

The synthetic GPU probe on the development laptop's **AMD Radeon(TM) Graphics**
using **AMDh264Encoder** encoded 180/180 frames at nominal 720x1280/60 and produced
two IDRs, including the explicit request at frame 90. Independent FFmpeg decoding
reported no errors; ffprobe reported Main, level 4.2, 720x1280, limited-range
BT.709, and 180 decoded pictures. Before the optional buffer-control change, the
observed maximum submit-to-polled-output interval was 16.372 ms in this short
synthetic run; this is not a Mercury/iOS latency measurement. The published
package was tested again with the requested 125,000-byte CBR buffer accepted:
180/180 frames, two IDRs, maximum observed encoder interval 16.347 ms and a clean
FFmpeg decode. The published host's malformed-handshake recovery, ordered input
transitions and disconnect-release smoke test also passed.

The probe exposed a driver media-type SPS that differed from the in-band SPS.
Startup now prefers the actual bitstream parameter sets and uses media-type
headers only when the picture omits them. Changes in actual parameter sets
after configuration still trigger a fresh video generation.

GitHub Actions builds/packages the video DLL and probe with the self-contained
Windows host, then compiles and packages the unsigned iOS app on macOS. No Xcode
run is claimed from this Windows workspace. The signed IPA, RX 6650 XT encoder,
USB transport, iPad presentation, full touch calibration, sustained 60 fps,
thermal behavior, recovery and combined input/video stress tests remain pending.
See [VIDEO-USAGE.md](VIDEO-USAGE.md) for the exact launch and test checklist.
