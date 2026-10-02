# Window capture implementation and validation plan

Reviewed 2026-10-02 against Brokencca `78944fb` and toucca `adc6b7a`.
Status: **complete; accepted on real Mercury on 2026-10-02**.
The user reports perfect boot-black circle alignment and no perceptible preview
latency. The supplied log and confirmed profile are reviewed in
[VALIDATION.md](VALIDATION.md). This development device
has no runnable Mercury. The deliverable is a Windows capture library and local
preview, with a reproducible surrogate test; encoding and iOS video are Milestone 3.

## Review conclusion and feasibility

The original Milestone 2 identifies the right APIs and failure cases, but is
not detailed enough to implement and accept: it omits window identity rules,
coordinate spaces, resource ownership, overload behavior, test fixtures, and
the boundary between local proof and game acceptance. This document supplies them.

Toucca can inform discovery and the intended controller geometry, but cannot
supply a capture pipeline. Its relevant behavior is:

| Source in the sibling toucca checkout | Observed behavior | Decision for Brokencca |
| --- | --- | --- |
| `MercuryHelper.cs`, `TryLocateMecury` | `FindWindow(null, "Mercury  ")`, including two trailing spaces | Preserve as an automatic-discovery hint; expose the HWND and allow explicit selection |
| Same method | Uses `GetWindowRect`, replaces only left/top with `ClientToScreen(0,0)`, then subtracts 10 from width/height and multiplies height by 0.938 | Do not treat this mixed rectangle or these constants as a measured client/video crop |
| `MainWindow.xaml.cs`, `AutoLocateMercury` | Polls every 300 ms, moves/resizes its own overlay, reloads WebView2 | Reuse the idea of tracking the selected window; no overlay movement or WebView reload is needed for capture |
| `MainWindow.xaml` | Borderless transparent window titled `Toucca` | Select the game's HWND, not this overlay |
| `web/controller.js` | Circle centered in the overlay, radius half the smaller dimension, four rings and 240 zones | Preserve existing input semantics; calibrate the video playfield separately |

User-provided calibration evidence (2026-10-02): after toucca places its overlay,
the overlay's outermost circle approximately matches the outermost circle of
Mercury's circular screen. Treat the placed overlay as a useful initial estimate
of playfield center and radius. This is approximate visual evidence, not a
measured pixel crop or proof that the window client area is the playfield.

There is no WGC, D3D capture, encoder, or video transport in toucca. Its title
lookup does not depend on game internals, so a fixture with the same title can
exercise discovery. An independently selected animated HWND can exercise WGC.
Neither proves Mercury's rendering mode, exact playfield crop, or game-PC timing.

**Feasibility:** finish the implementation and local capture proof without
Mercury, using toucca's discovery behavior plus a new capture backend and fixture.
The game-PC test now completes Mercury acceptance for the tested setup. Local
capture was developed while the existing input hardware retest was pending;
enabling combined video for
gameplay remains gated on that retest and capture-on/off input measurements.

## Deliverables and implementation order

The projects and command options below are implemented. Actual run/build
instructions are in [CAPTURE-USAGE.md](CAPTURE-USAGE.md). Test coverage and remaining
hardware combinations are recorded separately in [VALIDATION.md](VALIDATION.md).

1. **Interop/build spike:** add `src/Brokencca.Capture.Windows` and
   `tools/Brokencca.CapturePreview`. Use a Windows-specific .NET 8 target
   (`net8.0-windows10.0.19041.0`) in those projects only, Windows x64 packaging,
   and a PerMonitorV2 application manifest. Keep Core, Host, and existing tests
   on their current targets. Use C#/WinRT Windows SDK projections, a small
   `IGraphicsCaptureItemInterop`/D3D surface interop layer, and Vortice D3D11/DXGI
   bindings; pin exact compatible package versions after a successful Release
   build. Use a small WinForms window hosting a D3D11 swap chain for preview.
   Acceptance of this slice: create an item for a supplied HWND, obtain a D3D11
   texture, present it, and dispose everything. Record dependencies and restore
   requirements; resolve projection/COM ownership problems before more features.
2. **Discovery and geometry:** implement `WindowLocator`, `WindowGeometry`,
   `CaptureOptions`, and a platform-neutral viewport transform in Core. Add the
   fixture below. Prove client-to-texture alignment before assuming a crop.
3. **Capture and preview:** implement `WgcCaptureSession`, a bounded owned-texture
   pool, GPU crop/scale, preview rendering, and diagnostics. Keep the library
   independent of preview UI and the input host, ready for an encoder consumer.
