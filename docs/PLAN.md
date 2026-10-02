# Brokencca implementation plan

Recorded 2026-09-30. User-approved target: **60 fps**. Windows: C#/.NET;
iOS: Objective-C. Xcode compilation is delegated to GitHub Actions on macOS.

Current status: the user has validated installation, USB/multitouch, serial game
communication, and actual gameplay. Milestone 1 is accepted as a prototype on
the tested setup. Load-dependent latency and overload recovery are now the
priority for combined gameplay/video validation. Milestone 2 window capture is
complete and accepted on the user's Mercury setup (2026-10-02); hardware H.264
and USB/iOS video are the next implementation milestone.
See [NEXT-STEPS.md](NEXT-STEPS.md) for the immediate execution plan and
[VALIDATION.md](VALIDATION.md) for the reported evidence.

## Goal

Turn an iPhone/iPad into a wired WACCA touch controller, then display the
Windows game window on that device with low and consistent latency. Preserve
toucca's existing 240-zone game input behavior and serial integration.

## Source assessment

Inspected local revisions:

- toucca `adc6b7a`: .NET 8 WPF/WebView2; a localhost WebSocket receives 30-byte
  touch bitmaps; `Area2Area` swaps the two 120-zone halves; `SerialManager`
  emulates the boards on COM5/COM6 at 115200 baud.
- Brokenithm-iOS `master` `1af0d2e`: Objective-C controller; iOS listens on
  port 24864 and sends CHUNITHM-specific input, not WACCA input.
- Brokenithm-iOS `origin/win-client` `d13b959`: .NET Framework 4.7.2;
  `iMobileDevice-net` opens the iOS port through usbmux. The host writes game
  input to shared memory and sends LED state back. Neither branch has video.
- User's segatools `8a966b2`: MercuryIO API 1.0, 240 bool-cell callback and
  by-value `led_data` (DWORD plus 480 RGBA entries); Elizabeth forwards LEDs.
  Touch callbacks still feed 520-byte emulated UART buffers inside the game.
- WACVR `c982169`: selectable serial/shared-memory touch and 480-entry LED
  capture. Its sector-major LED ordering is used for the experimental iOS view.

Windows initiates both sockets even though it hosts the game. iOS accepts
connections. This is usbmux over USB, not USB tethering, WebUSB, or a USB HID
emulator. Unmodified Brokenithm applications are not protocol-compatible.

## Architecture

```text
iOS UIKit multitouch -> 30-byte snapshots -> control socket over usbmux
    -> Windows InputSession -> immutable state FIFO
       -> external serial output OR shared-memory MercuryIO DLL

Game LED callback -> separate LED IPC -> host -> USB/LED 24866 -> iOS ring LEDs

Windows game HWND -> Windows Graphics Capture -> D3D11 crop/scale/NV12
    -> hardware H.264 -> separate video socket over usbmux
    -> iOS VideoToolbox -> Metal -> touch overlay
```

Control and video have separate sockets, processing queues, and backpressure.
They still share USB bandwidth; separate sockets are not a latency guarantee.
The first host is a console application; WPF settings/window selection can
be added when capture is introduced. Core protocol and mapping have no UI or
serial-device dependency.

## Milestone 1: wired input prototype (initial implementation)

Implemented in this repository:

- Versioned, little-endian control framing with exact-length reads, bounded
  payload sizes, layout handshake, sequence checks, and immutable snapshots.
- Native iOS touch surface with four rings and 60 angular sectors; immediate
  local feedback; begin/move/end/cancel handling; reset on rotation/background.
- iOS Network.framework loopback TCP listener on 24864. This is newly written
  transport code using Brokenithm's connection model, without copying its app.
- Host connects to a local `iproxy` endpoint. Optional `--iproxy`/`--udid`
  launches and supervises the external bridge in USB-only mode.
- Full-state heartbeat every 100 ms; host disconnects and resets after 500 ms
  without a complete valid state/reset packet. Handshake has a 3-second deadline.
- Single serial writer, bounded transition FIFO, atomic packet construction,
  independent scan readiness for both boards, startup response constants from
  toucca, and queue clearing on reset/disconnect. Overflow fails the session
  and releases input instead of allowing growing delay.
- Dry-run host, simulated iOS peer, regression tests, and CI definitions.

The user reports successful real-device gameplay and accepts this as a prototype.
Full checklist coverage and quantitative latency remain pending; rapid movement,
more contacts, and rapid taps expose delay that needs improvement before video.

### Deliberate compatibility decisions

- Keep frontend zone ordering, half swap, bit order, serial side naming, and
  toucca's startup responses. `COM5` is paired to game `COM3`; `COM6` to `COM4`.
