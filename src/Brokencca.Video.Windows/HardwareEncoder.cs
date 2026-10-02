using System.Runtime.InteropServices;
using System.Text;
using Brokencca.Capture.Windows;
using Brokencca.Core;
using Vortice.Direct3D11;

namespace Brokencca.Video.Windows;

/// <summary>Thread-confined MF worker; native tracked samples own NV12 surfaces until released.</summary>
public sealed class HardwareEncoder : IDisposable
{
    private readonly GpuDevice gpu;
    private nint handle;
    private readonly byte[] output = new byte[VideoProtocol.MaxAccessUnit];
    public string Name { get; }
    public HardwareEncoder(GpuDevice gpu, int width, int height, int bitrate)
    {
        this.gpu = gpu;
        var detail = new StringBuilder(512);
        int hr = Create(gpu.Device.NativePointer, (uint)width, (uint)height, (uint)bitrate, out handle, detail, 512);
        if (hr < 0) throw new IOException($"Hardware encoder unavailable: {detail} (0x{hr:X8}).");
        Name = detail.ToString();
    }
    public bool Submit(CapturedFrameLease frame, long time100ns, PixelRect content)
        => SubmitTexture(frame.Texture, frame.Info.Crop, time100ns, content);
    public bool SubmitTexture(ID3D11Texture2D texture, PixelRect crop, long time100ns, PixelRect content)
    {
        int hr;
        lock (gpu.Gate) hr = SubmitNative(handle, texture.NativePointer, crop.X, crop.Y, crop.Width,
            crop.Height, time100ns, content.X, content.Y, content.Width, content.Height);
        if (hr < 0)
        {
            var desc = texture.Description;
            CheckNative(hr, $"GPU conversion/submission of {desc.Width}x{desc.Height} {desc.Format} texture (bind={desc.BindFlags}, crop={crop}, content={content})");
        }
        return hr == 0;
    }
    public (byte[] Bytes, long Time100ns)? Poll()
    {
        int hr = PollNative(handle, output, (uint)output.Length, out uint length, out long pts);
        CheckNative(hr, "Hardware encoder input/output processing");
        return hr == 0 ? (output.AsSpan(0, checked((int)length)).ToArray(), pts) : null;
    }
    public void ForceIdr() => CheckNative(ForceNative(handle), "Hardware encoder IDR request");
    public byte[] Headers()
    {
        byte[] bytes = new byte[4096];
        int hr = HeadersNative(handle, bytes, (uint)bytes.Length, out uint length);
        CheckNative(hr, "Reading hardware encoder parameter sets"); return bytes.AsSpan(0, checked((int)length)).ToArray();
    }
    private static void CheckNative(int hr, string operation)
    {
        if (hr < 0) throw new IOException($"{operation} failed (HRESULT 0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
    }
    public void Dispose() { if (handle != 0) { Destroy(handle); handle = 0; } }
    private const string Dll = "brokencca-video.dll";
    [DllImport(Dll, EntryPoint = "bc_video_create", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int Create(nint device, uint width, uint height, uint bitrate, out nint result, StringBuilder detail, uint detailSize);
    [DllImport(Dll, EntryPoint = "bc_video_submit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SubmitNative(nint encoder, nint texture, int x, int y, int width, int height, long pts, int left, int top, int targetWidth, int targetHeight);
    [DllImport(Dll, EntryPoint = "bc_video_poll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PollNative(nint encoder, byte[] bytes, uint capacity, out uint length, out long pts);
    [DllImport(Dll, EntryPoint = "bc_video_idr", CallingConvention = CallingConvention.Cdecl)] private static extern int ForceNative(nint encoder);
    [DllImport(Dll, EntryPoint = "bc_video_destroy", CallingConvention = CallingConvention.Cdecl)] private static extern void Destroy(nint encoder);
    [DllImport(Dll, EntryPoint = "bc_video_headers", CallingConvention = CallingConvention.Cdecl)] private static extern int HeadersNative(nint encoder, byte[] bytes, uint capacity, out uint length);
}
