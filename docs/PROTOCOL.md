# Brokencca control protocol v1

iOS listens on loopback TCP port 24864. Windows connects through usbmux/iproxy.
TCP is a byte stream: reads can split or combine frames. All multibyte integers
are unsigned **little-endian**, independent of native struct layout.

| Offset | Bytes | Field |
| --- | --- | --- |
| 0 | 4 | ASCII `BCCA` |
| 4 | 1 | Version = 1 |
| 5 | 1 | Message type |
| 6 | 2 | Reserved flags = 0 |
| 8 | 4 | Payload length (header excluded), maximum 4096 |
| 12 | 4 | Per-direction sequence number |
| 16 | 8 | Sender monotonic microseconds; diagnostic, not a shared clock |
| 24 | N | Payload |

| Type | Value | Payload |
| --- | --- | --- |
| HELLO | 1 | `F0 00 1E 00`: uint16 zone count 240, uint16 bitmap bytes 30 |
| TOUCH | 2 | Exactly 30 bytes, least-significant bit first per byte |
| RESET | 3 | Empty; releases every zone and clears queued input |
| PING | 4 | Empty; reserved, not accepted in the input-only session |
| PONG | 5 | Empty; reserved, not accepted in the input-only session |

Windows sends HELLO first. iOS validates and replies HELLO, then sends an empty
TOUCH. During normal use iOS sends each changed snapshot immediately, and repeats
the current full state every 100 ms. HELLO consumes a sequence number. Sequences
must increase modulo 2^32 (forward distance less than 2^31); gaps are allowed.
The host handshake deadline is 3 s; each subsequent complete message has a 500 ms
deadline, including header and payload. Wrong type/version/layout, malformed
length, stale sequence, EOF, timeout, and cancellation all reset host input.

The prototype has one controller. No second connection may take over a live iOS
session. A reconnect starts from an empty state; old snapshots are never replayed.
Heartbeat repetitions may be suppressed by the serial sink, but changed states
are processed in FIFO order. A queue overflow disconnects and resets instead of
discarding arbitrary touch transitions.

## Layout

Zones 0..119: right half; 120..239: left half. Each half has four rings of 30
sectors, ordered top to bottom. Ring thresholds are .7/.8/.9 of half the smaller
view dimension. Boundaries belong to the inner ring. The drawn inner radius is
.6, but toucca compatibility deliberately accepts inner and outer touches.
Touches exactly on the vertical centre line are excluded.

The Windows serial mapper includes toucca's half swap. Frontend zone 0 becomes
bit 0 in COM5 packet byte 1; zone 119 becomes bit 4 in COM5 packet byte 24.
Zones 120..239 map equivalently onto COM6. Bytes 0 and 34 are the packet header
and 7-bit rolling counter. XOR of all 36 packet bytes equals 0x80.

For the optional MercuryIO backend, frontend zones map directly to callback
cells: this fork packs cells 0..119 into emulated game COM3, and 120..239 into
COM4. This produces the same touch data fields as the external COM5/COM6 path;
applying WACVR's frontend-specific half swap here would incorrectly swap halves.

## Golden HELLO frame

Sequence `0x11223344`, timestamp `0x0102030405060708`:

```text
42 43 43 41 01 01 00 00 04 00 00 00 44 33 22 11
08 07 06 05 04 03 02 01 F0 00 1E 00
```

The C# and iOS C header tests use this same fixture. Video framing is a later
milestone and must negotiate a different payload limit and stream generation.
The control-v2 codec and iOS HELLO parser now accept a 24-byte HELLO containing
the existing four layout bytes, a uint32 video capability (1), and a 16-byte
session token. The host-side v2 session is opt-in with `--video`; the
current CLI still starts v1 input sessions. V2 keeps TOUCH/RESET layouts and the
input timeout/sequence rules. The portable video-v1 framing and JSON validation
helpers and live video owners are implemented. Video uses a separate listener
on 24865, HELLO token binding, CONFIG/READY, and an initial IDR before normal
access units. See [VIDEO-USAGE.md](VIDEO-USAGE.md) for build/launch instructions
and [VIDEO-STREAMING.md](VIDEO-STREAMING.md) for the video wire contract.
See [VIDEO-STREAMING.md](VIDEO-STREAMING.md) for the full proposed session flow.

## Optional LED stream v1 (independent of control)

The updated iOS app also listens on loopback port **24866**. `--hook --leds`
forwards latest complete game LED snapshots over a separate usbmux connection.
No extra message type, capability, or payload is sent on control-v1 port 24864;
old IPAs still work for touch. They simply lack the optional LED listener.

| Offset | Bytes | Field |
| --- | --- | --- |
| 0 | 4 | ASCII `BCLD` |
| 4 | 1 | LED stream version = 1 |
| 5 | 3 | Reserved = 0 |
| 8 | 4 | Payload length = 1924 |
| 12 | 4 | Sequence, increasing modulo 2^32 as for control |
| 16 | 4 | Raw Mercury `unitCount` (not interpreted as a buffer length) |
| 20 | 1920 | 480 RGBA quadruplets in native game order |

Total packet size: 1940 bytes. Host starts sending without HELLO after TCP
connect; only host-to-iOS LED frames are supported. Fragmented/coalesced reads
are handled. Bad magic/version/reserved fields/length, stale sequence, EOF,
and approximately one second without a complete frame close only the LED
connection and clear its display. The input listener/session is untouched.

Output is capped at 30 fps and retains latest-frame rather than input history.
The host's socket write has a 250 ms deadline; a stalled LED client reconnects
independently. No LED send or receive can renew the input heartbeat lease.
LED clocks are local; the wire has no cross-device latency timestamp. See
[HOOK-IO.md](HOOK-IO.md) for physical mapping and game-validation limitations.