- Keep centre-line exclusion and acceptance of inner/outer radius touches.
  The displayed rings do not imply that the centre is an input dead zone.
- Clamp angular indices to 0..29 to prevent floating-point boundary overflow.
- Counter is wrapped to 0..127 **before** computing the checksum. toucca's
  mutation after checksum construction could produce a mismatched packet at wrap.
- No general Windows touch injection or CHUNITHM shared-memory integration.

### Known serial compatibility limit

toucca and its WACVR reference parse serial request bursts without a formal
packet-length parser. This prototype collects raw binary bytes until a 3 ms
idle gap, with an additional wait for the minimum known read-command prefix.
This improves on text decoding but is **not** a complete serial framing solution:
coalesced requests or long inter-byte gaps can still be misinterpreted. Basic
game compatibility is now user-validated. For broader robustness, capture startup
transactions, verify boundaries, and replace this adapter with a length/state
parser if needed. Serial write
completion also does not prove the game has sampled every very short tap.

### Acceptance checklist

1. Install/sign iOS app; unlock/trust device; verify Windows Apple device service.
2. Start with `--dry-run`, then touch all rings, both halves, simultaneous contacts,
   fast taps and boundary slides; verify cancellation and rotation release state.
3. Verify only the chosen USB device connects; test cable removal and app suspension.
4. Configure com0com, start serial host before game, and validate startup/scan on
   both ports. Check all 240 zones in the game's input test screen.
5. Run sustained play, reconnect, game restart, rapid down/up, and queue overload tests.
6. Measure callback-to-serial-handoff median/p95/p99. Goal: p95 below 10 ms;
   not yet measured and excludes touchscreen sampling and game polling.

## Milestone 1.1: input latency under load (next)

Deliver a diagnostic build, reproduce the reported ten-contact/rapid-tap load,
and measure each stage before changing input semantics. Remove confirmed host
scheduling/logging bottlenecks, keep serial writes outside the producer queue
lock with reset-order guarantees, and bound queue age as well as capacity.
Preserve meaningful zone transitions, short taps, releases, and simultaneous
contacts. Do not tie input transmission to the 60 fps video target or simply
increase the queue limit. Re-test on the user's game machine before moving on.

Detailed deliverables, overload decisions, and acceptance criteria are in
[NEXT-STEPS.md](NEXT-STEPS.md).

The diagnostic build and physical serial/iOS baseline are available. Six
reproduced host FIFO overflows, with sub-millisecond writes during their windows,
justify the Step 2 host-worker revision now implemented: signalled dedicated
worker, bounded draining, writes outside the producer lock, generation-safe
reset, and per-port/receive-to-completion diagnostics. The 64-entry FIFO and
input semantics are unchanged. Hardware before/after acceptance remains pending;
see [VALIDATION.md](VALIDATION.md) for evidence and retest instructions.

At the user's request, an optional MercuryIO backend and independent LED return
path are now implemented. C# host/Objective-C input remain; the small native IO
DLL matches only Mercury's API and does not modify segatools. The 64-entry FIFO,
reset generations, native host-death watchdog, and separate latest-frame LED
socket avoid copying a lossy latest-state input slot. The default 240/s callback
cap is experimental because downstream game buffers/polling remain finite.
Actual hook/LED hardware acceptance and iOS compilation are pending. See
[HOOK-IO.md](HOOK-IO.md) for configuration, scope, and a serial/hook comparison.

## Milestone 2: window capture proof (complete)

The implementation-ready scope, coordinate/resource contracts, test matrix, and
acceptance gates are in [WINDOW-CAPTURE.md](WINDOW-CAPTURE.md), reviewed against
the local toucca source on 2026-10-02. The standalone WGC/D3D11 library, preview,
and animated fixture are implemented; see [CAPTURE-USAGE.md](CAPTURE-USAGE.md)
for commands and [VALIDATION.md](VALIDATION.md) for local and real-Mercury evidence.

Accepted by the user on 2026-10-02 after testing real Mercury on the RX 6650 XT
game PC: boot-black calibration aligns perfectly and the preview has no
perceptible latency. The supplied capture log and confirmed profile establish
the tested `1080x1920` client at `144 DPI`. This closes the window-capture
milestone; quantitative latency and additional compatibility coverage are
recorded separately in the validation report.

This device has no runnable Mercury. The standalone library, local preview, and
animated fixture provide the local proof. Toucca supplies a
window-title discovery hint and overlay geometry reference, not capture code;
its `-10`/`0.938` adjustments are not a validated video crop. The user reports
that the placed overlay's outer ring approximately matches Mercury's outer
ring; preserve that as the initial playfield center/radius calibration reference.
The reported black boot background also supports an optional conservative
circle-fit suggestion, checked by the user before saving a profile.
The implementation uses explicit HWND selection, measured client/DPI bounds,
configurable crop, bounded GPU ownership, and lifecycle recovery, with CPU pixel
copies restricted to optional diagnostics/calibration.

