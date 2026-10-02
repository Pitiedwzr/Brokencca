# Next milestone: stable input latency, then 60 fps video

## Decision and evidence

Accept the current version as the first playable wired-input prototype on the
user's tested setup. Installation, native multitouch, Apple services/iproxy,
WACCA board communication, and gameplay have been demonstrated by the user.

The next deliverable is an **input performance revision**, provisionally 0.2.
Adding video now would add CPU/GPU/USB work before we understand the existing
latency under load. Keep C# on Windows, Objective-C on iOS, the working serial
mapping, and iproxy. No language rewrite or native usbmux integration is needed
to investigate this problem. Version numbers here are planning labels, not tags.

Reported symptoms:

- Generally imperceptible latency during normal gameplay.
- Increasing delay with more contacts or faster movement.
- Delay during rapid taps.
- Extreme ten-contact movement produces a reported overflow and 1 s reconnect.

The initial report did not distinguish the host and iOS overflow guards.
The latest paired serial-mode logs now confirm six **host serial FIFO** overflows:
depth 64, queue age up to 733.269 ms, while serial writes in those reporting
windows took at most 0.334 ms. This localizes the sustained backlog to the host
serial worker and strongly supports a scheduling/service-rate problem, not a
demonstrated baud-rate limit. See [VALIDATION.md](VALIDATION.md) for the baseline.

## Step 1 — Diagnostic build and reproducible baseline

Deliver a host diagnostics mode plus small iOS timing counters. Include build
revision, sink mode, OS/device model, iproxy version, COM configuration, and a
machine-readable disconnect/overflow reason. Record both tested PCs separately.
Get the iOS model and game PC GPU before selecting video settings.

Measure stage-local intervals with monotonic clocks:

| Stage | Measurements |
| --- | --- |
| iOS touch callback | Event timestamp, callback start, contacts, changed zones, callbacks/s |
| iOS transport queue | Callback-to-enqueue/send time, pending writes, oldest pending age, send-completion delay |
| Windows receive | Sequence, frame rate, complete-frame arrival time, receiver stalls |
| Host sink queue | Enqueue/dequeue time, depth, oldest age, reset/overflow count |
| Serial output | Per-port write duration, pending driver bytes where meaningful, polling/wake intervals |
| Game-visible response | Repeated rapid-tap and slide tests; camera measurement where possible |

Retain the original iOS event timestamp before dispatching work: the existing
wire timestamp is assigned at send time and cannot measure upstream waiting.
Report median, p95, p99, max, and queue high-water marks. Aggregate console output
roughly once per second; detailed bounded traces are opt-in and flushed off the
input path. A quiet dry-run mode is essential to distinguish terminal overhead
from transport or device limits. Compare tracing on/off to measure its overhead.

Do not subtract unrelated iOS and Windows clocks. Start with intervals local to
each machine; use an explicit RTT/clock-offset exchange for cross-device timing
if needed. Account for measurement uncertainty. A socket send completion is not
proof that the host or game has consumed the state. New acknowledgements or
timestamp payloads need negotiated/versioned framing: v1 currently rejects
unexpected message types and lengths after HELLO.

Expand the simulator into repeatable workloads: single taps, two-finger chords,
ten-finger slides, release/repress of the same zone, and long bursts. Sweep input
rates such as 120/240/480/1000 snapshots per second to find actual capacity, not
to declare every rate a hardware requirement. Replay identical input through:

1. Quiet dry-run on the development laptop.
2. Quiet dry-run on the game PC.
3. Serial output on the game PC.

This comparison separates logging/host/USB costs from the serial/game path.

## Step 2 — Remove measured host bottlenecks

First implementation candidates, conditional on the baseline measurements:

- Replace one-dequeue-plus-`Thread.Sleep(1)` scheduling with a signalled worker.
  Drain ready work with bounded fairness for serial commands, resets, and shutdown.
  Maintain serialized writes per port and game startup/keepalive behavior.
  Sleep(1) is not a guaranteed 1 ms cadence; do not assume a fixed 15 ms cadence
  either. Measure it on the actual machine.
- Keep potentially blocking COM writes outside the producer queue lock. Retain
  a single I/O owner and generation/ordering rules: a reset must invalidate
  queued old states, and a previously dequeued state must not be replayed after
  the reset has been applied. Cover that race with deterministic tests.
- Eliminate synchronous per-state console printing from performance mode.
- After profiling, reuse receive buffers and reduce per-frame allocation/timer
  churn if GC or allocation time is material.
- Measure whether either COM port limits service rate. The 115200 setting on
  virtual com0com ports alone does not establish actual throughput or game polling
  rate; do not assume changing baud rate fixes it or is protocol-compatible.

