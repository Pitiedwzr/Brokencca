using System.Numerics;
using System.Runtime.InteropServices;
using Brokencca.Core;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.DXGI.DXGI;

namespace Brokencca.Capture.Windows;

/// <summary>GPU-only crop, aspect-fit, calibration guide, and procedural fixture rendering.</summary>
public sealed class GpuPresenter : IDisposable
{
    private readonly GpuDevice gpu;
    private readonly IDXGISwapChain1 swapChain = null!;
    private readonly ID3D11VertexShader vertex = null!;
    private readonly ID3D11PixelShader preview = null!, fixture = null!;
    private readonly ID3D11Buffer constants = null!;
    private readonly ID3D11SamplerState sampler = null!;
    private ID3D11RenderTargetView? target;
    private int width, height;
    private bool disposed;
    [StructLayout(LayoutKind.Sequential)] private struct Parameters { public Vector4 Crop, Source, Circle, Extra; }
    public long Presents { get; private set; }
    public long PresentBusy { get; private set; }
    public GpuPresenter(GpuDevice gpu, nint hwnd, int width, int height)
    {
        this.gpu = gpu; this.width = Math.Max(1, width); this.height = Math.Max(1, height);
        try
        {
        lock (gpu.Gate)
        {
            using var factory = CreateDXGIFactory2<IDXGIFactory2>(false);
            swapChain = factory.CreateSwapChainForHwnd(gpu.Device, hwnd, new SwapChainDescription1
            {
                Width = (uint)this.width, Height = (uint)this.height, Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new(1, 0),
                SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore
            });
            factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
            using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice1>();
            dxgiDevice.MaximumFrameLatency = 1;
            vertex = gpu.Device.CreateVertexShader(Compiler.Compile(Shader, "VS", "capture.hlsl", "vs_5_0").Span);
            preview = gpu.Device.CreatePixelShader(Compiler.Compile(Shader, "Preview", "capture.hlsl", "ps_5_0").Span);
            fixture = gpu.Device.CreatePixelShader(Compiler.Compile(Shader, "Fixture", "capture.hlsl", "ps_5_0").Span);
            constants = gpu.Device.CreateBuffer(64, BindFlags.ConstantBuffer);
            sampler = gpu.Device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
            CreateTarget();
        }
        }
        catch { Dispose(); throw; }
    }
    private void CreateTarget() { using var buffer = swapChain.GetBuffer<ID3D11Texture2D>(0); target = gpu.Device.CreateRenderTargetView(buffer); }
    public void Resize(int newWidth, int newHeight)
    {
        if (newWidth <= 0 || newHeight <= 0) return;
        lock (gpu.Gate)
        {
            if (width == newWidth && height == newHeight) return;
            gpu.Context.UnsetRenderTargets(); target?.Dispose(); target = null;
            swapChain.ResizeBuffers(2, (uint)newWidth, (uint)newHeight, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            width = newWidth; height = newHeight; CreateTarget();
        }
    }
    private void SetPipeline(ID3D11PixelShader shader, Parameters parameters, Viewport viewport)
    {
        var context = gpu.Context;
        context.ClearRenderTargetView(target!, new Color4(0, 0, 0, 1));
        context.OMSetRenderTargets(target!);
        context.RSSetViewport(viewport);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex); context.PSSetShader(shader);
        context.UpdateSubresource(in parameters, constants);
        context.PSSetConstantBuffer(0, constants); context.PSSetSampler(0, sampler);
    }
    public bool Present(CapturedFrameLease lease, PlayfieldCircle? circle = null)
    {
        lock (gpu.Gate)
        {
            var info = lease.Info;
            var viewport = new CaptureViewport(info.Crop, width, height);
            var parameters = new Parameters
            {
                Crop = new((float)info.Crop.X / info.Width, (float)info.Crop.Y / info.Height, (float)info.Crop.Width / info.Width, (float)info.Crop.Height / info.Height),
                Source = new(info.Width, info.Height, (float)(1.5 / viewport.Scale), 0)
            };
            if (circle is not null && info.GeometryResolved)
            {
                var (x, y, radius) = circle.ToPixels(info.Client.Width, info.Client.Height);
                parameters.Circle = new((float)(info.Client.X + x), (float)(info.Client.Y + y), (float)radius, 1);
            }
            SetPipeline(preview, parameters, new Viewport((float)viewport.Left, (float)viewport.Top, (float)(info.Crop.Width * viewport.Scale), (float)(info.Crop.Height * viewport.Scale)));
            using var view = gpu.Device.CreateShaderResourceView(lease.Texture);
            gpu.Context.PSSetShaderResource(0, view);
            gpu.Context.Draw(3, 0); gpu.Context.PSSetShaderResource(0, null!);
            return Submit();
        }
    }
    public bool PresentFixture(long frame, bool bootBlack)
    {
        lock (gpu.Gate)
        {
            var parameters = new Parameters { Source = new(width, height, 0, 0), Circle = new(width * .54f, height * .47f, Math.Min(width, height) * .39f, 1), Extra = new(frame % 1000000, bootBlack ? 1 : 0, 0, 0) };
            SetPipeline(fixture, parameters, new Viewport(0, 0, width, height));
            gpu.Context.Draw(3, 0); return Submit();
        }
    }
    private bool Submit()
    {
        // DXGI_ERROR_WAS_STILL_DRAWING means drop this presentation, never queue a wait.
        var result = swapChain.Present(0, PresentFlags.DoNotWait);
        if (result.Code == unchecked((int)0x887A000A)) { PresentBusy++; return false; }
        result.CheckError();
        if (result.Code != 0) return false; // e.g. DXGI_STATUS_OCCLUDED
        Presents++; return true;
    }
    public void Dispose()
    {
        lock (gpu.Gate)
        {
            if (disposed) return; disposed = true;
            gpu.Context.UnsetRenderTargets(); target?.Dispose(); sampler?.Dispose(); constants?.Dispose(); fixture?.Dispose(); preview?.Dispose(); vertex?.Dispose(); swapChain?.Dispose();
        }
    }
    private const string Shader = """
        cbuffer Params : register(b0) { float4 crop; float4 source; float4 circle; float4 extra; };
        Texture2D image : register(t0); SamplerState linearSampler : register(s0);
        struct Vertex { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        Vertex VS(uint id : SV_VertexID) {
            Vertex v; v.uv = float2((id << 1) & 2, id & 2);
            v.pos = float4(v.uv * float2(2,-2) + float2(-1,1), 0, 1); return v;
        }
        float4 Preview(Vertex v) : SV_Target {
            float2 uv = crop.xy + v.uv * crop.zw;
            float4 color = image.Sample(linearSampler, uv); color.a = 1;
            float2 p = uv * source.xy;
            float d = length(p-circle.xy);
            if (circle.w > 0 && (abs(d-circle.z) < source.z || (abs(p.x-circle.x)<source.z && abs(p.y-circle.y)<7*source.z) || (abs(p.y-circle.y)<source.z && abs(p.x-circle.x)<7*source.z))) return float4(1,.85,0,1);
            return color;
        }
        float digit(float2 p, int n) {
            int bits[10] = {63,6,91,79,102,109,125,7,127,111};
            float2 centers[7] = {float2(.5,0),float2(1,.5),float2(1,1.5),float2(.5,2),float2(0,1.5),float2(0,.5),float2(.5,1)};
            float lit=0;
            [unroll] for(int i=0;i<7;i++) { float2 q=abs(p-centers[i]); bool horizontal=(i==0||i==3||i==6); if ((bits[n]&(1<<i))!=0 && q.x<(horizontal?.38:.08) && q.y<(horizontal?.08:.38)) lit=1; }
            return lit;
        }
        float4 Fixture(Vertex v) : SV_Target {
            float2 p=v.pos.xy; float d=length(p-circle.xy);
            bool inside=d<circle.z;
            float3 color=extra.y>0?float3(0,0,0):float3(.08,.10,.16);
            if(inside) {
                color=float3(.17,.30,.42);
                float r=d/circle.z;
                if(abs(r-.6)<.006||abs(r-.7)<.006||abs(r-.8)<.006||abs(r-.9)<.006||abs(r-1)<.006) color=float3(.5,.8,1);
                if(abs(p.x-circle.x-sin(extra.x*.035)*circle.z*.8)<3) color=float3(.8,.35,.15);
                float2 q=(p-(circle.xy+float2(-65,-18)))/16;
                int col=(int)floor((q.x+.1)/1.4);
                if(col>=0&&col<6) { int value=(int)extra.x; int divisor=1; for(int k=0;k<5-col;k++)divisor*=10; if(digit(float2(q.x-col*1.4,q.y),value/divisor%10)>0)color=1; }
            }
            if(extra.y==0) {
                if(p.x<1||p.y<1||p.x>source.x-1||p.y>source.y-1)color=1;
                if(p.x<20&&p.y<20)color=float3(1,0,0);
                if(p.x>source.x-20&&p.y<20)color=float3(0,1,0);
                if(p.x<20&&p.y>source.y-20)color=float3(0,0,1);
                if(p.x>source.x-20&&p.y>source.y-20)color=float3(1,1,0);
            }
            return float4(color,1);
        }
        """;
}
