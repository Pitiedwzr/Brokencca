// Adapted from toucca/web/controller.js, GPL-3.0-or-later.
#pragma once
#include <math.h>

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
