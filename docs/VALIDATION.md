# Implementation validation

## User-reported hardware acceptance

The user has tested the prototype and considers it suitable as a prototype release.
These are user-reported results, not new measurements made by the assistant:

| Check | Reported result |
| --- | --- |
| IPA installation and operation | Working |
| Multitouch and dry-run input | Working |
| Apple device services and iproxy on the development laptop | Working |
| Host on a separate WACCA machine, with COM5/COM6 | Game communication and touch recognition working |
| Actual gameplay | Latency usually imperceptible; noticeable delay with more contacts, faster slides, and rapid taps |
| Extreme ten-contact rapid movement | Reported host "touch overflow", followed by successful recovery after a 1-second retry |

Tested host command:

```text
Brokencca.Host.exe --iproxy "./iproxy/iproxy.exe" --serial --left COM5 --right COM6
```

This establishes a playable wired-input prototype on the tested setup. Device
models, exact build IDs, quantitative timing, all-zone coverage, and session
duration were not supplied. The report does not identify the exact overflow
message or conclusively localize its source. In the current source, the serial
sink has a 64-state overflow guard, the iOS sender has a 64-pending-write guard,
and the dry-run console sink has no touch queue. Capture the exact message and
mode in the next diagnostic build rather than attributing all delays to serial.

Next work is defined in [NEXT-STEPS.md](NEXT-STEPS.md).

## Initial automated checks

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

## Still needing validation or improvement

- Exact artifact/build provenance and installation method for reproducibility;
  successful installation is established, but workflow logs were not reviewed.
- Cable removal, background/foreground, rotation, device selection, and game
  restart behavior beyond the reported overflow recovery.
- Exhaustive all-240-zone validation and startup on other game/driver versions.
- Burst-based serial request parsing under fragmented/coalesced game traffic;
  successful gameplay does not establish every possible command boundary.
- Quantitative latency/jitter, sustained input rate, overload source, and
  preservation/game sampling of very short taps. The reported load-dependent
  latency is the next milestone's primary issue.
- Capture, encoding, video streaming, or audio: not implemented in this milestone.

The automated checks establish behavior under simulation; the user's device and
game tests additionally establish practical compatibility on their setup. Neither
is a quantitative latency guarantee. See PLAN.md for the remaining device checklist.