4. **Lifecycle and regression:** cover resize/DPI changes, minimize/restore,
   closure/reselection, slow consumer, disposal races, and simulated device loss.
   Add deterministic tests to the existing regression runner for pure geometry
   and state rules; put interactive WGC checks in a separate smoke runner.
5. **Package and report:** add the projects to `Brokencca.sln`, build/publish
   scripts, and Windows CI. Package preview and fixture separately from the host.
   Record local results and limitations in `VALIDATION.md`; provide a game-PC
   checklist and usable commands. Do not mark Mercury compatibility passed from
   fixture results. No iOS build or protocol change is needed for this milestone.

The API baseline chosen for this first package is Windows 10 build 19041 or
newer, subject to `GraphicsCaptureSession.IsSupported()` at runtime. The HWND
capture API itself was introduced in build 18362; see
[CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow).
An unsupported environment must produce an actionable status, not a desktop
capture fallback. Start in SDR with HDR disabled on the test display; HDR/tone
mapping and exclusive fullscreen are later compatibility checks.

## Window selection contract

- `--list-windows` lists top-level candidates with HWND, PID, process name where
  accessible, escaped title, class, client dimensions, DPI, and minimized state.
  Exclude this process's preview windows and known controller overlays from
  automatic selection. Do not require elevation just to read process details.
- `--hwnd 0x...` explicitly selects a live top-level window. `--mercury` selects
  the unique eligible exact-title `Mercury  ` candidate. These modes are mutually
  exclusive. Zero matches reports waiting; multiple matches requires an explicit
  HWND. Do not invent a required Mercury executable name from the title.
- Capture only that window through `CreateForWindow`. Record HWND/PID/process
  start time when available and revalidate identity when state changes. Never
  reuse a stale numeric HWND as authority to capture an unrelated replacement.
- A closed explicit HWND ends that selection. Mercury discovery can offer a
  new candidate after restart, but requires selection again before capturing a
  replacement. Poll discovery at 300 ms while waiting; no busy loop.
- Selection must not activate, resize, reposition, inject into, or attach the
  IO hook to the source. Preview closure stops only the capture application.

Example usage (see the usage guide for package subdirectories):

```powershell
.\Brokencca.CaptureFixture.exe --title "Mercury  " --size 1280x720 --fps 60
.\Brokencca.CapturePreview.exe --list-windows
.\Brokencca.CapturePreview.exe --mercury --crop client --fps 60 --diagnostics
.\Brokencca.CapturePreview.exe --hwnd 0x123456 --crop normalized:0.1,0.1,0.8,0.8 --fps 60
```

## Geometry and crop contract

Use half-open pixel rectangles and name every coordinate space explicitly:
screen physical pixels, client pixels, capture texture pixels, normalized crop,
and preview content pixels. PerMonitorV2 awareness must be active before window
creation and geometry calls. Never apply a second DPI scale to physical pixels.

1. Obtain client width/height using `GetClientRect`; map both corners to screen
   coordinates using `ClientToScreen`. Measure outer and DWM extended frame
   bounds separately for diagnostics. `GetWindowRect` may include invisible
   resize borders and is DPI-virtualized; DWM bounds have different DPI behavior.
   See [GetWindowRect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect),
   [GetClientRect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclientrect),
   and [ClientToScreen](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clienttoscreen).
2. Establish the capture texture's screen-space origin independently. Use DWM
   visible bounds as the initial hypothesis, compare with item/frame sizes, and
   verify using the fixture's one-pixel edges in decorated, maximized, and
   borderless modes. WGC frame dimensions alone do not prove the origin. Keep
   this conversion in one adapter with fixtures for each supported case. If the
   measured relationship is inconsistent, report geometry-unresolved and show
   an uncropped diagnostic view; do not silently apply guessed border offsets.
3. Translate the measured client rectangle into texture coordinates. Intersect
   with the current frame's `ContentSize` and texture extent. Skip a transient
   inconsistent resize frame and rebuild geometry; do not stretch clipped data
   to look like a complete client image. Empty/zero-size regions pause capture.
4. Default `--crop client` retains the full client image. An optional normalized
   `x,y,width,height` rectangle is relative to that client area, finite, positive,
   and wholly inside [0,1]. Invalid configuration is rejected. Round left/top
   down and right/bottom up, then clamp to valid pixel bounds. Preserve normalized
   intent across resize. Provide full-item view for border diagnostics.
