#pragma once
#include <stdint.h>
#include <stddef.h>

/* Windows x64, little endian. All access is under the corresponding named mutex.
 * Input and LED locks are independent. No pointers or compiler-sized bools in IPC. */
#define BC_IPC_MAGIC 0x50494342u
#define BC_LED_MAGIC 0x444c4342u
#define BC_IPC_VERSION 1u
#define BC_CAPACITY 64u
#define BC_PREFIX L"Local\\BROKENCCA_MERCURY_V1"
#define BC_WATCHDOG_MS 500u

struct bc_entry {
    uint32_t generation;
    uint32_t reserved;
    uint64_t received_qpc;
    uint64_t enqueued_qpc;
    uint8_t bitmap[30];
    uint8_t padding[2];
};
struct bc_input {
    uint32_t magic, version, generation, read_index, write_index, consumer_pid, producer_pid, period_us;
    uint64_t producer_tick, consumer_tick, completed_received, completed_at, completed_count;
    uint32_t watchdog_resets;
    uint8_t reserved[52];
    struct bc_entry entries[BC_CAPACITY];
};
struct bc_leds {
    uint32_t magic, version, unit_count, sequence;
    uint64_t updated_tick;
    uint8_t reserved[8];
    uint8_t rgba[1920];
};
_Static_assert(sizeof(struct bc_entry) == 56, "entry ABI");
_Static_assert(offsetof(struct bc_input, entries) == 128, "header ABI");
_Static_assert(sizeof(struct bc_input) == 3712, "input ABI");
_Static_assert(offsetof(struct bc_leds, rgba) == 32, "LED ABI");
_Static_assert(sizeof(struct bc_leds) == 1952, "LED size");
