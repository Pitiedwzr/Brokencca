# Windows window capture proof

This package contains a local GPU capture preview and an animated test window.
It does not stream video to iOS yet. Use an unlocked Windows 10 build 19041+
desktop with a working graphics driver. Start with SDR/HDR disabled and a
windowed/borderless source. No COM ports, IO hook, iPad, or Mercury installation
are needed for the fixture.

In the self-contained package, open PowerShell in the package directory:

```powershell
.\fixture\Brokencca.CaptureFixture.exe --title "Mercury  " --boot-black
.\preview\Brokencca.CapturePreview.exe --mercury --calibrate-black --diagnostics
```

Run these in separate terminals. The two trailing spaces in the Mercury title
are intentional. If more than one such window exists, list windows and select
the intended HWND explicitly:

```powershell
.\preview\Brokencca.CapturePreview.exe --list-windows
.\preview\Brokencca.CapturePreview.exe --hwnd 0x123456 --diagnostics
```

The preview captures the source window rather than the desktop/controller
overlay. It aspect-fits the client image by default. Use `--crop item` to inspect
window borders or `--crop normalized:x,y,width,height` for a crop inside the
client area. Crop coordinates use [0,1]; resizing preserves their normalized
intent. If geometry cannot be resolved, the preview labels a full-item diagnostic
view rather than guessing offsets.

## Playfield calibration

The user reports that toucca's placed outer ring approximately matches Mercury's
outer circle. `--toucca-reference` displays an initial estimate from that placement
logic. Adjust and verify it against the actual game; it is not an exact crop.

During boot, the area outside the circular display is black. `--calibrate-black`
or **C** explicitly samples several frames and proposes a circle only after
three consistent detections. Sampling uses diagnostic CPU readback and is
excluded from steady-state GPU-only performance runs. The detector requires a
substantial lit circular silhouette with dark surroundings. A dark screen,
sparse loading logo, clipped circle, or other ambiguous image yields no suggestion;
wait for the circular area to light up, or use the toucca reference/manual guide.
Detection never automatically crops video or saves a confirmed profile.

- **G:** toggle the yellow circle guide and center cross.
- **Arrow keys:** move the circle center by one source pixel.
- **+ / -:** change radius by one source pixel.
- **S:** confirm the checked guide and save a JSON profile. `--save-profile FILE`
  supplies its destination; otherwise a save dialog opens. Check center and
  top/right/bottom/left circumference before saving. A crop that clips the guide
  is rejected.
- **P:** pause the preview consumer for 250 ms to test backlog dropping.
- **R:** retry capture; after Mercury exits, explicitly select its replacement.
- **Esc:** stop the preview and release capture resources.

Load a saved profile with `--profile FILE`; its crop is applied unless `--crop`
explicitly overrides it. Profiles preserve client-relative center/radius and
record source size/DPI/provenance. A different aspect ratio requires recalibration.
Source restarts clear the old calibration. The normal system capture indicator
remains visible. Static/minimized sources are marked stale, without claiming a
live frame rate.

Fixture controls: **B** toggles boot black, **F** toggles borderless/decorated mode,
**R** changes size, **M** minimizes then restores after one second, **T** changes
title, **Esc** closes. The fixture has an intentionally offset circle, moving bar,
numeric frame counter, colored corner markers, and one-pixel client edges.

## Development and validation

From the repository, use the local `.tools/dotnet/dotnet.exe` SDK if needed:

```powershell
.\scripts\build.ps1
.\scripts\publish-capture.ps1
.\scripts\capture-smoke-test.ps1
.\scripts\capture-smoke-test.ps1 -BootBlack -DeviceLoss -OutputDirectory artifacts/capture-smoke-recovery
.\scripts\capture-smoke-test.ps1 -Borderless -Lifecycle -OutputDirectory artifacts/capture-smoke-lifecycle
.\scripts\capture-smoke-test.ps1 -StartStop -OutputDirectory artifacts/capture-smoke-start-stop
.\scripts\capture-smoke-test.ps1 -SlowConsumerMs 250 -Seconds 12 -OutputDirectory artifacts/capture-smoke-slow
.\scripts\capture-smoke-test.ps1 -Seconds 610 -OutputDirectory artifacts/capture-soak
.\scripts\analyze-capture-log.ps1
.\scripts\capture-input-replay.ps1 -CaptureState off
# Repeat with capture running, using the same rate and duration:
.\scripts\capture-input-replay.ps1 -CaptureState on
```

`capture-smoke-test.ps1` launches and owns its fixture, selects only its HWND,
checks captured pixels/calibration, and records logs in the specified directory.
Do not run simultaneous GPU performance cases. CI builds and runs deterministic
tests; an interactive GPU smoke test requires a real unlocked desktop and is
not implied by CI success. The user accepted real Mercury capture on 2026-10-02:
boot-black circle detection aligns perfectly and preview latency is imperceptible.
The supplied log and confirmed profile are recorded in [VALIDATION.md](VALIDATION.md).
The development device still has no runnable Mercury. Additional fullscreen
coverage, combined input performance, and iOS video require later tests.

The preview targets 60 submissions/presentations per second; WGC is source-driven
and may supply fewer unique frames. Diagnostics report arrivals, submission/drop
counts, pending/leased slots, frame age, QPC-to-present-submit timing, CPU, memory,
and adapter identity. Present-submit timing does not measure visible-display or
iOS latency. Device recovery has three bounded attempts; `--inject-device-loss-at`
tests the recovery path by simulation, not a physical GPU reset.

`--slow-consumer-ms N` injects one consumer stall after two seconds and verifies
that the next acquired frame is recent. `--capture-cycles N` is a smoke-test-only
option for repeated complete device/session/presenter teardown and startup.

Use `-PackageDirectory artifacts/windows-capture` with the smoke runner to test
the published self-contained executables. The replay comparison exercises the
quiet dry-run input host, not actual USB, serial, or game consumption.
