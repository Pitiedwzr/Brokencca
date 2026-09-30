# Brokencca

Wired iOS touch controller for WACCA, based on toucca's input mapping and serial
backend and Brokenithm-iOS's usbmux connection model.

**Initial input prototype.** Windows host and native iOS sources are present;
physical iOS/game validation is still required. Window/video streaming is planned
at **60 fps** and is not implemented yet. Use the PC display for this milestone.

- [Implementation plan and remaining milestones](docs/PLAN.md)
- [Control protocol](docs/PROTOCOL.md)
- [Validation results and hardware checks still needed](docs/VALIDATION.md)

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
The inherited burst-based serial command parsing still needs real-game checks;
see the plan's compatibility limit. The iOS layout preserves toucca's unusual
inner/outer radius acceptance, so the blank centre is not a guaranteed dead zone.

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

Workflows are checked in but are not run until this repository is pushed to
GitHub. No remote build or hardware validation is implied by their presence.

## Layout

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
[toucca](https://github.com/Pitiedwzr/toucca). The wired connection architecture
is informed by [Brokenithm-iOS](https://github.com/esterTion/Brokenithm-iOS).
