#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>
#include "BCWire.h"

enum { BCVideoHeaderSize = 40, BCVideoMaxJson = 16384, BCVideoMaxAU = 2097152 };
enum { BCVideoHello = 1, BCVideoHelloAck = 2, BCVideoConfig = 3, BCVideoReady = 4,
       BCVideoAU = 5, BCVideoFeedback = 6, BCVideoRequestIDR = 7, BCVideoClockPing = 8,
       BCVideoClockPong = 9, BCVideoStatus = 10, BCVideoError = 11 };

static inline uint64_t BCGet64(const uint8_t *p) {
    return (uint64_t)BCGet32(p) | ((uint64_t)BCGet32(p + 4) << 32);
}
static inline void BCPut64(uint8_t *p, uint64_t value) {
    BCPut32(p, (uint32_t)value); BCPut32(p + 4, (uint32_t)(value >> 32));
}
static inline void BCVideoHeader(uint8_t *p, uint8_t type, bool idr, uint32_t length,
                                  uint32_t sequence, uint64_t generation, uint64_t frame,
                                  uint64_t capturedUs) {
    memset(p, 0, BCVideoHeaderSize);
    memcpy(p, "BCVD", 4); p[4] = 1; p[5] = type; p[6] = idr ? 1 : 0;
    BCPut32(p + 8, length); BCPut32(p + 12, sequence);
    BCPut64(p + 16, generation); BCPut64(p + 24, frame); BCPut64(p + 32, capturedUs);
}
static inline bool BCValidVideoHeader(const uint8_t *p) {
    if (memcmp(p, "BCVD", 4) || p[4] != 1 || p[5] < 1 || p[5] > 11 || p[7] ||
        (p[5] == BCVideoAU ? p[6] > 1 : p[6] != 0)) return false;
    uint32_t length = BCGet32(p + 8);
    if (length == 0 || length > (p[5] == BCVideoAU ? BCVideoMaxAU : BCVideoMaxJson)) return false;
    bool handshake = p[5] == BCVideoHello || p[5] == BCVideoHelloAck;
    uint64_t generation = BCGet64(p + 16), frame = BCGet64(p + 24), timestamp = BCGet64(p + 32);
    if (handshake ? generation != 0 : p[5] != BCVideoError && generation == 0) return false;
    return p[5] == BCVideoAU ? frame != 0 && timestamp != 0 : frame == 0 && timestamp == 0;
}
static inline bool BCValidAvcc(const uint8_t *p, size_t length, bool markedIdr) {
    size_t offset = 0;
    bool picture = false, idr = false;
    while (offset < length) {
        if (length - offset < 5) return false;
        uint32_t nalLength = ((uint32_t)p[offset] << 24) | ((uint32_t)p[offset + 1] << 16) |
                             ((uint32_t)p[offset + 2] << 8) | p[offset + 3];
        offset += 4;
        if (nalLength == 0 || nalLength > length - offset) return false;
        uint8_t type = p[offset] & 31;
        if (type == 1 || type == 5) picture = true;
        if (type == 5) idr = true;
        offset += nalLength;
    }
    return picture && idr == markedIdr;
}
