using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Brokencca.Core;

namespace Brokencca.Capture.Windows;

public sealed record WindowIdentity(nint Hwnd, uint ProcessId, long? StartTime, string Title, string ClassName, string? ProcessName)
{
    public bool IsAlive => Native.IsWindow(Hwnd) && Native.GetWindowThreadProcessId(Hwnd, out uint pid) != 0 && pid == ProcessId && (StartTime is null || GetStart(pid) == StartTime);
    internal static long? GetStart(uint pid) { try { using var p = Process.GetProcessById((int)pid); return p.StartTime.ToUniversalTime().Ticks; } catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; } }
}

public sealed record WindowGeometry(PixelRect ClientScreen, PixelRect OuterScreen, PixelRect VisibleScreen, uint Dpi, bool Minimized)
{
    public static WindowGeometry Read(nint hwnd)
    {
        if (!Native.GetClientRect(hwnd, out var client) || !Native.GetWindowRect(hwnd, out var outer)) throw new System.ComponentModel.Win32Exception();
        var start = new Native.Point { X = client.Left, Y = client.Top };
        var end = new Native.Point { X = client.Right, Y = client.Bottom };
        if (!Native.ClientToScreen(hwnd, ref start) || !Native.ClientToScreen(hwnd, ref end)) throw new System.ComponentModel.Win32Exception();
        if (Native.DwmGetWindowAttribute(hwnd, 9, out var visible, Marshal.SizeOf<Native.Rect>()) < 0) visible = outer;
        return new(new(start.X, start.Y, end.X - start.X, end.Y - start.Y), outer.ToPixels(), visible.ToPixels(), Native.GetDpiForWindow(hwnd), Native.IsIconic(hwnd));
    }

    public bool TryClientInTexture(int width, int height, out PixelRect result)
    {
        result = default;
        // WGC normally uses DWM visible bounds. Refuse mismatched styles/resize frames.
        if (Minimized || !ClientScreen.IsPositive || VisibleScreen.Width != width || VisibleScreen.Height != height) return false;
        result = new(ClientScreen.X - VisibleScreen.X, ClientScreen.Y - VisibleScreen.Y, ClientScreen.Width, ClientScreen.Height);
        return result.X >= 0 && result.Y >= 0 && result.Right <= width && result.Bottom <= height;
    }
}

public static class WindowLocator
{
    public const string MercuryTitle = "Mercury  ";
    public static IReadOnlyList<WindowIdentity> List()
    {
        List<WindowIdentity> result = [];
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (Native.DwmGetWindowAttributeUInt(hwnd, 14, out uint cloaked, sizeof(uint)) >= 0 && cloaked != 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return true;
            var title = new StringBuilder(1024); var className = new StringBuilder(256);
            Native.GetWindowText(hwnd, title, title.Capacity); Native.GetClassName(hwnd, className, className.Capacity);
            if (title.Length == 0) return true;
            string? name = null;
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            result.Add(new(hwnd, pid, WindowIdentity.GetStart(pid), title.ToString(), className.ToString(), name));
            return true;
        }, 0);
        return result;
    }
    public static WindowIdentity Select(nint hwnd)
    {
        var window = List().SingleOrDefault(w => w.Hwnd == hwnd) ?? throw new ArgumentException("HWND is not a live visible top-level source window.");
        if (IsOverlay(window)) throw new ArgumentException("The controller/preview overlay cannot be a capture source.");
        return window;
    }
    public static bool IsOverlay(WindowIdentity window) => window.Title == "Toucca" || string.Equals(window.ProcessName, "toucca", StringComparison.OrdinalIgnoreCase) || window.Title.StartsWith("Brokencca Capture Preview", StringComparison.Ordinal);
    public static IReadOnlyList<WindowIdentity> MercuryCandidates() => List().Where(w => w.Title == MercuryTitle && !IsOverlay(w)).ToArray();
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public readonly PixelRect ToPixels() => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    internal delegate bool EnumCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect rect, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int DwmGetWindowAttributeUInt(nint hwnd, int attribute, out uint value, int size);
}
