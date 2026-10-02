# Brokencca

Wired iOS touch controller for WACCA, based on toucca's input mapping and serial
backend and Brokenithm-iOS's usbmux connection model.

**Playable input prototype, validated by the user on real hardware.** IPA,
multitouch, USB forwarding, and WACCA serial input work on the tested setup.
Heavy multitouch/fast movement can cause delay or an overflow/reconnect; reducing
that latency is the next milestone. A host scheduling fix is implemented and
awaits game-PC retesting; see the validation report. A standalone Windows window
capture preview and calibration fixture are implemented and locally tested at
the **60 fps** target. Window capture is now accepted on real Mercury, with
perfect boot-black circle alignment and no perceptible preview latency reported
by the user. Hardware H.264 streaming over USB, iOS VideoToolbox/Metal playback,
and calibrated touch mapping are now implemented for device testing. Sustained
60 fps and combined gameplay performance still need validation.

- [Implementation plan and remaining milestones](docs/PLAN.md)
- [Next milestone: input latency, then video](docs/NEXT-STEPS.md)
- [Control protocol](docs/PROTOCOL.md)
- [Validation results and hardware checks still needed](docs/VALIDATION.md)
- [Optional MercuryIO backend and iPad LEDs](docs/HOOK-IO.md)
- [Window capture preview, fixture, and calibration](docs/CAPTURE-USAGE.md)
- [60 fps iOS video streaming design and acceptance plan](docs/VIDEO-STREAMING.md)
- [Video build, launch, and device test instructions](docs/VIDEO-USAGE.md)

## Build and test Windows

Requires the **.NET SDK**, not just the runtime. `dotnet --list-sdks` must list an
SDK capable of building .NET 8. No Visual Studio IDE is required.

```powershell
dotnet build Brokencca.sln -c Release
dotnet run --project tests/Brokencca.Tests -c Release
dotnet run --project src/Brokencca.Host -c Release -- --help
```

Alternatively, `./scripts/build.ps1` builds and runs the regression suite. It
prefers a repository-local `.tools/dotnet/dotnet.exe` when present, otherwise
uses `dotnet` on PATH. Tests are a dependency-free executable; **`dotnet test`
does not run this suite**.

To verify the packaged executable, run `dotnet publish src/Brokencca.Host -c Release
-r win-x64 --self-contained true -o artifacts/windows`, then run
`./scripts/smoke-test.ps1` in PowerShell 7. The smoke test checks reconnection after
a malformed handshake, ordered transitions, and release on disconnect in dry-run mode.

## Try without a device

In one terminal:

```powershell
dotnet run --project tools/Brokencca.Simulator -c Release
```

In another:

```powershell
dotnet run --project src/Brokencca.Host -c Release -- --dry-run
```

The simulator sends a short sequence of presses/releases and disconnects while
holding zone 0. The host should print the matching zones and a final `Zones: []`.
It then retries, ready for another simulator/device session.

For a quiet, machine-readable timing baseline, run the host with diagnostics and
select a repeatable simulator workload/rate:

```powershell
dotnet run --project src/Brokencca.Host -c Release -- --dry-run --quiet --diagnostics
dotnet run --project tools/Brokencca.Simulator -c Release -- --workload all --rate 240 --duration 60
```

Available workloads are `taps`, `chords`, `slides`, `repress`, `burst`, and
`all`. Sweep `--rate` through 120, 240, 480, and 1000. Diagnostics are JSON lines
with receive intervals, sink time, serial queue age/depth, serial write time, and
machine-readable disconnect reasons. For a hardware run, add `--device-model`
and `--gpu` to record the missing test metadata; run once with `--quiet` and once
without it to quantify console overhead.

## Connect an iPhone/iPad

1. Build/sign/install the iOS app (below), keep it foreground, and connect USB.
2. Ensure Apple Mobile Device Support/service is available on Windows, and
   unlock/trust the device as necessary.
3. Install a Windows build of upstream `libusbmuxd`'s `iproxy`. The prototype
   expects the current `LOCAL_PORT:DEVICE_PORT` argument syntax.
4. Start the USB-only bridge and then the host:

```powershell
iproxy -l 24864:24864
dotnet run --project src/Brokencca.Host -c Release -- --dry-run
```

The host can also launch/supervise an installed iproxy:

```powershell
dotnet run --project src/Brokencca.Host -c Release -- --dry-run --iproxy C:/path/to/iproxy.exe --udid YOUR_DEVICE_UDID
```

`--udid` is optional with one device. This is a USB connection; Wi-Fi and
tethering are not required. The host only connects to loopback. USB driver and
iproxy setup are external prerequisites; no device utilities are bundled.

