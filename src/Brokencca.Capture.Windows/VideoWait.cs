using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Brokencca.Capture.Windows;

/// <summary>Local high-resolution video wait; does not change global timer resolution.</summary>
public sealed class VideoWait : IDisposable
{
    private readonly SafeWaitHandle timer;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, nint routine, nint argument, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
    public VideoWait()
    {
        timer = CreateWaitableTimerEx(0, null, 2, 0x1F0003);
        if (timer.IsInvalid) { timer.Dispose(); timer = CreateWaitableTimerEx(0, null, 0, 0x1F0003); }
        if (timer.IsInvalid) throw new System.ComponentModel.Win32Exception();
    }
    public void Milliseconds(double milliseconds)
    {
        long due = -Math.Max(1, (long)(Math.Clamp(milliseconds, .1, 20) * 10000));
        if (!SetWaitableTimer(timer, ref due, 0, 0, 0, false)) throw new System.ComponentModel.Win32Exception();
        if (WaitForSingleObject(timer, 50) == uint.MaxValue) throw new System.ComponentModel.Win32Exception();
    }
    public void Dispose() => timer.Dispose();
}
