#include <assert.h>
#include <stdio.h>
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
        assert(BCZoneAt(x, y, 1000, 1000) == side * 120 + ring * 30 + sector);
    }
    const uint8_t expected[] = {0x42,0x43,0x43,0x41,1,1,0,0,4,0,0,0,0x44,0x33,0x22,0x11,8,7,6,5,4,3,2,1};
    uint8_t header[BCHeaderSize];
    BCHeader(header, BCHello, 4, 0x11223344, UINT64_C(0x0102030405060708));
    assert(memcmp(header, expected, sizeof(header)) == 0);
    assert(BCGet32(header + 12) == 0x11223344);
    puts("PASS iOS geometry (240 centres + boundaries) and wire golden fixture");
    return 0;
}
