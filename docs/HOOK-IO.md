# Optional MercuryIO input and LEDs

Implemented in `0.2.0-mercury-hook`, alongside the unchanged serial worker.
Experimental until tested in the user's game. The brief post-worker-fix serial
stress test reportedly no longer overflowed; full serial acceptance is still
pending, independently of this new backend.

## Compatibility and scope

ABI inspected: the user's segatools checkout `8a966b2454185699e8b7d256f4ab43384c274ba0`,
README version `2026-04-06`, specifically `games/mercuryio/mercuryio.h`,
`mercuryhook/mercury-dll.c`, `touch.c`, and `elizabeth.h/.c`. API version is
`0x0100`; LEDs use a **by-value** struct containing DWORD `unitCount` and
480 RGBA entries (1,924 bytes). All seven required loader symbols plus version
are exported. An extra stop export supports automated tests. Operator/volume
keyboard mappings still use `[io4]` and `SEGATOOLS_CONFIG_PATH`.

Touch callback cells 0..119 feed game COM3, and 120..239 feed COM4. This already
matches Brokencca's frontend/COM5/COM6 ordering, so the adapter does **not** copy
WACVR's frontend half swap. Native tests reconstruct the fork's bit packing and
compare every cell against Brokencca's serial fixtures.

Only Brokencca's new IO DLL is built: no other games, no changes to segatools,
and no replacement `mercuryhook.dll`. Do not replace unrelated loader DLLs.
The IO module is pinned for process lifetime, matching this ABI's lack of a
loader shutdown callback; stop never joins a worker inside DllMain.

**Hook does not bypass every serial layer.** This fork's touch callback encodes
input into emulated COM3/COM4 UART buffers inside the game process. We bypass
external COM5/COM6, com0com, and Brokencca's board command parser. The callback
has no game-consumption acknowledgement and ignores failed buffer writes;
hook delivery is therefore not proof that the game sampled every edge.

## Build and install

```powershell
./scripts/build.ps1
dotnet publish src/Brokencca.Host -c Release -r win-x64 --self-contained true -o artifacts/windows-hook
./scripts/build-hook.ps1 -Test
```

The native build needs **x86_64 MinGW-w64 GCC** (`-Compiler` can specify its full
path). CI builds the DLL with GCC, tests it, and includes it in the Windows
artifact. The tests also launch the packaged host against a fake native game
when the EXE is present in the selected output directory. No real COM ports or
game processes are touched by these tests.

1. Back up the existing relevant INI sections. Copy `brokencca-mercuryio.dll`
   from the Windows publish folder into the game's `bin` folder.
2. Select the hook in your existing segatools configuration:

   ```ini
   [touch]
   enable=1

   [mercuryio]
   path=brokencca-mercuryio.dll

   [elizabeth]
   enable=1
   ```

3. Keep the host publish folder intact. Start:

   ```powershell
   .\Brokencca.Host.exe --hook --diagnostics --iproxy .\iproxy\iproxy.exe
   ```

4. Start the game. `hook_connected:true` indicates a live native callback worker,
   not proof that both game boards are scanning. Wait for game startup before
   stressing input. Validate both halves and every ring before normal play.

Choose **one** touch backend per run. Serial remains `--serial` with
`[touch] enable=0`; switching requires restarting the game after changing its
configuration. Do not run WACVR/toucca as a competing input source.
Use the same Windows session and compatible privilege level for host/game;
the bridge does not grant shared-memory access to Everyone.

## Enable LEDs on the iPad

Hook touch alone works with the existing IPA. **LED display requires the new
IPA** containing `BCLEDTransport` (iOS project version 0.2.0, build 2), built and
signed through the existing macOS/CI installation workflow.

```powershell
.\Brokencca.Host.exe --hook --leds --diagnostics --iproxy .\iproxy\iproxy.exe
```

The host adds a second port pair to iproxy. With an external bridge, run:

```powershell
iproxy -l 24864:24864 24866:24866
```