Keep the proven board responses and mapping unchanged while investigating latency.
Harden the burst-based command parser separately using captured game transactions
if fragmentation or game-restart tests expose it as a problem.

## Step 3 — Control overload without erasing inputs

Do not simply increase 64 to a larger queue: that can hide overflow by increasing
delay. Do not send input only at the 60 Hz video cadence. Input processing remains
independent of rendering and responds immediately to changes.

Prefer capacity improvements before introducing lossy scheduling. Remove only
exact duplicate snapshots initially. A slide across a zone creates meaningful
press/release edges, so "movement" is not automatically disposable. A latest-state
slot can erase a full tap; OR-ing queued states can invent holds/chords. Neither
is an acceptable default.

If measured event production still exceeds sustained output capacity, explicitly
design an edge-preserving bounded scheduler. Verify same-zone repeated taps,
simultaneous contacts, cancellations, cross-zone slides, and final all-release.
The current bitmap cannot distinguish touch identity or begin/move/end causality;
add metadata only if a demonstrated scheduling policy requires it, with protocol
versioning and interoperability tests. Evaluate any minimum pulse duration as an
explicit game-calibration option, not an unconditional latency-adding fix.

Track **queue age as well as depth**. Define an overload budget from measurements;
do not replay seconds of stale input. Sustained overload cannot guarantee both
unbounded lossless history and low latency. Retain fail-safe release/reconnect for
true stalls until a tested reset/resynchronization protocol exists. Do not make
the 1 s reconnect faster as a substitute for fixing overload during normal play.

On iOS, measure transport-queue delay and main-thread rendering separately. Cache
static ring paths/background if drawing is significant; throttle visual feedback
independently of input. Acknowledge host consumption only if useful for measured
flow control; never wait a round trip for every individual touch packet.

## Step 4 — Acceptance gate for the input revision

Use the same device, cable, PC, game, and replay for before/after comparisons.

- Zero lost/reordered meaningful transitions in supported-rate replay, including
  rapid press/release/repress and simultaneous contacts. Test reset during an
  in-flight write, stalled consumer, overflow, partial packets, and reconnect.
- No overflow/reconnect in a 60-second repeat of the user's ten-contact stress
  case; no stuck touches or increasing backlog during a 30-minute play session.
- Provisional host-only target: receive-to-serial-write-completion p95 below
  5 ms and p99 below 10 ms on the game PC. Revise only with measured explanation
  of a driver/game constraint; this excludes iOS sampling and game consumption.
- Report timing for 1/2/5/10 contacts and fast slides/taps. Heavy-input delay must
  improve against baseline; measured queue age must remain bounded.
- Separately validate game-visible rapid taps. Successful serial writes and
  automated edge preservation do not prove the game sampled each short pulse.
- Repeat app suspension, rotation, cable removal/reconnect, and game restart.

Deliver diagnostics, trace/replay support, targeted scheduling fixes, updated
regression tests, and a before/after report. Changes that rely on real hardware
need a user test build before this gate can be marked passed.

## Step 5 — Resume the video plan

The standalone capture proof in [WINDOW-CAPTURE.md](WINDOW-CAPTURE.md) is complete
and accepted on real Mercury (2026-10-02). The development device has no runnable
Mercury; the user's game-PC log/profile and visual test complete that check.
The next implementation stage is encoded video over USB. Combined gameplay/video
acceptance still requires the input gate. Stages:

1. **Windows capture preview — complete:** explicit HWND or Mercury-title discovery,
   measured crop/DPI handling, bounded WGC/D3D11 capture targeting 60 fps, and
   local fixture validation. Real Mercury boot-black calibration and preview
   are accepted; compare input with capture off/on during video integration.
2. **Encoded video over USB:** hardware H.264, separate video connection, bounded
   queues, VideoToolbox decoding, and Metal presentation on iOS. Keep touch
   feedback local and keep the game/controller coordinate transform consistent.
   The reviewed implementation sequence, session/wire contracts, concrete queue
   budgets, and hardware acceptance matrix are in
   [VIDEO-STREAMING.md](VIDEO-STREAMING.md). The stream is implemented for device
   testing; see [VIDEO-USAGE.md](VIDEO-USAGE.md) for builds and launch commands.
3. **Tune and calibrate:** resolution/bitrate controls, keyframe recovery, audio
   offset assessment, and full input-plus-video stress testing. Keep the original
   30-60 ms capture-to-display range as an unverified engineering target.

At each stage, reuse the input stress baseline; reject video settings that cause
input queues to grow. Audio streaming, replacing iproxy, and a full settings UI
remain later work unless measurements reveal a direct dependency.