5. Fit the crop into the preview with aspect ratio preserved and letterboxing.
   Export source size, effective crop, rotation (initially 0), content rectangle,
   and a geometry generation. Unit-test forward/inverse transforms and letterbox
   rejection for later iOS use. Do not change touch protocol or radial mapping.
6. The game playfield may occupy less than the client image. Use the user's
   observed outer-ring match as the initial calibration anchor. Keep three
   separate values: measured window client bounds, chosen video crop, and
   playfield circle. Toucca's 0.938 is useful evidence about overlay placement;
   it does not establish a universal video-crop ratio. Do not choose a square
   crop or discard HUD content automatically.

### Toucca-based initial playfield calibration

Prefer measuring the actual placed overlay's client rectangle, if toucca is
already running on the game PC. Its web canvas fills that rectangle, with center
at half width/height and outer radius `min(width,height)/2`. Read these bounds
passively; do not launch a second serial controller just for calibration.

Without a live overlay, offer an explicitly named `toucca-reference` calibration
estimate derived from the inspected helper. Let `(Cx,Cy)` be Mercury's client
origin in screen coordinates and `(R,B)` its outer window right/bottom from the
same DPI-aware measurement. Toucca requests:

```text
overlayWidth  = trunc(R - Cx - 10)
overlayHeight = trunc((B - Cy - 10) * 0.938)
centerScreen  = (Cx + overlayWidth/2, Cy + overlayHeight/2)
radiusPixels  = min(overlayWidth, overlayHeight)/2
```

The truncation reflects toucca's integer `SetWindowPos` arguments. This is a
reference estimate, not an assertion that another process's DPI behavior and
WebView layout produce identical physical pixels. Reject nonpositive dimensions.
Subtract the measured client origin and store normalized center `(cx/W,cy/H)`
and radius `r/min(W,H)` with source dimensions, DPI, and provenance
(`toucca-reference` or `measured`). Revalidate if aspect ratio changes.

Show this circle as a toggleable guide over the captured preview. On Mercury,
check center plus top/right/bottom/left circumference points, then adjust center
and radius and save a named profile. The user reports an approximate match, so
do not mark this calibration exact before measurement. The fixture must contain
an offset circle in a nonsquare client area to test that calibration is not
silently recentered on the window or crop.

Apply the same crop-and-scale transform to the video and calibration guide.
For Milestone 3, use the resulting display circle to place the iOS input rings
and map touches while retaining the existing 240-zone ordering and radial
semantics. If a deliberate crop clips the calibrated ring, show that conflict
and require an adjusted crop/profile rather than stretching the circle to fit.

Recompute geometry on resize, DPI/monitor/style changes, and restore. A generation
change invalidates pending old-sized textures. Future NV12 conversion will need
even output dimensions; do not shift the visible crop now just to impose that.

### Boot-black calibration aid

The user reports that during boot the area outside Mercury's circular display
is black. The implemented optional detector samples the measured client image,
thresholds near-black pixels, selects the largest connected lit component, and
fits its row/column silhouette to a circle. It rejects small or clipped shapes,
ellipses/rectangles, unsupported partial arcs, and nonblack surroundings. A
continuous large outer ring can define a circle even if its interior is dark.
Three consistent observations are required. A dark frame interrupts consensus;
the algorithm never infers a circle from all-black pixels or automatically
changes crop/input geometry. Sparse boot content may be insufficient, in which
case the toucca reference/manual guide remains available.

Sampling is opt-in (`--calibrate-black` or C), uses explicit diagnostic CPU
readback, and is kept outside steady-state performance runs. The result is a
visible suggestion with provenance `black-background`; the user checks/adjusts
the guide and presses S to confirm and save it. Record source aspect ratio and
DPI, and require recalibration after an aspect-ratio change.

## GPU path, scheduling, and ownership

- Create one hardware D3D11 device with BGRA support and record adapter identity.
  Wrap it as a WinRT D3D device. Create a BGRA8 free-threaded WGC pool with two
  buffers. Keep the normal system capture indicator; disable cursor capture
  where supported. No border-suppression permission is required by this plan.
- `FrameArrived` runs on the pool's internal worker, independent of a UI
  dispatcher. Keep the callback short and do not invoke UI synchronously; see
  [CreateFreeThreaded](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded?view=winrt-26100).
