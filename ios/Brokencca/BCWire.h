#pragma once
#include <stdint.h>
#include <string.h>

enum { BCHeaderSize = 24, BCHello = 1, BCTouch = 2, BCReset = 3, BCVideoCapability = 1 };
static inline void BCPut32(uint8_t *p, uint32_t n) {
    for (int i = 0; i < 4; i++) p[i] = (uint8_t)(n >> (i * 8));
}
static inline uint32_t BCGet32(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static inline void BCHeaderVersion(uint8_t *p, uint8_t version, uint8_t type, uint32_t length, uint32_t seq, uint64_t time) {
    memset(p, 0, BCHeaderSize);
    memcpy(p, "BCCA", 4); p[4] = version; p[5] = type;
    BCPut32(p + 8, length); BCPut32(p + 12, seq);
    BCPut32(p + 16, (uint32_t)time); BCPut32(p + 20, (uint32_t)(time >> 32));
}
static inline void BCHeader(uint8_t *p, uint8_t type, uint32_t length, uint32_t seq, uint64_t time) {
    BCHeaderVersion(p, 1, type, length, seq, time);
}
static inline bool BCValidHello(const uint8_t *packet, size_t length) {
    if (length < BCHeaderSize || memcmp(packet, "BCCA", 4) || packet[5] != BCHello || packet[6] || packet[7]) return false;
    uint8_t version = packet[4];
    uint32_t payload = BCGet32(packet + 8);
    if (version == 1 ? payload != 4 : version == 2 ? payload != 24 : true) return false;
    if (length != BCHeaderSize + payload) return false;
    const uint8_t layout[] = {240, 0, 30, 0};
    if (memcmp(packet + BCHeaderSize, layout, 4)) return false;
    return version != 2 || BCGet32(packet + BCHeaderSize + 4) == BCVideoCapability;
}