## Enable WACCA input

Configure com0com pairs **COM3 ↔ COM5**, **COM4 ↔ COM6**, with buffer overrun
enabled on both ends as in toucca. Disable hook-based touch input (`touch.enable=0`)
where applicable. Close toucca or any other process owning those COM ports.

Start Brokencca **before starting the game**:

```powershell
dotnet run --project src/Brokencca.Host -c Release -- --serial --left COM5 --right COM6
```

Keep iproxy running, or pass `--iproxy` to the command above. Default mode is
dry-run; COM ports open only with `--serial`. Ctrl+C requests an all-release and
shuts down. The game must complete board startup before packets are emitted.
The `0.2.0-serial-worker` revision uses a signalled serial worker without a sleep
between queued transitions. It preserves press/release ordering, the 64-state
safety limit, and the iOS boundary expansion. Replace the complete Windows
publish folder, not just its EXE; no IPA update is needed for this host-only fix.
Use `--diagnostics` for the before/after test. New fields include
`receive_to_serial_ms` (valid touch receipt through both COM writes returning),
`left_write_ms`/`right_write_ms`, driver-buffer high-water marks, and
`worker_interval_ms` (including idle waits). COM completion is not proof of
game consumption. See [the retest checklist](docs/VALIDATION.md#serial-worker-retest).

The inherited burst-based serial command parsing works in the reported gameplay
test but still needs fragmentation/restart coverage; see the plan's compatibility
limit. The iOS layout preserves toucca's unusual
inner/outer radius acceptance, so the blank centre is not a guaranteed dead zone.

## Optional hook input and LEDs

The host now supports `--hook` as an alternative to `--serial`, with a native
`brokencca-mercuryio.dll` matching the user's segatools MercuryIO 1.0 ABI.
The Windows publish folder `artifacts/windows-hook` includes the DLL when built
with `./scripts/build-hook.ps1`. Set `[touch] enable=1` and
`[mercuryio] path=brokencca-mercuryio.dll` in the game configuration, then run:

```powershell
.\Brokencca.Host.exe --hook --leds --diagnostics --iproxy .\iproxy\iproxy.exe
```

Omit `--leds` for hook touch alone, which works with the existing IPA. LED display
needs the updated IPA and `[elizabeth] enable=1`. LEDs use a separate USB socket
and bounded latest-frame rendering; input protocol v1 and boundary expansion
remain unchanged. Full instructions, callback-rate caveats, and hardware tests
are in [HOOK-IO.md](docs/HOOK-IO.md). Hook game compatibility and LED orientation
are not yet hardware-validated; the existing serial path remains available.

## iOS build

The app uses Objective-C, UIKit, and Network.framework with an iOS 15 minimum.
There are no CocoaPods dependencies. On macOS with Xcode and XcodeGen:

```sh
cd ios
xcodegen generate
xcodebuild -project Brokencca.xcodeproj -scheme Brokencca \
  -configuration Release -destination 'generic/platform=iOS' \
  -derivedDataPath build CODE_SIGNING_ALLOWED=NO build
```

The GitHub Actions workflow performs this build and uploads an **unsigned IPA**.
It must be signed/provisioned by your chosen installation workflow before it can
run on a device. For Xcode installation, generate/open the project, select your
development team and a suitable bundle ID, then build to the device. The unsigned
CI build does not need signing secrets and is not an App Store release.

The user has installed and tested an IPA successfully. Exact CI run/build
provenance has not been recorded here; see the validation report for the scope
of the confirmed hardware results.

## Layout

Windows window capture now has a standalone GPU preview and animated test
fixture, including toucca-reference and boot-black circle calibration. Build,
run, and profile instructions are in [CAPTURE-USAGE.md](docs/CAPTURE-USAGE.md).
It is separate from the input host; iOS video streaming is a later milestone.

```text
src/Brokencca.Core       Framing, sessions, touch geometry, serial packet encoding
src/Brokencca.Host       Windows console host and serial worker
ios/Brokencca           Native touch surface and USB-facing TCP listener
tools/Brokencca.Simulator Scripted peer for local smoke testing
tests                   C# regression runner and portable iOS C tests
```

## Credits and license

GPL-3.0-or-later; see [LICENSE](LICENSE) and [NOTICE](NOTICE).

Touch geometry and serial protocol constants/layout are adapted from
[toucca](https://github.com/BlueGlassBlock/toucca). The wired connection architecture
is informed by [Brokenithm-iOS](https://github.com/esterTion/Brokenithm-iOS). The `mercuryio` hook is informed
 by [WACVR](https://github.com/xiaopeng12138/WACVR).
