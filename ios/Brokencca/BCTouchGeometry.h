// Adapted from toucca/web/controller.js, GPL-3.0-or-later.
#pragma once
#include <math.h>
#include <stdint.h>
#include <stdbool.h>

#define BC_SECTOR_MARGIN 0.25
#define BC_RADIAL_MARGIN 0.25

static inline int BCZoneAt(double x, double y, double width, double height) {
    if (!isfinite(x) || !isfinite(y) || !isfinite(width) || !isfinite(height) || width <= 0 || height <= 0) return -1;
    double dx = x - width / 2, dy = y - height / 2;
    if (dx == 0) return -1;
    double radius = fmin(width, height) / 2;
    double distance = sqrt(dx * dx + dy * dy);
    int ring = (distance > radius * 0.7) + (distance > radius * 0.8) + (distance > radius * 0.9);
    const double pi = 3.14159265358979323846;
    double angle = atan(dy / dx) * (dx < 0 ? -1 : 1);
    int sector = (int)floor((angle + pi / 2) / (pi / 30));
    if (sector < 0) sector = 0;
    if (sector > 29) sector = 29;
    return (dx < 0 ? 120 : 0) + ring * 30 + sector;
}

// Populates outZones with active zones (primary + boundary-expanded neighbors).
// Returns the number of zones written to outZones (0 to 4).
static inline int BCZonesForPoint(double x, double y, double width, double height, int outZones[4]) {
    if (!isfinite(x) || !isfinite(y) || !isfinite(width) || !isfinite(height) || width <= 0 || height <= 0) return 0;
    double dx = x - width / 2, dy = y - height / 2;
    double radius = fmin(width, height) / 2;
    double distance = sqrt(dx * dx + dy * dy);
    if (distance == 0 || radius <= 0) return 0;

    int primaryRing = (distance > radius * 0.7) + (distance > radius * 0.8) + (distance > radius * 0.9);
    int rings[2];
    int ringCount = 1;
    rings[0] = primaryRing;

    double ringMargin = BC_RADIAL_MARGIN * 0.1 * radius;
    if (primaryRing == 0 && distance > 0.7 * radius - ringMargin) {
        rings[ringCount++] = 1;
    } else if (primaryRing == 1) {
        if (distance < 0.7 * radius + ringMargin) rings[ringCount++] = 0;
        else if (distance > 0.8 * radius - ringMargin) rings[ringCount++] = 2;
    } else if (primaryRing == 2) {
        if (distance < 0.8 * radius + ringMargin) rings[ringCount++] = 1;
        else if (distance > 0.9 * radius - ringMargin) rings[ringCount++] = 3;
    } else if (primaryRing == 3 && distance < 0.9 * radius + ringMargin) {
        rings[ringCount++] = 2;
    }

    int sides[2];
    int sectors[2];
    int sectorCount = 0;

    if (dx == 0) {
        if (dy < 0) {
            sides[0] = 0; sectors[0] = 0;
            sides[1] = 1; sectors[1] = 0;
            sectorCount = 2;
        } else if (dy > 0) {
            sides[0] = 0; sectors[0] = 29;
            sides[1] = 1; sectors[1] = 29;
            sectorCount = 2;
        } else {
            return 0;
        }
    } else {
        int primarySide = dx < 0 ? 1 : 0;
        const double pi = 3.14159265358979323846;
        double angle = atan(dy / dx) * (dx < 0 ? -1 : 1);
        double sectorPos = (angle + pi / 2) / (pi / 30);
        int primarySector = (int)floor(sectorPos);
        if (primarySector < 0) primarySector = 0;
        if (primarySector > 29) primarySector = 29;

        sides[0] = primarySide;
        sectors[0] = primarySector;
        sectorCount = 1;

        double frac = sectorPos - primarySector;
        if (frac < BC_SECTOR_MARGIN) {
            if (primarySector > 0) {
                sides[1] = primarySide;
                sectors[1] = primarySector - 1;
                sectorCount = 2;
            } else {
                sides[1] = primarySide ^ 1;
                sectors[1] = 0;
                sectorCount = 2;
            }
        } else if (frac > 1.0 - BC_SECTOR_MARGIN) {
            if (primarySector < 29) {
                sides[1] = primarySide;
                sectors[1] = primarySector + 1;
                sectorCount = 2;
            } else {
                sides[1] = primarySide ^ 1;
                sectors[1] = 29;
                sectorCount = 2;
            }
        }
    }

    int total = 0;
    for (int r = 0; r < ringCount; r++) {
        for (int s = 0; s < sectorCount; s++) {
            outZones[total++] = sides[s] * 120 + rings[r] * 30 + sectors[s];
        }
    }
    return total;
}

static inline void BCApplyTouch(double x, double y, double width, double height, uint8_t *bitmap) {
    int zones[4];
    int count = BCZonesForPoint(x, y, width, height, zones);
    for (int i = 0; i < count; i++) {
        int z = zones[i];
        if (z >= 0 && z < 240) {
            bitmap[z / 8] |= (uint8_t)(1 << (z % 8));
        }
    }
}
