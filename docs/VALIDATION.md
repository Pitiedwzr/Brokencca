# Initial implementation validation

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

## Not yet validated

- Full Objective-C app compilation or XcodeGen generation on macOS. The workflow
  is prepared, but this new repository has no GitHub remote and no workflow run.
- iOS signing, installation, multitouch behavior, suspension, or USB transport.
- Apple device service / iproxy interoperability on this laptop.
- Physical or virtual serial ports and the real WACCA startup protocol.
- Burst-based serial request parsing under fragmented/coalesced game traffic.
- Real touch latency, sustained input rate, or very short game-visible taps.
- Capture, encoding, video streaming, or audio: not implemented in this milestone.

These checks establish host/protocol behavior under simulation, not an end-to-end
hardware compatibility or performance claim. See PLAN.md for the device checklist.
