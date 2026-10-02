using System.Diagnostics;
using Brokencca.Core;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics;

namespace Brokencca.Capture.Windows;

public enum CaptureState { Starting, Running, Paused, Recreating, Stopped, Faulted }
public sealed record CaptureOptions(NormalizedCrop Crop, bool FullItem = false, int FramesPerSecond = 60)
{
    public void Validate() { Crop.Validate(); if (FramesPerSecond is < 1 or > 60) throw new ArgumentException("Capture fps must be between 1 and 60."); }
}
public sealed record CapturedFrameInfo(long Id, long Timestamp100ns, int Width, int Height, PixelRect Client, PixelRect Crop, WindowGeometry Geometry, long GeometryGeneration, long SessionGeneration, bool GeometryResolved);

public sealed class CapturedFrameLease : IDisposable
{
    private WgcCaptureSession? owner;
    private readonly int index;
    public CapturedFrameInfo Info { get; }
    public ID3D11Texture2D Texture { get; }
    internal CapturedFrameLease(WgcCaptureSession owner, int index, ID3D11Texture2D texture, CapturedFrameInfo info) { this.owner = owner; this.index = index; Texture = texture; Info = info; }
    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Return(index);
}

/// <summary>WGC lifetime never escapes the callback. Owned leases must be returned before disposal.</summary>
public sealed class WgcCaptureSession : IDisposable
{
    private sealed class Slot(ID3D11Texture2D texture, ID3D11Query fence) : IDisposable
    {
        public ID3D11Texture2D Texture { get; } = texture;
        public ID3D11Query Fence { get; } = fence;
        public CapturedFrameInfo? Info;
        public void Dispose() { Fence.Dispose(); Texture.Dispose(); }
    }
    private static long nextGeneration;
    private readonly GpuDevice gpu;
    private readonly CaptureOptions options;
    private readonly GraphicsCaptureItem item;
    private readonly IDirect3DDevice winDevice;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private FrameSlotPool slots = new();
    private Slot[] textures = [];
    private int width, height, closed, needsDrain;
    private long nextCapture, id, geometryGeneration = 1;
    private long nextIdentityCheck;
    private WindowGeometry? lastGeometry;
    private bool stopped;
    private string reason = "starting";
    private CaptureState state = CaptureState.Starting;
    private Exception? failure;
    private long arrivals, submitted, droppedBusy, droppedRate, droppedSlots, replaced, recreates, lastArrival, callbackTicks;
    public WindowIdentity Source { get; }
    public long SessionGeneration { get; } = Interlocked.Increment(ref nextGeneration);
    public bool UpdateIntervalUncapped { get; }
    public CaptureState State { get { lock (gpu.Gate) return state; } }
    public Exception? Failure { get { lock (gpu.Gate) return failure; } }
    public WgcCaptureSession(GpuDevice gpu, WindowIdentity source, CaptureOptions options)
    {
        options.Validate();
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("Windows Graphics Capture is unavailable. Use an unlocked Windows 10 2004+ desktop with a supported graphics driver.");
        if (!source.IsAlive) throw new ArgumentException("Source window identity changed.");
        this.gpu = gpu; this.options = options; Source = source;
        item = CaptureInterop.CreateItem(source.Hwnd);
        winDevice = CaptureInterop.WrapDevice(gpu.Device);
        width = Math.Max(1, item.Size.Width); height = Math.Max(1, item.Size.Height);
        Direct3D11CaptureFramePool? createdPool = null;
        GraphicsCaptureSession? createdSession = null;
        try
        {
            createdPool = Direct3D11CaptureFramePool.CreateFreeThreaded(winDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32 { Width = width, Height = height });
            createdSession = createdPool.CreateCaptureSession(item);
            pool = createdPool; session = createdSession;
            session.IsCursorCaptureEnabled = false;
            UpdateIntervalUncapped = CaptureInterop.UncapUpdates(session);
            lock (gpu.Gate) Allocate();
            item.Closed += Closed;
            pool.FrameArrived += FrameArrived;
            session.StartCapture();
        }
        catch
        {
            item.Closed -= Closed;
            if (createdPool is not null) createdPool.FrameArrived -= FrameArrived;
            createdSession?.Dispose(); createdPool?.Dispose();
            foreach (var texture in textures) texture.Dispose();
            winDevice.Dispose(); throw;
        }
    }
    private void Allocate()
    {
        List<Slot> made = [];
        try
        {
            for (int i = 0; i < 3; i++)
            {
                var texture = gpu.CreateTexture(width, height);
                try { made.Add(new(texture, gpu.Device.CreateQuery(new QueryDescription(QueryType.Event)))); }
                catch { texture.Dispose(); throw; }
            }
            textures = made.ToArray(); slots = new();
        }
        catch { foreach (var s in made) s.Dispose(); throw; }
    }
    private void Closed(GraphicsCaptureItem sender, object args) => Interlocked.Exchange(ref closed, 1);
    private void PollFences()
    {
        for (int i = 0; i < textures.Length; i++)
            if (slots.States[i] == FrameSlotState.Retiring && gpu.IsComplete(textures[i].Fence)) slots.Complete(i);
    }
    private void FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        long start = Stopwatch.GetTimestamp();
        if (!Monitor.TryEnter(gpu.Gate)) { Interlocked.Exchange(ref needsDrain, 1); Interlocked.Increment(ref droppedBusy); return; }
        try
        {
            if (stopped || state == CaptureState.Faulted) return;
            Direct3D11CaptureFrame? latest = null;
            try
            {
                for (var frame = sender.TryGetNextFrame(); frame is not null; frame = sender.TryGetNextFrame())
                { arrivals++; latest?.Dispose(); latest = frame; }
                if (latest is null) return;
                lastArrival = start;
                if (Volatile.Read(ref closed) != 0 || (start >= nextIdentityCheck && !Source.IsAlive)) { state = CaptureState.Stopped; reason = "source-closed"; return; }
                if (start >= nextIdentityCheck) nextIdentityCheck = start + Stopwatch.Frequency * 3 / 10;
                var geometry = WindowGeometry.Read(Source.Hwnd);
                if (geometry.Minimized || !geometry.ClientScreen.IsPositive) { state = CaptureState.Paused; reason = "minimized-or-empty"; slots.InvalidatePending(); return; }
                int newWidth = latest.ContentSize.Width, newHeight = latest.ContentSize.Height;
                if (newWidth <= 0 || newHeight <= 0) return;
                PollFences();
                if (newWidth != width || newHeight != height)
                {
                    state = CaptureState.Recreating; reason = "resize"; slots.InvalidatePending(); PollFences();
                    if (!slots.IsQuiescent) { gpu.Context.Flush(); return; }
                    latest.Dispose(); latest = null;
                    foreach (var oldTexture in textures) oldTexture.Dispose(); textures = [];
                    width = newWidth; height = newHeight; geometryGeneration++; recreates++; nextCapture = 0;
                    sender.Recreate(winDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32 { Width = width, Height = height });
                    Allocate(); lastGeometry = null; return;
                }
                if (lastGeometry != geometry)
                { slots.InvalidatePending(); geometryGeneration++; lastGeometry = geometry; }
                bool resolved = geometry.TryClientInTexture(width, height, out var client);
                var crop = options.FullItem || !resolved ? new PixelRect(0, 0, width, height) : options.Crop.ToPixels(client);
                // A mismatched style may need a whole session restart, not guessed offsets.
                state = CaptureState.Running; reason = resolved ? "running" : "geometry-unresolved: full-item diagnostic view";
                long interval = Stopwatch.Frequency / options.FramesPerSecond;
                if (start + Stopwatch.Frequency / 1000 < nextCapture) { droppedRate++; return; }
                nextCapture = nextCapture == 0 || start - nextCapture > interval ? start + interval : nextCapture + interval;
                using var texture = CaptureInterop.GetTexture(latest.Surface);
                var desc = texture.Description;
                // A transient resize/minimize surface can be smaller than ContentSize.
                // Validate before reserving a slot: a query must never retire before End.
                if (desc.Width < width || desc.Height < height) return;
                int oldPending = slots.Pending;
                int index = slots.Reserve();
                if (index < 0) { droppedSlots++; return; }
                if (oldPending >= 0) replaced++;
                // The frame pool may have a larger surface after a resize; only copy valid content.
                gpu.Context.CopySubresourceRegion(textures[index].Texture, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
                gpu.Context.End(textures[index].Fence); gpu.Context.Flush();
                textures[index].Info = new(++id, latest.SystemRelativeTime.Ticks, width, height, client, crop, geometry, geometryGeneration, SessionGeneration, resolved);
                submitted++;
            }
            finally { latest?.Dispose(); }
        }
        catch (Exception e) { failure = e; state = CaptureState.Faulted; reason = $"capture-failed: 0x{e.HResult:X8} {e.Message}"; }
        finally { callbackTicks += Stopwatch.GetTimestamp() - start; Monitor.Exit(gpu.Gate); }
    }
    public CapturedFrameLease? TryAcquire()
    {
        lock (gpu.Gate)
        {
            if (stopped || state is CaptureState.Faulted or CaptureState.Stopped || Volatile.Read(ref closed) != 0) return null;
            // A full WGC pool may stop raising events after a callback skipped a busy GPU.
            // Drain on the consumer worker as well; all pool access still holds the same gate.
            if (Interlocked.Exchange(ref needsDrain, 0) != 0) FrameArrived(pool, null!);
            if (state is CaptureState.Faulted or CaptureState.Stopped) return null;
            PollFences();
            int index = slots.Pending;
            if (index < 0 || !gpu.IsComplete(textures[index].Fence)) return null;
            index = slots.Acquire();
            return index < 0 ? null : new(this, index, textures[index].Texture, textures[index].Info!);
        }
    }
    internal void Return(int index)
    {
        lock (gpu.Gate)
        {
            // A new event query covers every consumer command issued before lease return.
            gpu.Context.End(textures[index].Fence); gpu.Context.Flush(); slots.Release(index);
        }
    }
    public object Diagnostics()
    {
        lock (gpu.Gate)
        {
            if (!stopped && (Volatile.Read(ref closed) != 0 || !Source.IsAlive)) { state = CaptureState.Stopped; reason = "source-closed"; }
            else if (!stopped && state != CaptureState.Faulted && Native.IsIconic(Source.Hwnd)) { state = CaptureState.Paused; reason = "minimized"; slots.InvalidatePending(); }
            double age = lastArrival == 0 ? double.PositiveInfinity : Stopwatch.GetElapsedTime(lastArrival).TotalMilliseconds;
            return new { state = state.ToString(), reason, session_generation = SessionGeneration, geometry_generation = geometryGeneration, hwnd = $"0x{Source.Hwnd:X}", pid = Source.ProcessId, title = Source.Title, adapter = gpu.Adapter, width, height, geometry = lastGeometry, arrivals, submitted, dropped_busy = Interlocked.Read(ref droppedBusy), dropped_rate = droppedRate, dropped_slots = droppedSlots, replaced, recreates, pending = slots.Pending >= 0 ? 1 : 0, leased = slots.Leased >= 0 ? 1 : 0, last_frame_age_ms = double.IsFinite(age) ? (double?)Math.Round(age, 3) : null, stale = age > 500, callback_total_ms = callbackTicks * 1000.0 / Stopwatch.Frequency };
        }
    }
    public void Dispose()
    {
        lock (gpu.Gate)
        {
            if (stopped) return;
            if (slots.Leased >= 0) throw new InvalidOperationException("Return the active capture lease before stopping.");
            stopped = true; state = CaptureState.Stopped;
            pool.FrameArrived -= FrameArrived; item.Closed -= Closed;
            session.Dispose(); pool.Dispose();
            foreach (var texture in textures) texture.Dispose(); textures = [];
            winDevice.Dispose();
        }
    }
}
