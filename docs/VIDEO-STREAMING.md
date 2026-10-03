# 60 fps Windows-to-iOS video implementation plan

Reviewed 2026-10-02 against Brokencca `232e76a`. Updated 2026-10-03: **encoder,
control-v2/video transport, VideoToolbox decode, Metal presentation and calibrated
touch mapping implemented for device testing**. Windows build and synthetic AMD
hardware encoding are checked locally; Xcode compilation, USB/iOS throughput and
combined gameplay acceptance remain pending. See [VIDEO-USAGE.md](VIDEO-USAGE.md).
This completes Milestone 3 in [PLAN.md](PLAN.md). Window capture is accepted;
combined gameplay acceptance still requires the input retest in
[NEXT-STEPS.md](NEXT-STEPS.md) and the gates below.

## Review conclusion

The original plan chooses suitable APIs but is not detailed enough to implement
or accept. It leaves protocol compatibility, codec sample format, GPU ownership,
queue bounds, stale TCP recovery, calibrated touch mapping, and the meaning of
60 fps unspecified. This document makes those decisions. Numeric budgets below
are proposed implementation defaults and acceptance targets, not measurements.

Repository findings that affect the design:

| Existing implementation | Required integration work |
| --- | --- |
| `InputSession` and `BCTransport` accept only control v1, with a fixed four-byte HELLO | Explicitly version session binding; do not append video messages to v1 |
| `WgcCaptureSession` has three owned BGRA textures, one pending frame, and one active lease | Add a separate bounded NV12 pool; do not let an asynchronous encoder retain a reusable capture texture |
| `GpuDevice` creates the default adapter and serializes its immediate context with `Gate` | Add adapter identity/selection and verify MF device sharing and video-processing support |
| Host targets plain `net8.0`; capture targets Windows build 19041 | Update the Windows host target/manifest when integrating capture; keep portable Core/tests portable |
| `main.m` draws an opaque controller centered in the view | Compose Metal video beneath a transparent calibrated touch/LED overlay |
| `BCZonesForPoint` implements expanded boundary touches, including both halves at the vertical seam | Preserve this current behavior, not the older single-zone centre-line description |
| Accepted profile uses the full 1080x1920 client, with an offset calibrated circle | Preserve the full crop by default; do not silently replace it with a centered square |

## Scope and starting configuration

- Windows x64, Windows 10 build 19041+ and working WGC; C#/.NET with MF/D3D11.
  iOS 15+, Objective-C, UIKit, Network.framework, VideoToolbox, and Metal.
- First hardware pair: RX 6650 XT game PC and iPad mini 5. Record OS, GPU driver,
  device OS, USB cable/port, and iproxy versions with results. Other devices are
  compatibility extensions, not evidence for this pair.
- H.264/AVC, progressive SDR, 8-bit 4:2:0, square pixels, BT.709 primaries,
  transfer and matrix, limited-range NV12. HDR, audio streaming, HEVC, Wi-Fi,
  120 fps, and replacing iproxy remain outside this milestone.
- Request **60/1 fps**. Start at 10 Mbps target bitrate, two-second maximum GOP
  (120 pictures), zero B-frames, low latency, and no lookahead where supported.
  Start with H.264 Main profile, Level 4.2; allow constrained Baseline only after
  an explicit negotiated configuration. Validate actual SPS/profile/level and
  absence of reordering. A rejected setting must appear in diagnostics.
- Default output fits the confirmed crop in a 1280-pixel long-edge envelope,
  without upscaling. Thus full portrait 1080x1920 becomes **720x1280**; landscape
  1920x1080 becomes 1280x720. A square crop must be explicitly chosen and uses a
  960x960 default. Optional full-quality envelopes are 1920 long edge with at
  most 2,073,600 pixels, or 1080x1080 for square content, initially 15-20 Mbps.
- Choose even coded dimensions for NV12. Preserve exact crop aspect ratio with
  a signaled content rectangle inside the coded image if rounding adds padding;
  never distort the circle or shift the source crop to satisfy alignment.
  Include codec clean-aperture/cropping in the decoder-to-content transform.
- No silent CPU encoder/decoder or CPU pixel-conversion fallback. An unavailable
  hardware path disables video with a useful reason while input stays usable.
  Hardware support at these sizes is a spike result, not a product-name promise.

60 fps means delivery and presentation of distinct source updates when a verified
60 Hz moving source supplies them. Count WGC arrivals, accepted capture frames,
encoded AUs, decoded frames, and distinct presented IDs separately. Static
windows may produce fewer WGC updates; repeating an image is not a new game frame.
59.94 Hz sources are reported at their measured cadence, not as a 60 Hz failure.

