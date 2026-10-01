using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using Brokencca.Core;

namespace Brokencca.Host;

[SupportedOSPlatform("windows")]
public sealed class HookTouchSink : ITimedTouchSink, IDisposable
{
    internal const string Prefix = @"Local\BROKENCCA_MERCURY_V1";
    internal const uint Magic = 0x50494342, LedMagic = 0x444c4342;
    internal const int HeaderSize = 128, EntrySize = 56, Capacity = 64, InputSize = 3712, LedSize = 1952;
    private readonly MemoryMappedFile mapping, ledMapping;
    private readonly MemoryMappedViewAccessor view, ledView;
    private readonly Mutex mutex, ledMutex;
    private readonly EventWaitHandle work;
    private readonly Semaphore owner;
    private readonly HostDiagnostics? diagnostics;
    private TouchState accepted = TouchState.Empty;
    private uint generation;
    private bool disposed;
    private ulong lastCompletion;

    public HookTouchSink(HostDiagnostics? diagnostics = null, int rate = 240)
        : this(Environment.GetEnvironmentVariable("BROKENCCA_IPC_PREFIX") ?? Prefix, diagnostics, rate) { }
    internal HookTouchSink(string prefix, HostDiagnostics? diagnostics = null, int rate = 240)
    {
        if (rate is < 60 or > 1000) throw new ArgumentOutOfRangeException(nameof(rate));
        if (prefix.Length >= 128 || !prefix.StartsWith(@"Local\", StringComparison.Ordinal))
            throw new ArgumentException("Hook IPC prefix must be a Local namespace name shorter than 128 characters.", nameof(prefix));
        this.diagnostics = diagnostics;
        owner = new(1, 1, prefix + ".ProducerOwner");
        if (!owner.WaitOne(0)) { owner.Dispose(); throw new IOException("Another Brokencca hook host already owns this IPC namespace."); }
        try
        {
            mutex = new(false, prefix + ".InputMutex");
            ledMutex = new(false, prefix + ".LedMutex");
            work = new(false, EventResetMode.AutoReset, prefix + ".Work");
            mapping = MemoryMappedFile.CreateOrOpen(prefix + ".Input", InputSize);
            ledMapping = MemoryMappedFile.CreateOrOpen(prefix + ".Leds", LedSize);
            view = mapping.CreateViewAccessor(0, InputSize);
            ledView = ledMapping.CreateViewAccessor(0, LedSize);
            Locked(() =>
            {
                uint magic = view.ReadUInt32(0);
                if (magic != 0 && (magic != Magic || view.ReadUInt32(4) != 1)) throw new IOException("Incompatible MercuryIO input IPC layout.");
                view.Write(0, Magic); view.Write(4, 1u);
                view.Write(24, (uint)Environment.ProcessId);
                view.Write(28, (uint)Math.Ceiling(1_000_000.0 / rate));
                view.Write(32, Tick);
                ResetLocked();
            });
            WithMutex(ledMutex, () =>
            {
                uint magic = ledView.ReadUInt32(0);
                if (magic != 0 && (magic != LedMagic || ledView.ReadUInt32(4) != 1)) throw new IOException("Incompatible MercuryIO LED IPC layout.");
                ledView.Write(0, LedMagic); ledView.Write(4, 1u);
            });
            work.Set();
        }
        catch
        {
            view?.Dispose(); ledView?.Dispose(); mapping?.Dispose(); ledMapping?.Dispose();
            mutex?.Dispose(); ledMutex?.Dispose(); work?.Dispose(); owner.Release(); owner.Dispose(); throw;
        }
    }
    private static ulong Tick => unchecked((ulong)Environment.TickCount64);
    private static void WithMutex(Mutex value, Action action)
    {
        bool held;
        try { held = value.WaitOne(100); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) throw new IOException("MercuryIO IPC mutex stalled.");
        try { action(); } finally { value.ReleaseMutex(); }
    }
    private void Locked(Action action) => WithMutex(mutex, action);
    public void Apply(TouchState state) => Apply(state, Stopwatch.GetTimestamp());
    public void Apply(TouchState state, long arrivalTimestamp)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            Locked(() =>
            {
                view.Write(32, Tick);
                if (generation != view.ReadUInt32(8)) { generation = view.ReadUInt32(8); accepted = TouchState.Empty; }
                if (view.ReadUInt32(20) != 0 && Tick - view.ReadUInt64(40) > 500)
                { ResetLocked(); throw new IOException("MercuryIO callback stalled; releasing all touches."); }
                if (accepted.SameAs(state)) return;
                uint read = view.ReadUInt32(12), write = view.ReadUInt32(16);
                if (unchecked(write - read) >= Capacity)
                {
                    diagnostics?.QueueOverflow(); ResetLocked();
                    throw new IOException("Hook input queue overflow; releasing all touches.");
                }
                Enqueue(state, arrivalTimestamp, write);
                accepted = state;
                uint depth = unchecked(write - read) + 1;
                long oldest = view.ReadInt64(HeaderSize + (read % Capacity) * EntrySize + 16);
                diagnostics?.QueueEnqueued((int)depth, Stopwatch.GetElapsedTime(oldest).TotalMilliseconds);
            });
        }
        finally { work.Set(); }
    }
    private void Enqueue(TouchState state, long received, uint write)
    {
        long offset = HeaderSize + (write % Capacity) * EntrySize;
        view.Write(offset, generation); view.Write(offset + 8, received);
        view.Write(offset + 16, Stopwatch.GetTimestamp());
        byte[] bits = state.ToArray(); view.WriteArray(offset + 24, bits, 0, bits.Length);
        view.Write(16, unchecked(write + 1));
    }
    private void ResetLocked()
    {
        generation = unchecked(view.ReadUInt32(8) + 1); view.Write(8, generation);
        view.Write(12, 0u); view.Write(16, 0u); accepted = TouchState.Empty;
        Enqueue(TouchState.Empty, 0, 0); diagnostics?.QueueReset();
    }
    public void Reset() { if (disposed) return; Locked(ResetLocked); work.Set(); }
    public Task RunAsync(CancellationToken token) => Task.Factory.StartNew(() =>
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                Locked(() =>
                {
                    view.Write(32, Tick);
                    ulong count = view.ReadUInt64(64);
                    if (count != lastCompletion)
                    {
                        lastCompletion = count;
                        long received = view.ReadInt64(48), completed = view.ReadInt64(56);
                        if (received != 0) diagnostics?.HookCompleted(Stopwatch.GetElapsedTime(received, completed).TotalMilliseconds);
                    }
                    uint depth = unchecked(view.ReadUInt32(16) - view.ReadUInt32(12));
                    diagnostics?.HookStatus(depth, view.ReadUInt32(20) != 0 && Tick - view.ReadUInt64(40) <= 500, view.ReadUInt32(72));
                });
                token.WaitHandle.WaitOne(50);
            }
        }
        finally { Reset(); Locked(() => view.Write(24, 0u)); work.Set(); }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public byte[] ReadLeds()
    {
        byte[] payload = new byte[LedProtocol.PayloadSize];
        // A contested/stale LED buffer becomes black, never a reason to block input.
        bool held;
        try { held = ledMutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
        if (!held) return payload;
        try
        {
            ulong age = Tick - ledView.ReadUInt64(16);
            diagnostics?.LedCapture(ledView.ReadUInt32(12), age);
            if (ledView.ReadUInt32(0) != LedMagic || age > 1000) return payload;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, ledView.ReadUInt32(8));
            ledView.ReadArray(32, payload, 4, 1920);
            return payload;
        }
        finally { ledMutex.ReleaseMutex(); }
    }
    internal void RecordLedSent() => diagnostics?.LedSent();
    public void Dispose()
    {
        if (disposed) return;
        Reset(); Locked(() => view.Write(24, 0u)); disposed = true;
        view.Dispose(); ledView.Dispose(); mapping.Dispose(); ledMapping.Dispose(); mutex.Dispose(); ledMutex.Dispose(); work.Dispose();
        owner.Release(); owner.Dispose();
    }
}
