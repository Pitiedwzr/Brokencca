#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../ios/Brokencca/BCTouchGeometry.h"
#include "../ios/Brokencca/BCWire.h"
#include "../ios/Brokencca/BCVideoWire.h"
#include "../ios/Brokencca/BCVideoJson.h"
#include "../ios/Brokencca/BCLed.h"

int main(void) {
    const char *good_ready = "{\"hardwareVerified\":false}";
    const char *duplicate_ready = "{\"hardwareVerified\":true,\"hardwareVerified\":false}";
    const char *extra_ready = "{\"hardwareVerified\":true,\"extra\":0}";
    const char *escaped_ready = "{\"hardware\\u0056erified\":true}";
    const char *nested_ready = "{\"hardwareVerified\":{\"value\":true}}";
    assert(BCVideoJSONFields((const uint8_t *)good_ready,strlen(good_ready),BCVideoReady));
    assert(!BCVideoJSONFields((const uint8_t *)duplicate_ready,strlen(duplicate_ready),BCVideoReady));
    assert(!BCVideoJSONFields((const uint8_t *)extra_ready,strlen(extra_ready),BCVideoReady));
    assert(!BCVideoJSONFields((const uint8_t *)escaped_ready,strlen(escaped_ready),BCVideoReady));
    assert(!BCVideoJSONFields((const uint8_t *)nested_ready,strlen(nested_ready),BCVideoReady));
    assert(BCZoneAt(500, 500, 1000, 1000) == -1);
    assert(BCZoneAt(501, 500, 1000, 1000) == 15);
    assert(BCZoneAt(1100, 500, 1000, 1000) == 105);
    assert(BCZoneAt(850, 500, 1000, 1000) == 15);
    assert(BCZoneAt(850.01, 500, 1000, 1000) == 45);
    assert(BCZoneAt(NAN, 500, 1000, 1000) == -1);
    const double pi = 3.14159265358979323846;
    for (int side = 0; side < 2; side++) for (int ring = 0; ring < 4; ring++) for (int sector = 0; sector < 30; sector++) {
        double angle = -pi / 2 + (sector + 0.5) * pi / 30;
        double radius = 500 * (0.65 + ring * 0.1);
        double x = 500 + cos(angle) * radius * (side == 0 ? 1 : -1);
        double y = 500 + sin(angle) * radius;
        int expected = side * 120 + ring * 30 + sector;
        assert(BCZoneAt(x, y, 1000, 1000) == expected);
        int z[4];
        int count = BCZonesForPoint(x, y, 1000, 1000, z);
        assert(count == 1);
        assert(z[0] == expected);
    }

    // Boundary expansion tests
    int z[4];
    // Center point returns 0
    assert(BCZonesForPoint(500, 500, 1000, 1000, z) == 0);

    // Sector boundary expansion within same side (sector 5 close to 4)
    {
        double angle = -pi / 2 + (5.0 + 0.1) * pi / 30;
        double radius = 500 * 0.65;
        double x = 500 + cos(angle) * radius;
        double y = 500 + sin(angle) * radius;
        int count = BCZonesForPoint(x, y, 1000, 1000, z);
        assert(count == 2);
        assert(z[0] == 5 && z[1] == 4);
    }

    // Sector boundary expansion across top 12 o'clock (side 0, sector 0 close to side 1, sector 0)
    {
        double angle = -pi / 2 + (0.0 + 0.1) * pi / 30;
        double radius = 500 * 0.65;
        double x = 500 + cos(angle) * radius;
        double y = 500 + sin(angle) * radius;
        int count = BCZonesForPoint(x, y, 1000, 1000, z);
        assert(count == 2);
        assert(z[0] == 0 && z[1] == 120);
    }

    // Sector boundary expansion across bottom 6 o'clock (side 0, sector 29 close to side 1, sector 29)
    {
        double angle = -pi / 2 + (29.0 + 0.9) * pi / 30;
        double radius = 500 * 0.65;
        double x = 500 + cos(angle) * radius;
        double y = 500 + sin(angle) * radius;
        int count = BCZonesForPoint(x, y, 1000, 1000, z);
        assert(count == 2);
        assert(z[0] == 29 && z[1] == 149);
    }

    // Radial boundary expansion across ring 0 and ring 1 (sector 5 center, radius 500 * 0.69)
    {
        double angle = -pi / 2 + 5.5 * pi / 30;
        double radius = 500 * 0.69;
        double x = 500 + cos(angle) * radius;
        double y = 500 + sin(angle) * radius;
        int count = BCZonesForPoint(x, y, 1000, 1000, z);
        assert(count == 2);
        assert(z[0] == 5 && z[1] == 35);
    }

    // Corner expansion (both sector 5/4 and ring 0/1) -> 4 zones
    {
        double angle = -pi / 2 + (5.0 + 0.1) * pi / 30;
        double radius = 500 * 0.69;
        double x = 500 + cos(angle) * radius;
        double y = 500 + sin(angle) * radius;
        int count = BCZonesForPoint(x, y, 1000, 1000, z);
        assert(count == 4);
        assert(z[0] == 5 && z[1] == 4 && z[2] == 35 && z[3] == 34);

        // Verify BCApplyTouch sets all 4 bits in bitmap
        uint8_t bitmap[30] = {0};
        BCApplyTouch(x, y, 1000, 1000, bitmap);
        for (int i = 0; i < 240; i++) {
            bool set = (bitmap[i / 8] & (1 << (i % 8))) != 0;
            bool expected_set = (i == 5 || i == 4 || i == 35 || i == 34);
            assert(set == expected_set);
        }
    }

    const uint8_t expected[] = {0x42,0x43,0x43,0x41,1,1,0,0,4,0,0,0,0x44,0x33,0x22,0x11,8,7,6,5,4,3,2,1};
    uint8_t header[BCHeaderSize];
    BCHeader(header, BCHello, 4, 0x11223344, UINT64_C(0x0102030405060708));
    assert(memcmp(header, expected, sizeof(header)) == 0);
    assert(BCGet32(header + 12) == 0x11223344);
    uint8_t video_hello[BCHeaderSize + 24] = {0};
    BCHeaderVersion(video_hello, 2, BCHello, 24, 0, 8);
    const uint8_t layout[] = {240, 0, 30, 0};
    memcpy(video_hello + BCHeaderSize, layout, 4);
    BCPut32(video_hello + BCHeaderSize + 4, BCVideoCapability);
    assert(BCValidHello(video_hello, sizeof(video_hello)));
    video_hello[BCHeaderSize + 4] = 2;
    assert(!BCValidHello(video_hello, sizeof(video_hello)));
    uint8_t video_header[BCVideoHeaderSize];
    BCVideoHeader(video_header, BCVideoAU, true, 6, 0x11223344,
        UINT64_C(0x0102030405060708), UINT64_C(0x1112131415161718), UINT64_C(0x2122232425262728));
    const uint8_t expected_video_header[] = {
        0x42,0x43,0x56,0x44,1,5,1,0,6,0,0,0,0x44,0x33,0x22,0x11,
        8,7,6,5,4,3,2,1,0x18,0x17,0x16,0x15,0x14,0x13,0x12,0x11,
        0x28,0x27,0x26,0x25,0x24,0x23,0x22,0x21
    };
    assert(memcmp(video_header, expected_video_header, sizeof(video_header)) == 0);
    assert(BCValidVideoHeader(video_header));
    const uint8_t video_au[] = {0, 0, 0, 2, 0x65, 0x88};
    assert(BCValidAvcc(video_au, sizeof(video_au), true));
    assert(!BCValidAvcc(video_au, sizeof(video_au), false));
    video_header[6] = 2;
    assert(!BCValidVideoHeader(video_header));
    uint8_t led_header[BCLedHeaderSize] = {'B','C','L','D',1,0,0,0};
    BCPut32(led_header + 8, BCLedPayloadSize); BCPut32(led_header + 12, UINT32_MAX);
    assert(BCValidLedHeader(led_header));
    for (int offset = 0; offset < 12; offset++) {
        uint8_t copy[BCLedHeaderSize]; memcpy(copy, led_header, sizeof(copy)); copy[offset] ^= 0x80;
        assert(!BCValidLedHeader(copy));
    }
    bool used[480] = { false };
    for (int zone = 0; zone < 240; zone++) {
        int index = BCLedIndexForZone(zone);
        assert(index >= 0 && index < 479 && !(index % 2));
        assert(!used[index] && !used[index + 1]); used[index] = used[index + 1] = true;
    }
    assert(BCLedIndexForZone(0) == 246 && BCLedIndexForZone(119) == 472);
    assert(BCLedIndexForZone(120) == 238 && BCLedIndexForZone(239) == 0);
    assert(BCLedIndexForZone(-1) == -1 && BCLedIndexForZone(240) == -1);
    puts("PASS iOS geometry, wire fixtures, LED header and all 480 LED mappings");
    return 0;
}
