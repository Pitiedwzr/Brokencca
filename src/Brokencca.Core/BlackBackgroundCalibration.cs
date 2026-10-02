namespace Brokencca.Core;

public sealed record CircleDetection(PlayfieldCircle Circle, double Confidence, double BoundaryError, int ForegroundPixels);

/// <summary>
/// Conservative boot-frame calibration: fits the silhouette of the largest lit component.
/// Rejects dark frames, sparse logos, rectangles, clipped circles, and nonblack surroundings.
/// This suggests a guide; it never changes the video crop or confirms a profile.
/// </summary>
public static class BlackBackgroundCalibration
{
    public static CircleDetection? Detect(ReadOnlySpan<byte> bgra, int width, int height, int stride, byte blackThreshold = 24)
    {
        if (width <= 0 || height <= 0 || stride < checked(width * 4) || bgra.Length < checked(stride * height)) throw new ArgumentException("Invalid BGRA image.");
        int step = Math.Max(1, (Math.Max(width, height) + 511) / 512);
        int w = (width + step - 1) / step, h = (height + step - 1) / step;
        var mask = new bool[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int p = y * step * stride + x * step * 4;
            mask[y * w + x] = Math.Max(bgra[p], Math.Max(bgra[p + 1], bgra[p + 2])) > blackThreshold;
        }
        int[] labels = new int[mask.Length], queue = new int[mask.Length];
        int largest = 0, largestCount = 0, label = 0, totalLit = 0;
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] != 0) continue;
            label++; int head = 0, tail = 1; queue[0] = start; labels[start] = label;
            while (head < tail)
            {
                int i = queue[head++], x = i % w, y = i / w;
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
                void Visit(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;
                    int j = ny * w + nx;
                    if (!mask[j] || labels[j] != 0) return;
                    labels[j] = label; queue[tail++] = j;
                }
            }
            totalLit += tail;
            if (tail > largestCount) { largestCount = tail; largest = label; }
        }
        if (largestCount < Math.Max(40, w * h * .001) || totalLit - largestCount > w * h * .01) return null;
        List<(double X, double Y)> points = [];
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int left = w, right = -1;
            for (int x = 0; x < w; x++) if (labels[y * w + x] == largest) { left = Math.Min(left, x); right = x; }
            if (right < 0) continue;
            points.Add((left + .5, y + .5)); points.Add((right + .5, y + .5));
            minX = Math.Min(minX, left); maxX = Math.Max(maxX, right); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        for (int x = minX; x <= maxX; x++)
        {
            int top = h, bottom = -1;
            for (int y = minY; y <= maxY; y++) if (labels[y * w + x] == largest) { top = Math.Min(top, y); bottom = y; }
            if (bottom >= 0) { points.Add((x + .5, top + .5)); points.Add((x + .5, bottom + .5)); }
        }
        // Kasa least-squares fit: x*x+y*y = a*x+b*y+c, centered for numerical stability.
        double ox = (minX + maxX + 1) / 2.0, oy = (minY + maxY + 1) / 2.0;
        double[,] matrix = new double[3, 4];
        foreach (var p in points)
        {
            double x = p.X - ox, y = p.Y - oy, z = x * x + y * y;
            double[] row = [x, y, 1];
            for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) matrix[i, j] += row[i] * row[j]; matrix[i, 3] += row[i] * z; }
        }
        for (int i = 0; i < 3; i++)
        {
            int pivot = i;
            for (int k = i + 1; k < 3; k++) if (Math.Abs(matrix[k, i]) > Math.Abs(matrix[pivot, i])) pivot = k;
            if (Math.Abs(matrix[pivot, i]) < 1e-8) return null;
            for (int j = 0; j < 4; j++) (matrix[i, j], matrix[pivot, j]) = (matrix[pivot, j], matrix[i, j]);
            double scale = matrix[i, i]; for (int j = i; j < 4; j++) matrix[i, j] /= scale;
            for (int k = 0; k < 3; k++) if (k != i) { double factor = matrix[k, i]; for (int j = i; j < 4; j++) matrix[k, j] -= factor * matrix[i, j]; }
        }
        double cx = matrix[0, 3] / 2 + ox, cy = matrix[1, 3] / 2 + oy;
        double radius = Math.Sqrt(matrix[2, 3] + Math.Pow(cx - ox, 2) + Math.Pow(cy - oy, 2));
        if (!double.IsFinite(radius) || radius < Math.Min(w, h) * .3 || cx - radius < 1 || cy - radius < 1 || cx + radius > w - 1 || cy + radius > h - 1) return null;
        double[] errors = points.Select(p => Math.Abs(Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2)) - radius) / radius).Order().ToArray();
        double error = errors[(int)(errors.Length * .9)];
        if (error > Math.Max(.025, 1.5 / radius)) return null;
        // A continuous outer ring also defines a screen even if its interior is black.
        // Require almost complete angular support; sparse circular logos/arcs are ambiguous.
        if (largestCount < Math.PI * radius * radius * .55)
        {
            bool[] angles = new bool[72];
            foreach (var p in points)
            {
                double d = Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2));
                if (Math.Abs(d - radius) <= Math.Max(1.5, radius * .025))
                {
                    double angle = Math.Atan2(p.Y - cy, p.X - cx) + Math.PI;
                    angles[Math.Min(71, (int)(angle * 72 / (2 * Math.PI)))] = true;
                }
            }
            if (angles.Count(p => p) < 68) return null;
        }
        // Strong exterior check prevents treating a bright disk on a gray/colored image as boot black.
        int exterior = 0, exteriorLit = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            if (Math.Pow(x + .5 - cx, 2) + Math.Pow(y + .5 - cy, 2) > Math.Pow(radius + 2, 2)) { exterior++; if (mask[y * w + x]) exteriorLit++; }
        if (exterior < w * h * .1 || exteriorLit > exterior * .005) return null;
        var circle = new PlayfieldCircle(cx * step / width, cy * step / height, radius * step / Math.Min(width, height), "black-background");
        circle.Validate();
        return new(circle, Math.Clamp(1 - error * 8, 0, 1), error, largestCount * step * step);
    }
}
