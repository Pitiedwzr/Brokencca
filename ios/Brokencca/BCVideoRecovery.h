#pragma once
#include <stdbool.h>
#include <stdint.h>

enum { BCVideoDecodeSlots = 3 };
// A brief decode stall keeps its references and applies bounded backpressure.
// Sustained stalls request a new IDR generation. Late output is never replayed.
static inline bool BCVideoDecodeStalled(uint64_t oldestUs, uint64_t nowUs) {
    return oldestUs && nowUs>oldestUs && nowUs-oldestUs>150000;
}
static inline bool BCVideoDecodedFrameFresh(uint64_t receivedUs, uint64_t nowUs) {
    return receivedUs && nowUs>=receivedUs && nowUs-receivedUs<=50000;
}