## Pipeline, scheduling, and resource ownership

```text
WGC owned BGRA lease -> D3D11 crop/scale/BT.709 NV12 pool -> hardware H.264 MFT
  -> bounded access-unit FIFO -> TCP/iproxy 24865 -> bounded AU parser
  -> VideoToolbox -> latest decoded CVPixelBuffer -> Metal at display cadence
                                                       + local touch/LED overlay
control 24864: independent input session and heartbeat
LED 24866: independent existing optional stream
```

1. Reuse `CapturedFrameInfo`, `CaptureProfile`, and measured client/crop geometry.
   Streaming requires `GeometryResolved`; unlike preview, an unresolved frame
   must not silently become full-item gameplay video. Tag work with capture
   session, geometry, and video generations so late callbacks cannot resurrect
   an old image or transform.
2. Create capture/conversion/MF resources on the same selected adapter. Record
   adapter LUID, encoder CLSID/name, driver, and hardware attributes. The current
   default-adapter constructor needs an explicit adapter option and video-support
   capability checks. Unsupported cross-adapter transfer is a visible failure.
3. Use a D3D11 video processor for GPU crop/scale/BGRA-to-NV12. Check input/output
   format support and configure range/matrix explicitly. Verify black/white,
   near-black, gray ramp, and saturated colors on an encoded fixture. CPU
   readback remains diagnostic only, disabled during performance runs.