Multiple pairs are supported by the tested iproxy 2.1.1 syntax, verified against
its [upstream source](https://github.com/libimobiledevice/libusbmuxd/blob/2.1.1/tools/iproxy.c).
`--led-port` changes the Windows loopback port only; the device listens on
24866. Port 24865 remains available for future video.

```text
iOS touches -> USB/control 24864 -> host bounded IPC FIFO -> MercuryIO -> game
game LED callback -> separate LED IPC -> host -> USB/LED 24866 -> iOS display
```

LEDs are latest-frame data at up to 30 fps, not an input FIFO. Native LED writes
never wait for the LED mutex; the host never waits on LED networking from its
touch path. LED socket errors do not reset input. The iOS renderer retains at
most one pending main-thread update plus the latest frame. Stale/disconnected
LEDs clear after approximately one second; local cyan touch feedback remains
immediate. No control-v1 message types or input payloads changed.

All 480 RGBA values and raw `unitCount` are transported unchanged. The UI uses
RGB (as WACVR does), two LED subcells per input zone, and WACVR's sector-major,
outer-to-inner ordering adjusted for Brokencca's opposite frontend half order.
Alpha is **not** reused as an IPC liveness flag. Physical
orientation and the two subcells' order remain hardware-validation items.

## Input ordering, scheduling, and failure handling

- Single producer and single native consumer per IPC namespace; duplicate hosts
  are rejected. Each changed 30-byte snapshot enters a 64-entry FIFO. Exact
  duplicates alone are suppressed. Overflow clears history, releases, and fails
  the input session; it does not become a larger queue or a latest-state slot.
- Resets advance a generation and enqueue release. A callback already committed
  outside the mutex may finish first, then release/new input follows. No old
  queued state replays after the release. Game startup/restart discards history
  accumulated while no game was consuming it; a fresh full snapshot restores
  currently held touches after startup.
- A named event wakes the native worker. High-resolution waitable-timer pacing
  caps callbacks at `--hook-rate 240` by default (60..1000 allowed). This avoids
  flooding the fork's 520-byte UART buffers, which hold roughly 14 input frames.
  This is an explicit experimental calibration setting, not an assumption that
  game polling is 240 Hz. Increasing it can overflow **downstream** even if the
  shared-memory FIFO is healthy. Callback duration also limits capacity.
- A producer lease updates every 50 ms. If the host dies/stalls for more than
  500 ms, the native worker discards pending history and releases input. Graceful
  host stop marks it offline immediately. A dead callback is detected even on
  duplicate input heartbeats. A blocked game thread cannot be forcibly made to
  consume a release; likewise this fork freezes prior input when unfocused.

IPC uses versioned fixed-width little-endian fields, separate input/LED mutexes,
and immutable copied packets. Default prefix: `Local\BROKENCCA_MERCURY_V1`.
`BROKENCCA_IPC_PREFIX` can isolate tests; if set, **both** processes must inherit
the same Local name (less than 128 characters). C static assertions and real
C#/DLL interop tests check the layout.

## Diagnostics and retest

Host logs contain `sink:"hook"`, API/rate/LED mode, queue high-water/overflows,
current `hook_queue_depth`, `hook_connected`, and `hook_watchdog_resets_total`.
`receive_to_hook_sampled_ms` samples the latest completed callback about every
50 ms using the shared Windows QPC clock. It is **not every callback**, not a
pooled latency percentile, and not game-visible latency. Idle/reset callbacks
are excluded. Serial fields remain specific to the serial backend.

LED fields: `led_capture_sequence`, `led_capture_age_ms`, `led_payloads_sent`.
An advancing capture sequence confirms game LED callbacks; a high age indicates
stale/no capture. Stream write completion alone does not confirm display. iOS
logs `BCCA_LED received=... displayed=... connected=... age_ms=...` alongside the
existing input `BCCA_DIAG` records.

Retest in one game launch where possible:

1. All rings/both halves; rapid press-release-repress; 60-second ten-finger stress.
   Require correct game-visible edges, zero overflow/reconnect, bounded backlog.
2. Game LED tests: solid red/green/blue, asymmetric left/right/sector patterns,
   ring patterns, then normal lighting. Check orientation and channel ordering.
3. Disable LED forwarding/close only its connection; input must continue. Compare
   identical touch workloads with LEDs off/on and with the existing serial path.
4. Held-touch cable removal, suspension, rotation, host exit/crash, and game
   restart. Require release/recovery. Capture input-display evidence, not just
   successful callback counts.
5. Thirty-minute gameplay with both input and LEDs enabled. No stuck input,
   growing delay/backlog, or accumulating rendering work.

Hardware acceptance and UIKit compilation are pending. The C#/native/portable-C
tests do not establish compatibility with every game/segatools fork or actual
game sampling. Keep serial available as the known playable fallback.