- Drain available frames and retain only the newest useful frame. While its WGC
  lease is live, submit a GPU copy into an owned texture, then dispose the lease.
  Never hand a returned WGC surface to another asynchronous consumer. Use three
  reusable owned slots with explicit free/pending/in-use states: at most one
  pending latest frame and one consumer lease. Replace a pending old frame;
  drop incoming work if no safely reusable slot exists. Never grow a FIFO.
- Serialize all immediate-context commands, including copy/crop/present, on a
  GPU gate with nonblocking acquisition in the capture callback. A busy gate
  drops that frame. GPU completion queries govern slot reuse; never overwrite
  a slot with outstanding GPU use. The preview worker polls completion without
  holding the gate. Present with bounded swap-chain latency and without blocking
  capture/input; resource disposal uses the same ownership rules.
- GPU shader crop/scale feeds the preview swap chain. No steady-state
  `Bitmap`, `WriteableBitmap`, JPEG, staging readback, or CPU pixel conversion.
  Optional one-shot PNG/pixel validation may explicitly read back a texture
  outside timing runs. NV12 conversion belongs to the encoder milestone.
- Cap submitted/presented frames at 60/s with monotonic deadlines; do not build
  delay by sleeping in `FrameArrived`. WGC is source-driven: static windows and
  slow producers may yield fewer unique frames. Count arrivals, dropped frames,
  and presentations separately; duplicates do not prove 60 fps capture.
- On systems exposing `IGraphicsCaptureSession5`, set the optional
  `MinUpdateInterval` to zero and apply our own cap; older Windows versions retain
  the supported WGC behavior. The ABI bridge avoids raising the Windows 10
  projection target. This was needed to remove an observed capture cadence limit
  on the development device; see Microsoft's
  [property documentation](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.minupdateinterval?view=winrt-26100).
  If a callback skips a busy GPU gate, the consumer also drains the latest WGC
  frame so a full two-buffer pool cannot remain stalled.
- The frame-consumer contract carries frame ID, QPC timestamp, dimensions,
  crop/geometry generation, session generation, and a disposable texture lease.
  Consumers must return leases; they cannot retain a bare texture indefinitely.
  Keep capture locks, errors, diagnostics, and queues independent of input/LEDs.

Microsoft documents frame lifetime, `ContentSize`, QPC timestamps, and frame-pool
recreation in its [capture guide](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture).
Use that lifetime contract even if a retained COM reference appears to work.

## Lifecycle and observable failures

Use explicit states: WaitingForWindow, Starting, Running, Paused, Recreating,
Stopped, and Faulted. Status transitions carry a reason and session generation.

| Event | Required behavior |
| --- | --- |
| Source resize | Dispose acquired frames, invalidate pending generation, quiesce GPU use, recreate pool/owned textures at the new nonzero size, recompute crop, resume |
| Minimize/zero size | Pause and label the last image stale; do not claim live 60 fps or allocate zero-sized textures; resume on restore |
| Occlusion/static content/no arrivals | Track last-frame age; show a stale indicator after 500 ms without a new frame, without calling this alone a fatal error; test occlusion using moving fixture content |
| Item closed/process exited | Stop session and release resources; explicit selection ends; return Mercury mode to discovery without capturing a replacement automatically |
| Device removed/reset | Record HRESULT and device reason, stop callbacks, invalidate leases, rebuild device/pool/preview; at most three recovery attempts with 250/500/1000 ms backoff, then a visible fault requiring retry |
| Unsupported/denied/protected target | Report API error or observed blank output; do not claim all-black pixels prove protection, and do not silently capture a monitor |
| Slow/closed preview | Replace/drop pending work and release leases; closure cancels session; capture resources must not wait on the input worker |
| Stop during callback/recreate | Cancel new work, detach handlers, quiesce callbacks and consumer/GPU use, dispose session/pool/textures/device exactly once; no callback after completed stop |

Simulate device-loss and stale-callback races through an injectable backend;
label these simulated unless an actual device reset is also tested. Keep a
bounded teardown timeout with diagnostics; never dispose resources still in use
just to meet the timeout.

## Local proof without Mercury

Add `tools/Brokencca.CaptureFixture`: a separate process with its own D3D11
animated window. Default title is `Brokencca Capture Fixture`; `--title` can set
the exact Mercury title. Render corner color markers, one-pixel edge lines,
asymmetric orientation labels, a moving bar, a frame counter, and a synthetic
four-ring playfield. Log actual presents/QPC and dimensions. Provide controls
for decorated/borderless mode, resize, minimize/restore, title change, and close.
Do not launch toucca's serial worker or require COM ports for this fixture.

