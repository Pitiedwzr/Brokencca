# Brokencca implementation plan

Recorded 2026-09-30. User-approved target: **60 fps**. Windows: C#/.NET;
iOS: Objective-C. Xcode compilation is delegated to GitHub Actions on macOS.

Current status: the user has validated installation, USB/multitouch, serial game
communication, and actual gameplay. Milestone 1 is accepted as a prototype on
the tested setup. Load-dependent latency and overload recovery are now the
priority. See [NEXT-STEPS.md](NEXT-STEPS.md) for the immediate execution plan and
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

Windows initiates both sockets even though it hosts the game. iOS accepts
connections. This is usbmux over USB, not USB tethering, WebUSB, or a USB HID
emulator. Unmodified Brokenithm applications are not protocol-compatible.

## Architecture

```text
iOS UIKit multitouch -> 30-byte snapshots -> control socket over usbmux
    -> Windows InputSession -> immutable state FIFO -> WACCA serial output

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

## Milestone 2: window capture proof (after input stabilization)

Expose/find the Mercury HWND (and allow explicit window selection). Replace
toucca's heuristic window offsets with measured client bounds, DPI awareness,
and a configurable crop. Capture the game window, not the transparent controller
overlay. Start with borderless/windowed mode and test fullscreen separately.

Use Windows Graphics Capture `CreateForWindow` and `CreateFreeThreaded` with
D3D11 textures. Handle window closure, resize, minimized/occluded behavior,
GPU device loss, and frame pool recreation. Measure capture timing before
introducing USB/video. Keep CPU pixel copies out of the steady-state path.

## Milestone 3: H.264 stream at 60 fps

Explicitly select a hardware encoder via Media Foundation; verify actual use.
GPU-convert to NV12. Request low latency, no B-frames, no lookahead where supported,
and a small rate-control buffer. Start with SDR and a capped resolution around
720p/1080p (or equivalent square crop), preserving aspect ratio. Experiment with
10-20 Mbps, then tune to the hardware. Do not tie video capture to touch delivery.

Add a separate iOS listener, tentatively 24865, and negotiate its session ID
over the control connection. Define codec configuration (SPS/PPS), access-unit
framing, stream generation, timestamps, dimensions/crop, and keyframe requests.
Video must not reuse the control parser's 4096-byte limit.

Use VideoToolbox hardware decoding and Metal via CVMetalTextureCache. Bound
capture/encoder/network/decode/presentation queues. Drop old raw frames before
encoding and old decoded frames before display; do not drop arbitrary encoded
reference frames. On excessive encoded backlog, restart at a fresh keyframe.
Account for bytes already buffered in the reliable transport, which cannot be
replaced with a newer frame. Lower bitrate/resolution before input is affected.

Share a viewport/crop/rotation transform between display and touch mapping.
Reject letterbox touches as a separate viewport rule; do not silently change
the inherited radial mapping inside the content rectangle. Draw touch feedback
locally above the video.

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
and AMD RX6650 XT game PC. Capture resolution, video throughput, and end-to-end
touch-to-game/display latency remain unmeasured.

## References

- [libusbmuxd / iproxy](https://github.com/libimobiledevice/libusbmuxd)
- [iproxy arguments](https://github.com/libimobiledevice/libusbmuxd/blob/master/docs/iproxy.1)
- [WGC window capture](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)
- [Free-threaded capture pool](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded)
- [Hardware MFT enumeration](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mftenumex)
- [H.264 encoder controls](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder)
- [VideoToolbox](https://developer.apple.com/documentation/videotoolbox/vtdecompressionsession)
- [Core Video Metal texture cache](https://developer.apple.com/documentation/CoreVideo/cvmetaltexturecache-q3j)