Local fixture proof and real Mercury crop/rendering compatibility are complete
for the tested setup. Combined capture/input performance remains part of the
game-PC input/video acceptance gate. Exclusive fullscreen and other untested
rendering/hardware combinations remain additional compatibility coverage.

## Milestone 3: H.264 stream at 60 fps

The original outline was reviewed on 2026-10-02 and needed concrete protocol,
resource ownership, overload, geometry, and acceptance contracts. The completed
design is [VIDEO-STREAMING.md](VIDEO-STREAMING.md). The encoder, transport,
decoder, presentation, and touch transform are implemented for device testing;
see [VIDEO-USAGE.md](VIDEO-USAGE.md). Xcode compilation and device acceptance
remain pending on GitHub Actions and the user's setup.

Start with hardware Media Foundation H.264, GPU NV12 conversion, SDR BT.709,
60 fps, no B-frames, and 10 Mbps. Preserve the accepted full-client crop; its
1080x1920 portrait image initially scales to 720x1280. Higher-resolution and
explicit square-crop settings follow measured hardware capability.

Use video port 24865 with its own bounded parser and explicit control-v2 session
binding. Keep control v1 for input-only compatibility. The detailed plan fixes
SPS/PPS and access-unit representation, generation/IDR recovery, feedback,
per-stage queue and age limits, and socket replacement for stale TCP backlog.

Decode with VideoToolbox and present through Metal at the measured display
cadence. Share the calibrated crop/circle transform with a transparent local
touch/LED overlay, retaining the current expanded touch-boundary behavior.
Validate codec hardware use, color/geometry, unique-frame throughput, visible
latency, thermal stability, and input performance with video off/on. Fixture
results and signed-device/real-Mercury acceptance are separate gates.

## Milestone 4: measurement and packaging

- Engineering target: roughly 30-60 ms capture-to-visible-frame at 60 fps, not a
  prediction or hardware guarantee. No 120 fps requirement in the first release.
- Instrument capture, encode, receive, decode, and presentation with frame IDs,
  queue ages/depths, and monotonic timestamps. Cross-device time needs clock-offset
  estimation. Validate visible latency with a high-speed camera.
- Compare input latency with video off/on; test keyframe bursts, high GPU load,
  reconnects, and sustained device thermal load.
- Keep audio on the PC initially. Measure/calibrate visual/audio offset for the
  rhythm game. iOS audio streaming and synchronization require a separate design.
- Later integrate native usbmux discovery/bindings behind the host transport to
  remove the external iproxy prerequisite. Do not port old .NET Framework binary
  dependencies without checking current architecture/runtime compatibility.
- Add settings UI, encoder diagnostics, viewport calibration, and signed releases.

## Build and validation constraints

Windows can build with the .NET 8 SDK (or newer SDK targeting .NET 8). At initial
setup only runtimes were on PATH; a local SDK was installed under ignored
`.tools/dotnet` for verification. No machine-wide SDK/PATH change is required.
The inherited .NET 8 target is provisional; update it before a long-lived release
according to Microsoft's supported runtime policy.

GitHub Actions builds Windows and an **unsigned** iOS device artifact on macOS.
The iOS artifact requires external signing/provisioning before installation.
The workflow does not supply signing credentials or demonstrate device behavior.
XcodeGen generates the Xcode project from `ios/project.yml`; iOS 15 is the current
prototype deployment minimum. The tested setup is an iPad mini (5th generation)
and AMD RX6650 XT game PC. Local fixture capture has been measured on the
development laptop, and real Mercury capture has been accepted on the game PC
with a supplied diagnostic log/profile. Sustained unique-game-frame throughput
and end-to-end touch-to-game/display latency remain unmeasured.

## References

- [libusbmuxd / iproxy](https://github.com/libimobiledevice/libusbmuxd)
- [iproxy arguments](https://github.com/libimobiledevice/libusbmuxd/blob/master/docs/iproxy.1)
- [WGC window capture](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)
- [Free-threaded capture pool](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded)
- [Hardware MFT enumeration](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mftenumex)
- [H.264 encoder controls](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder)
- [VideoToolbox](https://developer.apple.com/documentation/videotoolbox/vtdecompressionsession)
- [Core Video Metal texture cache](https://developer.apple.com/documentation/CoreVideo/cvmetaltexturecache-q3j)
