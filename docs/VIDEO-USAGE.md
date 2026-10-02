# Video build and device test

The video path is implemented for testing: Windows WGC/D3D11 capture and GPU
NV12 conversion, hardware Media Foundation H.264, a separate USB-forwarded TCP
connection, iOS VideoToolbox decode and Metal presentation. Audio stays on the PC.
The target is 60 fps; sustained device performance and visible latency are not
yet accepted.

## Build artifacts

Run the **Build and test** GitHub Actions workflow on the branch containing these
changes (push, pull request, or `workflow_dispatch`). Download:

- **Brokencca-Windows-x64**: self-contained host, `brokencca-video.dll`, hardware
  encoder probe, MercuryIO DLL, documentation and licenses.
- **Brokencca-iOS-unsigned**: `Brokencca-unsigned.ipa`. Sign it with your existing
  installation method before installing. No signing credentials are supplied.
- **Brokencca-Capture-Windows-x64**: capture preview/calibration and fixture tools.

The macOS job compiles the complete iOS device app before packaging the IPA.
Its deployment minimum is iOS 15. The video hardware verification keys require
iOS 17: older supported systems report capability checked/session unverified;
17+ must pass explicit session hardware verification.

Local Windows build needs .NET 8 SDK and an x64 MinGW-w64 C++ compiler:

```powershell
./scripts/build.ps1
./scripts/publish-video.ps1 -Compiler g++.exe
```

Keep the entire Windows artifact together. The video DLL must be beside the
host/probe executable. No FFmpeg installation is needed to stream.

## First launch on the game PC

Install the updated signed IPA and open Brokencca. Use the confirmed calibration
profile produced by CapturePreview; the existing `log/capture-profile.json`
is suitable for the previously tested Mercury geometry. Start the game so that
its unique Mercury window exists before starting the video host. The host binds
to that window's identity; restarting the game requires restarting the host.

From the Windows artifact directory, adapt the paths/COM ports:

```powershell
./Brokencca.Host.exe --serial --left COM5 --right COM6 --video --mercury `
  --profile C:/path/capture-profile.json --iproxy C:/path/iproxy.exe `
  --video-diagnostics --device-model "iPad mini 5" --gpu "RX 6650 XT" `
  2>&1 | Tee-Object video-test.log
```

If the game requires the serial host before startup, launch the established
input host first, start the game, then stop that host and run the command above.
For the hook backend substitute `--hook`; add `--leds` if desired. Input-only
usage remains available by omitting `--video`. An old IPA supports input-only v1;
video requires the updated IPA.

Default video settings are a 1280-pixel long edge (720x1280 for the accepted
1080x1920 portrait profile), 60 fps, Main H.264 with no B-frames, and 10 Mbps.
Options: `--video-long-edge 960..1920`, `--video-bitrate 6000000..20000000`,
`--video-port` for a distinct local port. Pixel count is capped at 2,073,600.
`--hwnd 0x...` can select a specific window instead of `--mercury`.

Without automatic forwarding, run iproxy separately:

```powershell
iproxy -l 24864:24864 24865:24865
```

Add `24866:24866` for optional LEDs. Video binds to the active control token;
input heartbeats and processing continue independently. Negotiation, encoder,
network, decode or presentation failure closes only video and retries with a
fresh CONFIG/IDR at reduced settings, up to three attempts. Input queue age over
20 ms or depth at least 48 stops video for the current control session. Thermal
serious/critical status also triggers video recovery. Restart the host after
video disables itself. These are provisional protection budgets for testing.

## Touch area during video

IPA build 5 adds a button in the top-right corner. **Full-size ring** is the
default: zoom and center the calibrated game circle until its radius matches the
original input-only controller (`min(view width, view height)/2`). The touch ring,
local highlights, LEDs and video use the same transform. The image keeps its
aspect ratio; surrounding game UI can be cropped. The calibrated playfield
circle stays visible, and areas outside actual video content do not produce input.

Tap the button for **Full image** to show the complete aspect-fit video with a
smaller, still aligned touch ring. Switching releases current contacts before
changing the mapping; lift and retouch to continue. The choice is saved across
app launches, and static images redraw when the mode changes. This is an iOS
presentation choice; capture calibration and encoding settings do not change.

The fixture center counter advances once per rendered frame. At its default
60 fps, a photographed seven-frame difference is approximately 117 ms of relative
display delay. Repeat photos to account for display/camera scan timing. The
working fixture log shows about 59 frames/sec sent but mostly about 30 Hz render
callbacks, so unique iPad presentation has not yet met the 60 fps acceptance gate.
Keep Low Power Mode off during performance testing; iOS may change the actual
CADisplayLink cadence despite a 60 Hz request. See Apple's
[display-link frame-rate behavior](https://developer.apple.com/documentation/quartzcore/cadisplaylink).
Build 5 iOS logs include `low_power`, `render_max_ms` and `drawable_wait_max_ms`
(maxima since the video generation began) to distinguish power policy and rendering
or drawable stalls. The Windows log alone cannot establish which cause dominates.

## What to test and return

1. Run `./Brokencca.VideoProbe.exe video-probe.h264` on the game PC. It must report
   180 pictures and at least two IDRs. Save its output. It exercises the encoder
   without the iOS/USB connection.
2. Launch streaming and save `video-test.log` plus the iOS `BCCA_VIDEO` logs.
   Check full-client framing, black bars, color, and all touch/LED sectors.
   Rotate with the source static, then moving; touch mapping must stay aligned.
3. Check static scenes, minimize/restore, resize, USB removal/reconnect, app
   suspension/resume and game restart. No stuck touch may survive a control loss.
4. Compare identical fast taps/slides and the 60-second ten-contact stress case
   with video off/on. Return queue-age/overflow logs and any visible differences.
5. Play for 30 minutes. Check sustained unique-frame presentation, dropped frames,
   increasing latency and thermal behavior. Measure visible latency with a
   high-speed camera where possible; the 30–60 ms budget is still a target.

Host `video` logs include sent/received/decoded/presented IDs, decoded backlog,
measured display cadence, replaced decoded frames, thermal status and clock
offset/uncertainty. Static scenes naturally send fewer unique frames. Display
cadence is not a count of unique game frames. Clock estimates are diagnostic,
not proof of visible latency; Metal's presented timestamp is not a camera
measurement. The first retained startup IDR may be older while configuration
completes; subsequent admitted raw frames use the 33 ms age budget.

The local Windows hardware probe and portable tests passed; Xcode compilation
and the device checks above are deliberately left to your Actions run and test.
