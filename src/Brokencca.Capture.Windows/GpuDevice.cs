using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace Brokencca.Capture.Windows;

/// <summary>All immediate-context use is serialized on Gate, including consumers.</summary>
public sealed class GpuDevice : IDisposable
{
    public object Gate { get; } = new();
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public string Adapter { get; }
    private bool disposed;
    public GpuDevice()
    {
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device!; Context = context!;
        try
        {
            using var dxgi = Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            Adapter = adapter.Description.Description;
        }
        catch { Context.Dispose(); Device.Dispose(); throw; }
    }
    public ID3D11Texture2D CreateTexture(int width, int height, bool staging = false) => Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
        Usage = staging ? ResourceUsage.Staging : ResourceUsage.Default,
        BindFlags = staging ? BindFlags.None : BindFlags.ShaderResource,
        CPUAccessFlags = staging ? CpuAccessFlags.Read : CpuAccessFlags.None
    });
    internal bool IsComplete(ID3D11Query query)
    {
        var result = Context.GetData(query, 0, 0, AsyncGetDataFlags.DoNotFlush);
        result.CheckError();
        return result.Code == 0;
    }
    /// <summary>Explicit diagnostic readback only; not part of steady-state preview.</summary>
    public byte[] Readback(ID3D11Texture2D texture, int width, int height)
    {
        lock (Gate)
        {
            using var staging = CreateTexture(width, height, true);
            Context.CopyResource(staging, texture);
            var map = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte[] result = new byte[checked(width * height * 4)];
                for (int y = 0; y < height; y++) Marshal.Copy(map.DataPointer + checked(y * (int)map.RowPitch), result, y * width * 4, width * 4);
                return result;
            }
            finally { Context.Unmap(staging, 0); }
        }
    }
    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed) return; disposed = true;
            Context.ClearState(); Context.Flush(); Context.Dispose(); Device.Dispose();
        }
    }
}
