#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../ios/Brokencca/BCTouchGeometry.h"
#include "../ios/Brokencca/BCWire.h"

int main(void) {
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
    puts("PASS iOS geometry (240 centres + boundaries + expansion) and wire golden fixture");
    return 0;
}