4. Hold `gpu.Gate` only while submitting immediate-context work. Release the
   BGRA lease after conversion commands are submitted, allowing its existing
   retirement query to protect the GPU read. MF receives a separate NV12 sample
   backed by `MFCreateDXGISurfaceBuffer`; configure `IMFDXGIDeviceManager` and
   `MFT_MESSAGE_SET_D3D_MANAGER` for a verified D3D11-aware MFT. Enable appropriate
   D3D multithread protection for MF's internal access. Do not hold the gate
   across MF waits/callbacks, socket I/O, or input processing. See Microsoft's
   [DXGI surface buffer API](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mfcreatedxgisurfacebuffer)
   and [D3D11 device-manager integration](https://learn.microsoft.com/en-us/windows/win32/medfound/supporting-direct3d-11-video-decoding-in-media-foundation)
   (the latter documents the decoder mechanism; encoder support must be probed).
5. Keep NV12 textures alive and immutable until both conversion GPU work and MF
   ownership have finished. Use tracked-sample release/allocator callbacks where
   supported; prove the selected MFT's ownership behavior in the spike. Returning
   from `ProcessInput` or receiving one output is not sufficient proof that an
   input surface is reusable. Bound outstanding ownership, including shutdown.
6. Implement an event-driven encoder worker: enumerate hardware video encoders
   with NV12 input/H.264 output, unlock asynchronous processing, negotiate media
   types, and service `METransformNeedInput`/`METransformHaveOutput`. Handle output
   allocation flags, stream changes, flush, drain, shutdown, and cancellation.
   Do not use a software synchronous polling loop for a hardware MFT. See
   [hardware enumeration](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mftenumex)
   and [asynchronous MFT contract](https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts).
7. Probe `ICodecAPI` support before setting low latency, mean/max bitrate, GOP,
   B-frame count, buffer size, and force-keyframe controls. Request about 100 ms
   rate-control buffering when supported; report actual supported values/units.
   Confirm behavior from output timing and bitstream, since a successful property
   call is not latency evidence. Microsoft lists relevant controls in its
   [H.264 encoder documentation](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder);
   vendor MFT support is a separate test.
8. Set MF sample time/duration in 100 ns units from monotonic source timing; use
   rational arithmetic for 60 fps, not a permanently truncated 16 ms timer.
   Preserve timestamp gaps when raw frames are dropped. Correlate output PTS to
   the original frame ID/capture timestamp with a bounded map; fail/recover on
   unresolvable output. DTS equals PTS for the required non-reordered stream.
9. Run capture/encode, network send, iOS receive/decode, and presentation on
   separate owners/queues. Input keeps its immediate event-driven path and
   existing FIFO semantics. Never wait for video from a touch callback or hold
   an input lock while doing video work.

## Session binding and compatibility

Retain control **v1** for input-only mode. Add explicit **control v2** for video
mode, retaining the existing 24-byte `BCCA` header, endian order, sequence rules,
4096-byte cap, and TOUCH/RESET layouts. Only HELLO changes: exactly 24 bytes,
`u16 zones=240, u16 bitmapBytes=30, u32 capabilities, byte[16] sessionToken`.
Capability bit 0 means video v1; all other bits are zero for this revision.
Windows generates a fresh random 128-bit token per control connection and iOS
echoes it with the accepted capability subset. A video-enabled host requires
bit 0. Tokens are association identifiers, not encryption or trust credentials;
do not log them. The post-HELLO control stream still contains only TOUCH/RESET.

The updated iOS listener accepts either v1 or v2 HELLO, then locks that version
for the connection. The host defaults to v1 unless `--video` is requested. An
old IPA rejects v2: report the need to update the IPA or run without `--video`;
do not retry an endless automatic downgrade/upgrade loop. New IPA + old host
remains input-compatible. Keep the three-second handshake deadline, 100 ms
full-state heartbeat, and 500 ms valid-input lease in both versions.

iOS listens on loopback **24865**; Windows initiates through the same USB device
as control. Add `LOCAL_VIDEO_PORT:24865` to the supervised iproxy invocation,
alongside control and optional LED mappings, with distinct local ports. Preserve
USB-only selection and `--udid`; manually managed bridges must target the same
device. Video cannot bind unless its token matches the live v2 input session.
Allow only one active video connection; reject unsolicited replacement. Close
the video child immediately when control closes or its token changes. Video
failure alone must not reset or reconnect control.

Connection order: control HELLO/empty TOUCH -> video HELLO/HELLO_ACK -> CONFIG
-> READY -> first IDR -> normal AUs/feedback. Each negotiation phase has a
three-second complete-message deadline. An unsupported video configuration
leaves control active. Every new video connection starts a new generation with
fresh CONFIG and IDR; stale generation work is never presented.

## Proposed video wire contract (version 1)

The `VideoProtocol.cs` and `BCVideoWire.h` framing helpers, strict JSON schema,
live session owners and codec configuration implement the contract below.
Hardware acceptance remains separate from source/build validation.
Use exact-length incremental reads: TCP may fragment or coalesce every field.

| Offset | Bytes | Header field |
| --- | --- | --- |
| 0 | 4 | ASCII `BCVD` |
| 4 | 1 | Version = 1 |
| 5 | 1 | Type from table below |
| 6 | 2 | Flags, zero except AU bit 0 = IDR |
| 8 | 4 | Payload bytes, excluding this 40-byte header |
| 12 | 4 | Per-direction sequence, same modulo rule as control; starts at 0 |
| 16 | 8 | Video generation; zero for HELLO/HELLO_ACK, otherwise nonzero |
| 24 | 8 | Source frame ID for AU; zero for other messages |
| 32 | 8 | Original Windows capture timestamp in microseconds for AU; zero otherwise |

Header integers are little-endian. Generation and frame IDs are monotonically
increasing uint64 values within the bound control session; restart that session
before wrap. Frame gaps from raw-frame drops are valid, but duplicate/backward
AU IDs are not. Source capture IDs may restart: assign a separate video frame
counter and retain the mapping in diagnostics. Only one generation is active.

Control-like video messages use UTF-8 JSON objects with the fields listed below,
no BOM, duplicate keys, unknown keys, nested extensions, NaN, or infinity. Reject
missing fields, invalid types, excessive nesting, overlong strings, and integers
outside their declared range before allocation. IDs/times in JSON are decimal
strings parsed as uint64, preserving exact values across tooling. All other
numeric fields are bounded integers unless explicitly labeled floating point.
Maximum JSON payload is 16 KiB. AU payload is binary, capped at **2 MiB** or the
smaller negotiated receiver cap. These limits are independent of control v1.

| Type | Direction | Payload and rule |
| --- | --- | --- |
| HELLO = 1 | Windows -> iOS | `sessionToken`: 32 hex characters; `videoVersion`: 1; optional Boolean `frameTiming` enables per-frame diagnostic logs on build-7+ IPA |
| HELLO_ACK = 2 | iOS -> Windows | `maxWidth`, `maxHeight` (1..1920), `maxPixels` (1..2073600), `maxFps` (1..60), `maxAuBytes` (65536..2097152), `profiles` (array of 1..2 values: `main`, `baseline`), `maxLevel` (H.264 level_idc, 1..42). Advertised bounds must have been probed; actual CONFIG still requires decoder creation |
| CONFIG = 3 | Windows -> iOS | Exact schema below; starts the nonzero generation |
| READY = 4 | iOS -> Windows | `hardwareVerified`: Boolean; true on iOS 17+ after requiring/verifying hardware, false on iOS 15–16 after checking H.264 hardware capability. Generation matches CONFIG; decoder/renderer configuration must succeed |
| AU = 5 | Windows -> iOS | One complete progressive picture/access unit, length-prefixed NAL units, with header frame ID and capture time |
| FEEDBACK = 6 | iOS -> Windows | Every 50 ms while streaming: decimal-string `receivedId`, `decodedId`, `presentedId`, `presentedAtUs` (0 if none), integer `pendingDecode` (0..3), `replacedDecoded` (uint32 cumulative), `thermal` (`nominal`, `fair`, `serious`, `critical`), `displayMilliHz` (0..240000) |
| REQUEST_IDR = 7 | iOS -> Windows | `reason` enum: `decode-error`, `missing-reference`, `stale`; at most once per second |
| CLOCK_PING = 8 | Windows -> iOS | `t1Us` host send time; at most once per second after initial sampling |
| CLOCK_PONG = 9 | iOS -> Windows | Echo `t1Us`, plus `t2Us` receive and `t3Us` send times from iOS monotonic clock |
| STATUS = 10 | Windows -> iOS | `state`: `running`, `source-idle`, `paused`, or `stopped`; `reason`: UTF-8 text <=256 bytes. At least every 250 ms when no AU is sent |
| ERROR = 11 | Either | `code`: `unsupported-config`, `hardware-unavailable`, `invalid-stream`, or `internal`; `detail` <=256 UTF-8 bytes. Best effort, then close video only |

CONFIG fields are `codec="h264"`, `profile`, `level`, `codedWidth`, `codedHeight`,
`fpsNum=60`, `fpsDen=1`, `bitrateBps` (4000000..20000000), `nalLengthBytes=4`,
`color="bt709-limited"`, `rotation=0`, `sourceWidth`, `sourceHeight` (1..16384),
`crop` (array of four finite normalized floats x/y/width/height inside the source client),
`contentRect` (array of four finite coded-pixel floats x/y/width/height inside coded bounds),
`circle` (array of three normalized client floats centerX/centerY/radius with the existing
`PlayfieldCircle` validation), and `sps`, `pps` (one base64 raw NAL each, no start
code; decoded lengths 1..1024). Coded dimensions must be even and inside the
negotiated bounds. Validate SPS dimensions/profile/level/cropping against CONFIG;
reject contradictory metadata and a clipped calibrated circle. CONFIG must fit
16 KiB and contain a confirmed profile's geometry. Full-client fixture tests
must supply an explicit fixture circle rather than reuse a Mercury profile.

AU NAL lengths are **four-byte big-endian**, as required by the chosen AVC sample
representation; this is the sole integer-endian exception. Each NAL must be
nonempty and fit the remaining payload; all lengths must sum exactly. Normalize
encoder Annex B output (three/four-byte start codes) to this representation.
Extract SPS/PPS from actual encoder sequence headers/output, never hardcode them.
Do not send bare Annex B to VideoToolbox. One AU may contain multiple slices and
ancillary NALs, but exactly one picture; the sender must validate MFT sample
boundaries. CONFIG's parameter sets are authoritative; changed sets require a
new generation. Mark IDR only when the picture actually contains IDR VCL NALs,
not merely because the encoder was asked for a keyframe.

The first AU after READY must be an IDR matching CONFIG. To obtain SPS/PPS,
the host may encode a bootstrap frame before CONFIG and hold its output within
the AU cap. Discard a bootstrap IDR older than 100 ms when READY arrives and
force another. The receiver rejects P-frames until a valid IDR is accepted.
Unknown type/flags/version, invalid lengths, wrong generation/order, or timeout
closes video; never try to resynchronize by scanning arbitrary compressed bytes.
Normal reconfiguration also closes/reopens video, simplifying ordered CONFIG
and decoder replacement. Clock exchange is optional diagnostics after READY;
all clock/feedback/status messages use the current configured generation.

REQUEST_IDR uses the same close/reopen recovery path as a decode failure in this
first implementation; it cannot bypass already-buffered AUs. Coalesce repeated
requests while recovering. Hardware/property negotiation errors are terminal
until settings change; overload/oversized AUs may invoke the bounded adaptation
and retry policy below. FEEDBACK may report zero before its first milestone;
otherwise IDs cannot move backward, exceed the last sent frame, or refer to a
retired generation. A presented ID cannot exceed decoded or received IDs.

## Bounded latency and overload behavior

These limits include work in progress. They are ceilings, not desired queue
depths; normal operation should have close to zero waiting frames.

| Stage | Initial bound | Saturation/age action |
| --- | --- | --- |
| Capture | Existing three textures, one pending, one lease | Replace pending raw frame; retain fence ownership rules |
| NV12/encoder input | Four owned surfaces; at most three accepted inputs without matching output | Admit latest available raw frame only; skip raw frames older than 33 ms; no free sample means skip admission |
| Encoded send | At most two AUs including active send, combined <=4 MiB; oldest enqueue age <=50 ms | Cancel/close video and restart generation; never remove one reference AU and keep its dependents |
| One socket write | <=100 ms to write a complete framed message | Timeout closes socket even after a partial write; do not continue framing on it |
| iOS receive/decode | One partial AU plus at most three complete outstanding AUs including VT submissions, <=8 MiB compressed total | Close/recover at IDR if full or complete AU waits >50 ms; no arbitrary compressed-frame dropping |
| Decoded/render | One latest pending pixel buffer, one retained displayed buffer for rotation, at most two drawables awaiting presentation and two GPU submissions | Coalesce immediate render requests; replace old pending frame, skip drawing if capacity/drawable unavailable; release GPU and presentation ownership separately |
| Feedback accounting | <=32 frame timing entries or 500 ms, whichever first | Missing correlation is reported; 250 ms without forward presentation progress during moving-source streaming triggers recovery |

If a selected encoder requires more buffering than these defaults, stop the
spike and document measured latency before revising the limit. Do not silently
grow pools to make a deadlock or throughput failure disappear. Track internal
encoder lag by input/output IDs, not just the application queue length.
If accepted encoder input makes no output progress for 100 ms, recover the video
generation; an idle source with no accepted input does not trigger this watchdog.

TCP, iproxy, usbmux, and the OS can already hold bytes after a successful write.
Socket send completion is not display completion. Sample FEEDBACK independently
of sending; track last received, decoded, and presented IDs and ages on the
host. Bound application buffers and request small socket buffers, then record
effective values. Recovery cancels/closes the old video socket, invalidates its
generation on iOS, and flushes/recreates the codec. The next connection sends
fresh CONFIG/IDR. Replacing an application queue alone does not purge TCP.

Use a three-second first-frame timeout; after startup, any partial video message
has a one-second completion deadline and either peer closes after one second
without a complete valid peer message (FEEDBACK or AU/STATUS as applicable).
Static content may retain the last frame with fresh STATUS and no presentation
progress requirement. Minimize/known capture failure shows a paused/stale status
within 500 ms. No WGC arrivals alone cannot distinguish a static window from a
frozen source; diagnostics must state that limitation. App suspension cancels
all sockets and releases touches; normal foreground resume starts fresh.

For recoverable video failure, retry at 250 ms, 500 ms, then 1 s; cap at three
consecutive failures and leave video disabled with manual retry available.
Reset the failure count after 30 seconds of healthy streaming. A GPU device loss
recreates capture/converter/encoder together using existing capture recovery
rules. Preserve window identity checks and require selection after source exit.

Initial adaptation is downward only during a session: if send/decode queue age
exceeds 33 ms for three consecutive one-second summaries, or overload causes
recovery, reduce bitrate by 25% to a 6 Mbps floor; then reduce the output envelope
one step (1920 -> 1280 -> 960 long edge; square 1080 -> 960 -> 720), maintaining
60 fps and aspect ratio. Apply changes through a new generation, no more often
than every five seconds. If the minimum configuration still fails, disable
video. An optional manually selected 30 fps mode must be labeled degraded and
does not pass this milestone. No automatic upward oscillation in the first build.

Input has priority: suspend video immediately on an input FIFO overflow or input
queue age over 20 ms, and disable it if rolling input p95 exceeds its approved
baseline by >2 ms for three summaries. These are conservative video admission
guards, not changes to input transition semantics or the existing reset policy.
Include optional LEDs in USB contention tests; separate sockets do not reserve
bandwidth. Serious/critical iOS thermal state reduces resolution or suspends
video; report power/thermal/display limits instead of claiming stable 60 fps.

## iOS decoder, rendering, and touch alignment

Add `BCVideoTransport`, `BCVideoDecoder`, and `BCVideoView` with independent
serial transport/decode queues and a main-thread view coordinator. Link
VideoToolbox, CoreMedia, CoreVideo, Metal, MetalKit, and QuartzCore in
`ios/project.yml`. Keep Network.framework work and H.264 parsing off the main
thread. Bound outstanding decode callbacks and retain each sample's bytes and
frame context until its completion; ignore callbacks from retired generations.

Create `CMVideoFormatDescription` using the two H.264 parameter sets and a
four-byte NAL header length, then `CMSampleBuffer`/`VTDecompressionSession`.
Use the AU capture timestamp as the sample PTS with a 1,000,000 timescale and
60/1 nominal duration; preserve dropped-frame gaps. This is a correlation clock,
not an instruction to schedule presentation against the unrelated iOS epoch.
Request real-time decode, bi-planar video-range output, IOSurface-backed,
Metal-compatible buffers. Require/verify hardware decode using the options and
properties available to the target iOS SDK/runtime; check availability rather
than copying macOS-only assumptions. Where a property is unavailable, establish
the supported iOS verification mechanism in the spike before claiming a pass.
The deployment minimum remains iOS 15. On iOS 15–16, check hardware H.264
capability and configure real-time decoding, then send `hardwareVerified=false`.
On iOS 17+, require hardware decoding and query the active session; failure
disables video. The host accepts both Boolean values and logs the distinction.
An unverified path cannot send READY with `hardwareVerified=true`. See Apple's
[H.264 format-description API](https://developer.apple.com/documentation/coremedia/cmvideoformatdescriptioncreatefromh264parametersets(allocator:parametersetcount:parametersetpointers:parametersetsizes:nalunitheaderlength:formatdescriptionout:)),
[hardware decoder requirement](https://developer.apple.com/documentation/videotoolbox/kvtvideodecoderspecification_requirehardwareacceleratedvideodecoder),
and [hardware-use property](https://developer.apple.com/documentation/videotoolbox/kvtdecompressionpropertykey_usinghardwareacceleratedvideodecoder).

Create Y and UV plane textures through `CVMetalTextureCache`, sample with the
declared BT.709 limited-range conversion, and render to an explicitly chosen
drawable color space. Verify the transfer path avoids double gamma conversion.
Keep the pixel buffer and plane textures alive through command-buffer completion;
do not convert frames via UIImage, CGImage, CPU RGBA, or per-frame texture uploads.
See [Core Video's Metal texture cache](https://developer.apple.com/documentation/corevideo/cvmetaltexturecache-q3j).

Use one CADisplayLink at preferred 60 Hz, in common run-loop modes, for fallback
render requests and cadence measurement; do not add a second independent timer.
As of IPA build 7, decode callbacks immediately request a coalesced main-thread
render of the newest ready buffer, removing the wait for the next display tick.
As of build 8 allow two unpresented drawables, freeing each slot at actual presentation
(or GPU failure), independently of the two GPU submission slots. Callback order
is unspecified, so each submission owns a ticket and releases each slot once.
Keep counts across retired generations until their callbacks release ownership.
The fixture-3 device trace showed roughly two refresh intervals from GPU completion
to presentation; build 7's single pending drawable consequently limited output
to about 30 new frames/s despite 60 Hz display-link callbacks.
Reuse the last drawable content if no new frame exists and count it as a repeat. Track
scheduled draw, GPU completion, and drawable presentation separately where
available; none alone proves glass latency. Low Power Mode, thermal policy, and
device limits can change the actual cadence despite the requested
[preferred frame-rate range](https://developer.apple.com/documentation/quartzcore/cadisplaylink/preferredframeraterange).

IPA build 6 uses three Core Animation drawables while keeping at most two GPU
submissions and one latest pending decoded buffer. GPU completion does not imply
Core Animation has released the currently displayed drawable. A per-frame
autorelease pool promptly releases command-buffer/render-pass/drawable references
after commit, following Apple's
[drawable lifecycle guidance](https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/Drawables.html).
Presentation is asynchronous (`presentsWithTransaction=NO`). Generation-scoped
render/wait means and GPU completion timings complement display cadence and
presentation counts; changing pool size alone does not establish lower glass
latency or sustained 60 fps.

`--video-diagnostics` adds optional Boolean `frameTiming` to HELLO and enables
generation/frame-correlated host and iOS timestamp logs. The default HELLO stays
unchanged; detailed tracing requires a matching build-7+ IPA. All iOS timestamps
and clock replies use Core Animation's host clock, matching Metal GPU/presentation
timestamps. `Brokencca.VideoTiming` joins the logs and reports stage durations
and capture-to-presentation estimates with clock uncertainty. It handles missing
timestamps, replaced frames, retired generations, static redraws and startup
separately; only actual, valid steady presentations contribute to summaries.
See [VIDEO-USAGE.md](VIDEO-USAGE.md) for the command and timing boundaries.

Implement one immutable geometry snapshot shared by video and touch rendering:

The steps below describe the selectable **Full image** mode. As of IPA build 5,
the default **Full-size ring** mode scales and centers the image so the calibrated
circle radius equals the original controller radius on the device. This crops
surrounding UI while keeping image aspect ratio and video/touch/LED alignment.
Both modes use the same immutable geometry; zoom affects the coded-image display
rectangle and its inverse touch transform. A full-device Metal viewport samples
the visible image via a UV region and draws black outside the coded image, avoiding
oversized negative viewports. Held contacts are released on mode switches.
See [VIDEO-USAGE.md](VIDEO-USAGE.md) for the mode button and tradeoff.

1. Convert the confirmed normalized client crop and circle to client pixels.
   Transform source -> crop -> coded content rectangle -> decoded clean aperture
   -> aspect-fit device content in UIKit points. Device scale affects Metal
   drawable pixels, not touch-point coordinates. Start with zero source rotation;
   portrait/landscape device rotation changes the fit, not source zone ordering.
2. For a touch, invert the display transform to client coordinates. Reject
   points in device letterboxing or coded padding before radial mapping. Convert
   to virtual circle coordinates `vx=(x-cx)/r+1`, `vy=(y-cy)/r+1`, and invoke
   existing `BCApplyTouch(vx, vy, 2, 2, bitmap)`. This preserves all current
   expanded-zone/seam/radial semantics, including points inside the circle's
   center region and beyond its outer radius within the valid content rectangle.
3. Draw outlines/local highlights and optional LEDs through the same transform.
   Set the touch overlay nonopaque with a clear background; remove inactive
   opaque ring fills in video mode. Make LED fills optional/translucent so they
   do not hide game notes. Touch capture remains on the topmost view.
4. When layout/crop/circle changes, release active touches, discard old held
   contacts until lifted, then atomically adopt the new snapshot with the first
   displayed frame of that generation. Recheck aspect/profile compatibility.
   During a video-only reconnect with unchanged geometry, retain input mapping
   and local feedback. On video disable retain the last calibrated mapping;
   an explicit switch to input-only layout also releases touches first.
5. Show waiting/paused/reconnecting/unsupported status without blocking touches
   or covering the playfield center. Rebuild the decoder on new CONFIG; cancel
   callbacks and release Metal/VT resources in order on background/disposal.

## Implementation sequence and deliverables

Each slice ends with a reviewable artifact and evidence; a passing CI compile
does not imply physical USB/encoder/iOS performance.

1. **Codec/interop spike:** add `Brokencca.Video.Windows` targeting the same
   Windows TFM as capture and a standalone encoder probe. Select/pin compatible
   MF bindings after a Release build; update third-party notices. Prove GPU
   NV12 -> real hardware H.264 on the RX 6650 XT, surface release, SPS/PPS/IDR,
   profile/level, zero B-frames, flush/restart, and latency. Produce a short
   fixture stream and machine-readable capability report. A laptop can validate
   code paths but cannot close the AMD hardware gate.
2. **Protocol/state core:** implement control-v2 opt-in, session binding, video
   framing, caps, generation and bounded recovery state machines in portable
   Core/C headers. Add shared binary/JSON fixtures and simulated slow/invalid
   peers. Keep existing control-v1 regressions passing.
3. **iOS playback fixture:** decode the captured fixture and exercise the Metal
   view on a signed iPad build, then connect a Windows fixture sender through
   iproxy. Verify hardware evidence, colors, portrait/landscape geometry, local
   touch/LED overlay, and sustained presentation before using live Mercury.
4. **Live capture integration:** connect accepted WGC/profile to the encoder,
   sender, feedback, and recovery. Update Host's Windows target and PerMonitorV2
   manifest. Planned options: `--video`, `--video-port` (24865), `--hwnd` or
   `--mercury`, `--profile`, `--video-long-edge` (default 1280),
   `--video-bitrate` (default 10000000), and `--video-diagnostics`. For square
   input use the square limits above. Reuse capture parsing instead of forking
   crop/calibration logic. These options do not exist yet.
5. **Failure/load validation:** automate fragmentation, stale session, stalled
   sender/receiver, queue saturation, generation races, keyframe recovery,
   invalid codec configuration, device loss, and control isolation. Complete
   the physical test matrix below and tune only from logged evidence.
6. **Package:** update Windows solution/build/publish scripts, macOS unsigned
   IPA workflow, README/PROTOCOL/usage guide, and VALIDATION evidence. Provide
   matched host/IPA revisions, exact commands, selected settings, known limits,
   and input-only rollback instructions. Signing remains the existing external
   installation step. Do not mark streaming complete until hardware gates pass.

## Measurement and acceptance

Log once per second off critical paths: frame counts/IDs, each queue's age,
depth and high-water mark, per-stage median/p95/p99/max, bytes/s and AU sizes,
drop/repeat reasons, effective encoder controls, hardware identities, generations,
recoveries, iOS display cadence, and thermal state. Keep detailed per-frame traces
bounded and opt-in. Compare tracing on/off. Preserve existing input metrics.

Use host monotonic intervals for capture-to-encode/send and iOS local intervals
for receive-to-decode/present. Calibrate WGC `SystemRelativeTime` against the host
monotonic epoch; do not assume raw Stopwatch ticks share its representation.
For clock exchange, host records t4 on receipt, estimates iOS-minus-host offset
as `((t2-t1)+(t3-t4))/2`, and network RTT as `(t4-t1)-(t3-t2)`. Take eight startup
samples, prefer the lowest-RTT sample, refresh every second, and report at least
RTT/2 uncertainty plus observed drift. Reject invalid samples. A clock-adjusted
latency outside the error bound is not a precise measurement.

Proposed steady-state p95 budget: capture/admission 8 ms, conversion/encode
12 ms, send/USB/receive 10 ms, decode 6 ms, presentation wait 17 ms, leaving
roughly 7 ms margin within 60 ms. These allocations identify bottlenecks;
stage percentiles do not mathematically sum to the end-to-end percentile.
Validate visible PC-to-iPad delay with a >=240 fps camera filming both displays
and an animated frame-ID/timecode fixture. Report PC scanout as a reference,
camera quantization and display scanout uncertainty; this measurement differs
from WGC-capture-to-glass and from touch-to-game latency. Capture at least 200
paired frame events, reporting median/p95/p99 and method. Measure PC audio versus
iPad picture and document any game offset adjustment; audio stays on Windows.

| Gate | Required evidence and pass condition |
| --- | --- |
| Build/regression | Release Windows build, existing executable test runner, portable iOS C tests, macOS Xcode unsigned device build; v1 interoperability and existing serial/hook/LED tests remain passing |
| Framing/ownership | Golden C#/C fixtures; every split boundary, coalescing, EOF/partial timeout, overflow length, malformed NAL/config, stale token/generation/sequence, missing IDR, late callbacks; bounded allocations and no reuse before release |
| Color/geometry | Fixture edges, moving frame IDs, offset circle, range/color bars, even-size padding, portrait/landscape, 100/144 DPI source, layout change while held, and letterbox rejection; all 240 zone mappings plus boundary-expansion behavior agree with baseline |
| Hardware codec | Logged hardware MFT/adapter and iOS hardware decode evidence; output SPS/settings verified at chosen size/bitrate; no software conversion/codec fallback |
| 60 fps | After 10 s warmup, a verified 60 Hz animated source runs 30 min: distinct presentation average >=59.4 fps, >=59 fps in at least 99% of rolling 10 s windows, <=1% missing source frames, and no unexplained presentation gap >100 ms. Repeated draw calls do not count; report capture/encode/decode loss separately |
| Latency | Capture-to-presentation estimate p95 <=60 ms, p99 <=100 ms with uncertainty reported; optical evidence consistent with the 30-60 ms engineering objective. If cadence passes but latency fails, record a performance failure, not completion |
| Input isolation | Complete the pending serial-worker gate, then matched video-off/on runs in one game launch: 60 s ten-contact movement, rapid tap/repress/chords, and 30 min gameplay. Zero stuck/missed/reordered test transitions and overflows; host receive-to-sink p95 <10 ms with video adding <=2 ms to p95 and <=5 ms to p99. Also compare iOS callback/send queues and game-visible results. Hook backend needs its own gate if claimed supported |
| Recovery | Stall each video stage for 500 ms, saturate GPU, burst IDRs, resize/minimize/restore, rotate, remove/reinsert cable, background/foreground, kill/restart iproxy, and simulate device loss. No stale-generation display or retained touch; video-only faults preserve control; healthy warm reconnect presents IDR within 3 s, cold codec startup within 10 s |
| Sustained/USB | Run with LEDs off/on, the selected USB hub/direct path, power/thermal conditions recorded. Pools stay bounded, no monotonic memory growth after warmup, no repeated reconnects or adaptation at the accepted nominal setting; constrained scenarios may degrade visibly but must retain safe input behavior |

The fixture closes local pipeline gates. Real Mercury on the RX 6650 XT and
signed iPad mini 5 app closes game/USB/thermal/latency gates. Record actual results
and artifacts in [VALIDATION.md](VALIDATION.md), marking unavailable hardware
checks pending. Current accepted capture evidence does not establish any of
these new streaming results.
