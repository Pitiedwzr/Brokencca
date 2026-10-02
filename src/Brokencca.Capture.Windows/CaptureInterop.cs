using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Brokencca.Capture.Windows;

internal static class CaptureInterop
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IItemInterop
    {
        nint CreateForWindow(nint hwnd, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }
    [DllImport("combase.dll", PreserveSig = false)]
    private static extern void RoGetActivationFactory(nint classId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IItemInterop factory);
    [DllImport("combase.dll")] private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string text, int length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("d3d11.dll", PreserveSig = false)] private static extern void CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint inspectable);

    public static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out nint text));
        try
        {
            Guid iid = typeof(IItemInterop).GUID;
            RoGetActivationFactory(text, ref iid, out var factory);
            try
            {
                Guid itemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                nint pointer = factory.CreateForWindow(hwnd, ref itemId);
                try { return MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer); }
                finally { Marshal.Release(pointer); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        finally { WindowsDeleteString(text); }
    }
    public static IDirect3DDevice WrapDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint pointer);
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }
    // Optional Windows 11 24H2 interface. Keep the Windows 10 projection/runtime baseline.
    // ABI source: microsoft/windows-rs Windows/Graphics/Capture IGraphicsCaptureSession5.
    public static unsafe bool UncapUpdates(GraphicsCaptureSession session)
    {
        var inspectable = MarshalInspectable<GraphicsCaptureSession>.CreateMarshaler2(session);
        try
        {
            Guid session5 = new("67C0EA62-1F85-5061-925A-239BE0AC09CB");
            int hr = Marshal.QueryInterface(inspectable.GetAbi(), ref session5, out nint pointer);
            if (hr == unchecked((int)0x80004002)) return false;
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var setter = (delegate* unmanaged[Stdcall]<nint, long, int>)(*(nint**)pointer)[7];
                Marshal.ThrowExceptionForHR(setter(pointer, 0)); return true;
            }
            finally { Marshal.Release(pointer); }
        }
        finally { inspectable.Dispose(); }
    }
    public static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var inspectable = MarshalInspectable<IDirect3DSurface>.CreateMarshaler2(surface);
        try
        {
            Guid accessId = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable.GetAbi(), ref accessId, out nint access));
            try
            {
                Guid textureId = typeof(ID3D11Texture2D).GUID;
                nint texture = 0;
                var call = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[3];
                Marshal.ThrowExceptionForHR(call(access, &textureId, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally { Marshal.Release(access); }
        }
        finally { inspectable.Dispose(); }
    }
}
