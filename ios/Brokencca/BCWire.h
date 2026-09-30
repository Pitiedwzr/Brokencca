#pragma once
#include <stdint.h>
#include <string.h>

enum { BCHeaderSize = 24, BCHello = 1, BCTouch = 2, BCReset = 3 };
static inline void BCPut32(uint8_t *p, uint32_t n) {
    for (int i = 0; i < 4; i++) p[i] = (uint8_t)(n >> (i * 8));
}
static inline uint32_t BCGet32(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static inline void BCHeader(uint8_t *p, uint8_t type, uint32_t length, uint32_t seq, uint64_t time) {
    memset(p, 0, BCHeaderSize);
    memcpy(p, "BCCA", 4); p[4] = 1; p[5] = type;
    BCPut32(p + 8, length); BCPut32(p + 12, seq);
    BCPut32(p + 16, (uint32_t)time); BCPut32(p + 20, (uint32_t)(time >> 32));
}
