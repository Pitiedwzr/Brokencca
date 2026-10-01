#pragma once
#include <stdint.h>
#include <string.h>
#include "BCWire.h"

enum { BCLedHeaderSize = 16, BCLedPayloadSize = 1924, BCLedPacketSize = 1940 };
static inline int BCValidLedHeader(const uint8_t *p) {
    return !memcmp(p, "BCLD", 4) && p[4] == 1 && !p[5] && !p[6] && !p[7] && BCGet32(p + 8) == BCLedPayloadSize;
}
/* WACVR LightManager's sector-major, outer-to-inner layout, two LEDs/cell.
 * Orientation must still be verified in the game's LED test screen. */
static inline int BCLedIndexForZone(int zone) {
    if (zone < 0 || zone >= 240) return -1;
    // WACVR frontend zone 120 corresponds to Brokencca zone 0 (game COM3).
    zone = (zone + 120) % 240;
    int side = zone / 120, ring = (zone % 120) / 30, sector = zone % 30;
    return 2 * (side * 120 + (side == 0 ? 29 - sector : sector) * 4 + 3 - ring);
}
