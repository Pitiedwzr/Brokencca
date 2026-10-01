/* MercuryIO 1.0 ABI verified against segatools 8a966b2 games/mercuryio and
 * games/mercuryhook/elizabeth.h. See NOTICE; no changes to segatools required. */
#include <windows.h>
#include <process.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>
#include "bc_ipc.h"

struct led_data { DWORD unitCount; uint8_t rgba[480 * 4]; };
_Static_assert(sizeof(struct led_data) == 1924, "Mercury LED ABI");
typedef void (*touch_callback)(const bool *state);
static INIT_ONCE once = INIT_ONCE_STATIC_INIT;
static HANDLE input_map, led_map, input_mutex, led_mutex, work, stop, thread, consumer_owner;
static struct bc_input *input;
static struct bc_leds *leds;
static touch_callback callback;
static volatile LONG opbtn, gamebtn;
static int vk_test = VK_F1, vk_service = VK_F2, vk_coin = VK_F3, vk_up = VK_UP, vk_down = VK_DOWN;
static LARGE_INTEGER frequency;

static HANDLE named_object(const wchar_t *prefix, const wchar_t *suffix, int kind, DWORD size)
{
    wchar_t name[200];
    if (swprintf(name, 200, L"%ls%ls", prefix, suffix) < 0) return NULL;
    if (kind == 0) return CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, size, name);
    if (kind == 1) return CreateMutexW(NULL, FALSE, name);
    if (kind == 2) return CreateEventW(NULL, FALSE, FALSE, name);
    return CreateSemaphoreW(NULL, 1, 1, name);
}
static bool take(HANDLE mutex, DWORD timeout)
{
    DWORD result = WaitForSingleObject(mutex, timeout);
    return result == WAIT_OBJECT_0 || result == WAIT_ABANDONED;
}
static BOOL CALLBACK initialize(PINIT_ONCE ignored, PVOID parameter, PVOID *context)
{
    (void)ignored; (void)parameter; (void)context;
    wchar_t prefix[128];
    DWORD length = GetEnvironmentVariableW(L"BROKENCCA_IPC_PREFIX", prefix, 128);
    if (length >= 128) return FALSE;
    if (!length) wcscpy(prefix, BC_PREFIX);
    input_mutex = named_object(prefix, L".InputMutex", 1, 0);
    led_mutex = named_object(prefix, L".LedMutex", 1, 0);
    work = named_object(prefix, L".Work", 2, 0);
    consumer_owner = named_object(prefix, L".ConsumerOwner", 3, 0);
    input_map = named_object(prefix, L".Input", 0, sizeof(*input));
    led_map = named_object(prefix, L".Leds", 0, sizeof(*leds));
    stop = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (!input_mutex || !led_mutex || !work || !consumer_owner || !input_map || !led_map || !stop) goto fail;
    input = MapViewOfFile(input_map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(*input));
    leds = MapViewOfFile(led_map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(*leds));
    if (!input || !leds || !take(input_mutex, 1000)) goto fail;
    if (!input->magic) { input->magic = BC_IPC_MAGIC; input->version = BC_IPC_VERSION; input->period_us = 4167; }
    bool valid = input->magic == BC_IPC_MAGIC && input->version == BC_IPC_VERSION;
    ReleaseMutex(input_mutex);
    if (!valid || !take(led_mutex, 1000)) goto fail;
    if (!leds->magic) { leds->magic = BC_LED_MAGIC; leds->version = BC_IPC_VERSION; }
    valid = leds->magic == BC_LED_MAGIC && leds->version == BC_IPC_VERSION;
    ReleaseMutex(led_mutex);
    QueryPerformanceFrequency(&frequency);
    if (valid) return TRUE;
fail:
    if (input) { UnmapViewOfFile(input); input = NULL; }
    if (leds) { UnmapViewOfFile(leds); leds = NULL; }
    HANDLE handles[] = { input_map, led_map, input_mutex, led_mutex, work, stop, consumer_owner };
    for (size_t i = 0; i < sizeof(handles) / sizeof(handles[0]); i++) if (handles[i]) CloseHandle(handles[i]);
    input_map = led_map = input_mutex = led_mutex = work = stop = consumer_owner = NULL;
    return FALSE;
}
static HRESULT ensure(void) { return InitOnceExecuteOnce(&once, initialize, NULL, NULL) ? S_OK : E_FAIL; }
uint16_t mercury_io_get_api_version(void) { return 0x0100; }
HRESULT mercury_io_init(void)
{
    wchar_t config[1024];
    DWORD length = GetEnvironmentVariableW(L"SEGATOOLS_CONFIG_PATH", config, 1024);
    if (!length || length >= 1024) wcscpy(config, L".\\segatools.ini");
    vk_test = GetPrivateProfileIntW(L"io4", L"test", VK_F1, config);
    vk_service = GetPrivateProfileIntW(L"io4", L"service", VK_F2, config);
    vk_coin = GetPrivateProfileIntW(L"io4", L"coin", VK_F3, config);
    vk_up = GetPrivateProfileIntW(L"io4", L"volup", VK_UP, config);
    vk_down = GetPrivateProfileIntW(L"io4", L"voldown", VK_DOWN, config);
    return ensure();
}
HRESULT mercury_io_poll(void)
{
    LONG op = (GetAsyncKeyState(vk_test) & 0x8000 ? 1 : 0) |
        (GetAsyncKeyState(vk_service) & 0x8000 ? 2 : 0) | (GetAsyncKeyState(vk_coin) & 0x8000 ? 4 : 0);
    LONG game = (GetAsyncKeyState(vk_up) & 0x8000 ? 1 : 0) | (GetAsyncKeyState(vk_down) & 0x8000 ? 2 : 0);
    InterlockedExchange(&opbtn, op); InterlockedExchange(&gamebtn, game);
    return S_OK;
}
void mercury_io_get_opbtns(uint8_t *value) { if (value) *value = (uint8_t)InterlockedCompareExchange(&opbtn, 0, 0); }
void mercury_io_get_gamebtns(uint8_t *value) { if (value) *value = (uint8_t)InterlockedCompareExchange(&gamebtn, 0, 0); }
HRESULT mercury_io_touch_init(void) { return ensure(); }