The interactive smoke runner should launch and own the fixture process, select
its reported HWND, test first-frame/crop pixels with explicit readback, and exit
with an error on failed assertions. It must never close arbitrary user windows.

| Check | Evidence/acceptance |
| --- | --- |
| Discovery | Exact title including trailing spaces, no match, duplicate titles, explicit HWND, overlay exclusion, source restart and stale HWND handling |
| Crop/transform | All four edge markers within one source pixel; no titlebar in client crop; pure tests for negative screen coordinates, crop rounding/invalid values, and forward/inverse transforms |
| DPI/style | 100/125/150/200% where available; decorated, maximized, borderless; move between different-DPI monitors and negative-coordinate desktop positions where available; unavailable combinations recorded as untested |
| Continuous capture | 1280x720 SDR fixture at measured 60 fps for 10 minutes, 60 Hz or faster display, preview at 60; record unique-frame throughput, frame gaps, and losses against source presents |
| Provisional performance gate | At least 59 unique captured frames/s averaged over the steady run when the source sustains 60; capture-QPC-to-present-submit p95 <= 33.3 ms; at most one pending owned frame, no growing age/memory trend; record failures rather than waive them |
| Slow consumer | Inject a 250 ms pause, confirm bounded slots/drops and recovery to recent content without replaying the backlog; no stale-generation presentation |
| Lifecycle | 20 resize/minimize/restore cycles, 20 start/stop cycles, occlusion by another moving window, source exit/restart, close during callback, injected device loss; no crash/deadlock or accumulating resources |
| Input isolation | Run existing simulator replay and diagnostic host in quiet dry-run mode with preview off/on; ordered input regressions still pass and supported-rate replay has zero overflow/reconnect; separately retain serial/hook game-PC timing gates |

Emit once-per-second JSON summaries: build/OS/adapter/driver metadata, source
identity/DPI/bounds, crop/generations, actual source/capture/preview rates, drop
reasons, outstanding slots, frame-age and callback-duration distributions,
recreate/failure counts, CPU and memory. Compare matched capture-off/on runs.
GPU usage and VRAM may require a separate profiler. Present-submit time is not
visible-display latency; do not label it capture-to-photon or iOS latency.

CI builds the new Windows projects and runs deterministic geometry/lifecycle
tests. Interactive GPU smoke/performance checks require an unlocked desktop and
working graphics driver; missing capability must be recorded as skipped, never
passed. Hosted CI success alone cannot accept WGC behavior or performance.

## Mercury and next-milestone acceptance

Milestone 2 is accepted: the user's real-Mercury test confirms boot-black circle
alignment and a responsive preview, supported by the saved log/profile. The
checklist below remains the broader compatibility and combined input/video
validation procedure; untested combinations are recorded in `VALIDATION.md`
and do not reopen the accepted capture milestone.

For further game-machine validation:

1. Start Mercury windowed/borderless, list/select the real HWND, record escaped
   title/class/process and geometry. Verify client capture, colors, all four
   edges, overlay exclusion, and the calibrated playfield circle/crop. Use the
   reported toucca outer-ring alignment as the initial reference, then verify
   its center and four circumference points. Keep exclusive
   fullscreen separately marked supported/unsupported after testing it.
2. Repeat resize, minimize/restore, Alt-Tab, occlusion, source restart, and any
   available mixed-DPI/multi-GPU conditions on the AMD RX6650 XT setup.
3. Repeat the existing 60-second touch stress and 30-minute gameplay with capture
   off/on. Require no new overflows, stuck/lost transitions or growing queue age;
   retain the input plan's provisional p95/p99 5/10 ms serial handoff targets.
   Measure hook mode separately if used; do not infer game-visible edges from
   dry-run success. This also depends on the outstanding input hardware gate.
4. Record actual Mercury frame delivery and capture timing, crop profile,
   screenshots, logs, and limitations in `VALIDATION.md`. The supplied capture
   log/profile and user acceptance are recorded there for the 2026-10-02 test;
   keep additional performance measurements separate from visual acceptance.

Milestone 3 starts from the owned GPU texture/metadata contract. It still needs
hardware encoder selection, NV12 conversion, codec/network framing, bounded
encoded backpressure, and iOS decoding/display. Neither toucca nor this preview
finishes those steps. A working surrogate permits that engineering to proceed,
but cannot establish final game/iPad compatibility or end-to-end latency.