## Planned change sequence

1. Diagnostics, quiet dry-run, and replay harness (establish cause).
2. Measured Windows worker/logging/lock fixes (keep protocol v1 where possible).
3. iOS scheduling/drawing and overload-policy changes only as evidence requires.
4. Hardware regression report and input revision acceptance.
5. Window capture proof (complete), then the first 60 fps video stream;
   combined acceptance follows the input gate.

The plan intentionally defers scheduling changes until the diagnostic baseline.

## Implementation progress

The first Step 1 instrumentation slice is now implemented. The host has
`--diagnostics` JSON-line summaries, `--quiet` dry-run, run metadata, categorized
disconnect reasons, receive/sink/serial queue/write timing, and high-water marks.
The simulator now provides named repeatable workloads at configurable rates and
durations. The iOS app records the original `UIEvent` timestamp before dispatch,
callback/contact/change counts, transport dispatch delay, pending-write depth and
age, and send-completion delay in once-per-second `BCCA_DIAG` logs. Protocol v1
framing is unchanged.

The paired device/game-PC baseline now supplies build/device/GPU/iproxy metadata
and localizes the reproduced overflow sufficiently to select Step 2. Opt-in
bounded detailed traces remain conditional on unexplained issues; do not delay
the measured scheduling fix to add unrelated instrumentation.

Step 2 is implemented in `0.2.0-serial-worker`: a dedicated signalled I/O owner,
no per-transition sleep, bounded command-service batches (16 transitions or
2 ms, whichever comes first), COM writes outside the producer lock, and reset
generations that invalidate already-dequeued old work. The FIFO remains 64;
only exact duplicate snapshots are suppressed. Startup responses, mapping,
115200 setting, keepalive, iOS boundary expansion, and protocol v1 are unchanged.
Shutdown attempts release independently on both ports if one has failed.

Diagnostics now include `receive_to_serial_ms`, per-port write durations,
driver-buffer high-water marks, and worker iteration intervals. The receive-to-
serial metric excludes heartbeats/synthetic releases and requires both boards
to be scanning. It ends when both `Write` calls return, not at game consumption.
Worker intervals include intentional idle waits; they are not per-frame latency.

Automated build/regressions pass; Step 4 hardware acceptance is **pending**.
Next: replace only the Windows host, repeat the same ten-finger stress for at
least 60 seconds, press/release/repress, and 30-minute play in one logged game
launch. Also check held-touch resets and game restart. No IPA update is needed
for this host-only fix. Do not add lossy input scheduling or accept combined
gameplay video until this retest; standalone capture development is covered by
the separate local/Mercury gates in [WINDOW-CAPTURE.md](WINDOW-CAPTURE.md).

User update 2026-10-02: a brief pressure test with the revised serial worker
reported no overflow; no log/full-duration test yet, so acceptance stays pending.

Additional user-requested option: `--hook` and `--leds` are implemented for the
provided segatools MercuryIO 1.0 ABI. Keep serial as the playable fallback and
test the hook separately before treating it as lower latency. Hook touch retains
a bounded transition FIFO and reset generations; LEDs have separate IPC/USB
queues and require an updated IPA for display. A configurable callback cap and
the fork's downstream UART buffer limits still require game-visible edge tests.
See [HOOK-IO.md](HOOK-IO.md). This does not unblock video acceptance by itself.

Window capture implementation, 2026-10-02: the standalone Windows library,
preview, and D3D11 fixture are implemented independently of the input host.
Explicit HWND/Mercury-title selection, measured client/DPI crop, bounded owned
textures, GPU aspect-fit/guide rendering, resize/minimize recovery, and bounded
device-loss retries are available. Calibration can use toucca's placement
reference or the user's reported black boot exterior; suggestions require
guide review and explicit profile saving. See [CAPTURE-USAGE.md](CAPTURE-USAGE.md)
and [VALIDATION.md](VALIDATION.md).

Window capture acceptance, 2026-10-02: the user reports perfect alignment using
boot-black circle detection and no perceptible preview latency on real Mercury.
Reviewed `log/capture.log` and the confirmed `log/capture-profile.json`, which
matches the three agreed detections for a `1080x1920`, `144 DPI` client on the
RX 6650 XT. Milestone 2 is complete. H.264/USB/iOS video is the next implementation
milestone; combined input/video acceptance remains a separate integration gate.

Reference: Microsoft's [Thread.Sleep documentation](https://learn.microsoft.com/en-us/dotnet/api/system.threading.thread.sleep)
explains that the requested timeout depends on clock resolution; it is not a
precise scheduling contract. Existing capture/codec references remain in PLAN.md.