static void expand(const uint8_t bitmap[30], bool cells[240])
{
    /* This fork routes cells 0..119 to game COM3 (our COM5), 120..239 to
     * COM4 (our COM6). Brokencca's frontend already has that order; WACVR's
     * frontend halves differ, so its IPC half swap must NOT be repeated here. */
    for (unsigned i = 0; i < 240; i++) cells[i] = (bitmap[i / 8] & (1u << (i % 8))) != 0;
}
static unsigned __stdcall run(void *ignored)
{
    (void)ignored;
    bool cells[240] = { false };
    uint32_t current_generation = 0;
    uint64_t last_callback = 0;
    bool expired = false;
    HANDLE waits[] = { stop, work };
    HANDLE pacing = CreateWaitableTimerExW(NULL, NULL, 0x2 /* high resolution */, TIMER_ALL_ACCESS);
    if (!pacing) pacing = CreateWaitableTimerW(NULL, FALSE, NULL);
    while (WaitForSingleObject(stop, 0) != WAIT_OBJECT_0) {
        struct bc_entry entry = {0};
        bool changed = false;
        uint32_t period = 4167;
        if (!take(input_mutex, 10)) { WaitForMultipleObjects(2, waits, FALSE, 10); continue; }
        uint64_t now = GetTickCount64();
        input->consumer_pid = GetCurrentProcessId(); input->consumer_tick = now;
        period = input->period_us;
        if (period < 1000 || period > 16667) period = 4167;
        bool stale = !input->producer_pid || now - input->producer_tick > BC_WATCHDOG_MS;
        if (stale) {
            if (!expired) {
                input->generation++; input->read_index = input->write_index = 0;
                input->watchdog_resets++; memset(cells, 0, sizeof(cells)); changed = true;
            }
            expired = true;
        } else {
            expired = false;
            uint32_t depth = input->write_index - input->read_index;
            if (depth > BC_CAPACITY) {
                input->generation++; input->read_index = input->write_index = 0;
                memset(cells, 0, sizeof(cells)); changed = true;
            } else if (depth) {
                entry = input->entries[input->read_index % BC_CAPACITY];
                input->read_index++;
                if (entry.generation == input->generation) { expand(entry.bitmap, cells); changed = true; }
            } else if (current_generation != input->generation) {
                memset(cells, 0, sizeof(cells)); changed = true;
            }
        }
        current_generation = input->generation;
        ReleaseMutex(input_mutex); // Never call the game's callback while holding IPC locks.
        LARGE_INTEGER qpc; QueryPerformanceCounter(&qpc);
        if (changed || !last_callback || (uint64_t)qpc.QuadPart - last_callback >= (uint64_t)frequency.QuadPart / 10) {
            callback(cells);
            QueryPerformanceCounter(&qpc); last_callback = qpc.QuadPart;
            if (take(input_mutex, 10)) {
                input->consumer_tick = GetTickCount64();
                input->completed_count++;
                input->completed_received = entry.received_qpc;
                input->completed_at = qpc.QuadPart;
                ReleaseMutex(input_mutex);
            }
            /* Configurable maximum callback rate protects this segatools fork's
             * 520-byte emulated UART buffers. This is not proof of game consumption. */
            LARGE_INTEGER due; due.QuadPart = -(LONGLONG)period * 10;
            if (pacing && SetWaitableTimer(pacing, &due, 0, NULL, NULL, FALSE)) {
                HANDLE paced[] = { stop, pacing };
                WaitForMultipleObjects(2, paced, FALSE, 20);
            } else {
                WaitForSingleObject(stop, (period + 999) / 1000);
            }
        } else {
            WaitForMultipleObjects(2, waits, FALSE, 50);
        }
    }
    memset(cells, 0, sizeof(cells)); callback(cells);
    if (take(input_mutex, 100)) { input->consumer_pid = 0; input->consumer_tick = 0; ReleaseMutex(input_mutex); }
    if (pacing) CloseHandle(pacing);
    ReleaseSemaphore(consumer_owner, 1, NULL);
    return 0;
}
void mercury_io_touch_start(touch_callback value)
{
    if (!value || FAILED(ensure()) || thread) return;
    if (WaitForSingleObject(consumer_owner, 0) != WAIT_OBJECT_0) {
        OutputDebugStringA("Brokencca: another MercuryIO consumer owns this IPC namespace\n"); return;
    }
    /* segatools has no unload/stop export in this ABI. Pin while callbacks run;
     * optional stop below is for tests/controlled shutdown, never DllMain joins. */
    HMODULE pinned;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        (LPCWSTR)(uintptr_t)mercury_io_touch_start, &pinned);
    callback = value; ResetEvent(stop);
    if (!take(input_mutex, 100)) { ReleaseSemaphore(consumer_owner, 1, NULL); return; }
    // New game/board startup must not replay history queued while no game consumed it.
    input->generation++; input->read_index = input->write_index = 0;
    input->consumer_pid = GetCurrentProcessId(); input->consumer_tick = GetTickCount64();
    ReleaseMutex(input_mutex);
    thread = (HANDLE)_beginthreadex(NULL, 0, run, NULL, 0, NULL);
    if (!thread) {
        if (take(input_mutex, 100)) { input->consumer_pid = 0; ReleaseMutex(input_mutex); }
        ReleaseSemaphore(consumer_owner, 1, NULL);
    }
}
void mercury_io_touch_stop(void)
{
    if (!thread) return;
    SetEvent(stop);
    WaitForSingleObject(thread, INFINITE);
    CloseHandle(thread); thread = NULL;
}
void mercury_io_touch_set_leds(struct led_data data)
{
    if (FAILED(ensure()) || !take(led_mutex, 0)) return; // LEDs may drop; input never waits for them.
    leds->unit_count = data.unitCount;
    memcpy(leds->rgba, data.rgba, sizeof(leds->rgba)); // Do not overwrite alpha with an IPC flag.
    leds->updated_tick = GetTickCount64(); leds->sequence++;
    ReleaseMutex(led_mutex);
}
