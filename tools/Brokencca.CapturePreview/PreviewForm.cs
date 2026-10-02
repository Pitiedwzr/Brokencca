using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using Brokencca.Capture.Windows;
using Brokencca.Core;

namespace Brokencca.CapturePreview;

internal sealed class PreviewForm : Form
{
    private readonly Options options;
    private readonly Panel surface = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Label status = new() { Dock = DockStyle.Top, Height = 62, ForeColor = Color.White, BackColor = Color.FromArgb(30, 30, 30), Padding = new(8) };
    private readonly CancellationTokenSource cancellation = new();
    private readonly System.Windows.Forms.Timer duration = new() { Interval = 100 };
    private readonly Stopwatch lifetime = Stopwatch.StartNew();
    private Task? renderer;
    private CapturedFrameInfo? lastFrame;
    private PlayfieldCircle? circle;
    private CaptureProfile? loadedProfile;
    private int renderWidth, renderHeight, guide = 1, sampleRequests, sampleAttempt, pauseMs, retry;
    private volatile bool calibrationPending;
    public int ExitCode { get; private set; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public PreviewForm(Options options)
    {
        this.options = options; Text = "Brokencca Capture Preview"; ClientSize = new(960, 680); KeyPreview = true;
        Controls.Add(surface); Controls.Add(status);
        status.Text = "Starting capture. C: boot calibration | G: guide | arrows/+/-: adjust | S: save | P: pause | R: retry | Esc: close";
        if (options.Profile is not null) loadedProfile = JsonSerializer.Deserialize<CaptureProfile>(File.ReadAllText(options.Profile)) ?? throw new ArgumentException("Empty profile.");
        sampleRequests = options.CalibrateBlack ? 1 : 0;
        surface.SizeChanged += (_, _) => { Volatile.Write(ref renderWidth, surface.ClientSize.Width); Volatile.Write(ref renderHeight, surface.ClientSize.Height); };
        duration.Tick += (_, _) => { if (options.Seconds > 0 && lifetime.Elapsed.TotalSeconds >= options.Seconds) Close(); };
        KeyDown += (_, e) =>
        {
            var info = Volatile.Read(ref lastFrame); var current = Volatile.Read(ref circle);
            switch (e.KeyCode)
            {
                case Keys.C: Interlocked.Exchange(ref sampleRequests, 1); break;
                case Keys.G: Interlocked.Exchange(ref guide, guide == 0 ? 1 : 0); break;
                case Keys.P: Interlocked.Exchange(ref pauseMs, 250); break;
                case Keys.R: Interlocked.Exchange(ref retry, 1); break;
                case Keys.S: Save(); break;
                case Keys.Escape: Close(); break;
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Add: case Keys.Subtract: case Keys.Oemplus: case Keys.OemMinus:
                    if (info is null || current is null || !info.GeometryResolved) break;
                    double dx = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
                    double dy = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
                    double dr = e.KeyCode is Keys.Add or Keys.Oemplus ? 1 : e.KeyCode is Keys.Subtract or Keys.OemMinus ? -1 : 0;
                    var changed = current with { CenterX = current.CenterX + dx / info.Client.Width, CenterY = current.CenterY + dy / info.Client.Height, Radius = current.Radius + dr / Math.Min(info.Client.Width, info.Client.Height), Provenance = "manual" };
                    try { changed.Validate(); Volatile.Write(ref circle, changed); calibrationPending = true; } catch (ArgumentException) { }
                    break;
            }
        };
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e); renderWidth = surface.ClientSize.Width; renderHeight = surface.ClientSize.Height;
        nint hwnd = surface.Handle; renderer = Task.Run(() => Run(hwnd)); duration.Start();
    }
    private void Run(nint previewHwnd)
    {
        var recovery = new CaptureRecovery(); bool injected = false, selectedOnce = false; int successfulCycles = 0;
        WindowIdentity? source = null;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (source is null)
                {
                    if (options.Hwnd is not null) source = WindowLocator.Select(options.Hwnd.Value);
                    else
                    {
                        var matches = WindowLocator.MercuryCandidates();
                        if (matches.Count > 1) throw new ArgumentException("Multiple Mercury windows found. Use --list-windows then --hwnd.");
                        if (matches.Count == 0) { UpdateStatus("Waiting for Mercury  (two trailing spaces). Explicit HWND selection is also available."); cancellation.Token.WaitHandle.WaitOne(300); continue; }
                        if (selectedOnce && Interlocked.Exchange(ref retry, 0) == 0) { UpdateStatus("Mercury restarted. Press R to select the new window."); cancellation.Token.WaitHandle.WaitOne(300); continue; }
                        source = matches[0];
                    }
                }
                selectedOnce = true;
                using var gpu = new GpuDevice();
                using var capture = new WgcCaptureSession(gpu, source, options.Capture);
                using var presenter = new GpuPresenter(gpu, previewHwnd, renderWidth, renderHeight);
                using var wait = new VideoWait();
                Console.WriteLine(JsonSerializer.Serialize(new { capture_run = true, build = typeof(PreviewForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, os = Environment.OSVersion.VersionString, adapter = gpu.Adapter, capture.UpdateIntervalUncapped, source = new { hwnd = $"0x{source.Hwnd:X}", source.ProcessId, source.StartTime, source.Title, source.ClassName }, options.Capture, hdr = "SDR-only: disable HDR for this proof" }));
                var clock = Stopwatch.StartNew(); var consensus = new CircleConsensus();
                long next = 0, report = Stopwatch.GetTimestamp(), priorPresents = 0, priorArrivals = 0;
                int attemptsLeft = 0, smokeChecks = 0;
                bool slowed = false, verifyRecovery = false;
                long lastSample = 0, smokeGeneration = -1, profileGeneration = -1;
                long previousGeometry = -1; double previousAspect = 0;
                long geometryChangedAt = 0, nextIdentityCheck = 0;
                double[] timings = new double[120]; int timingCount = 0, timingIndex = 0;
                using var process = Process.GetCurrentProcess(); TimeSpan priorCpu = process.TotalProcessorTime;
                while (!cancellation.IsCancellationRequested)
                {
                    if (Interlocked.Exchange(ref retry, 0) != 0) break;
                    long now = Stopwatch.GetTimestamp();
                    if (!injected && options.InjectDeviceLossAt > 0 && lifetime.Elapsed.TotalSeconds >= options.InjectDeviceLossAt)
                    { injected = true; throw new COMException("Injected device loss (simulation)", unchecked((int)0x887A0005)); }
                    if (capture.Failure is not null) throw capture.Failure;
                    if (capture.State == CaptureState.Stopped || (now >= nextIdentityCheck && !source.IsAlive)) break;
                    if (now >= nextIdentityCheck) nextIdentityCheck = now + Stopwatch.Frequency * 3 / 10;
                    int pause = Interlocked.Exchange(ref pauseMs, 0);
                    if (pause > 0) cancellation.Token.WaitHandle.WaitOne(pause);
                    if (Interlocked.Exchange(ref sampleRequests, 0) != 0) { attemptsLeft = 6; sampleAttempt = 0; consensus = new(); }
                    if (now >= next)
                    {
                        using var lease = capture.TryAcquire();
                        if (lease is not null)
                        {
                            var info = lease.Info; Volatile.Write(ref lastFrame, info);
                            if (verifyRecovery)
                            {
                                double recoveredAge = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency - info.Timestamp100ns / 10000.0;
                                if (options.Smoke && recoveredAge > 100) throw new InvalidOperationException($"Slow consumer recovered a stale frame ({recoveredAge:F1} ms).");
                                Console.WriteLine(JsonSerializer.Serialize(new { slow_consumer_recovered_age_ms = recoveredAge })); verifyRecovery = false;
                            }
                            if (info.GeometryGeneration != previousGeometry)
                            {
                                double aspect = info.Client.IsPositive ? (double)info.Client.Width / info.Client.Height : 0;
                                if (previousGeometry >= 0 && Math.Abs(aspect - previousAspect) > .005) { Volatile.Write(ref circle, null); calibrationPending = false; }
                                previousGeometry = info.GeometryGeneration; previousAspect = aspect;
                                geometryChangedAt = now;
                                consensus = new();
                            }
                            if (info.GeometryResolved && profileGeneration != info.GeometryGeneration)
                            {
                                profileGeneration = info.GeometryGeneration;
                                if (loadedProfile is not null) { loadedProfile.ValidateFor(info.Client.Width, info.Client.Height); Volatile.Write(ref circle, loadedProfile.Circle); loadedProfile = null; }
                                else if (options.TouccaReference && Volatile.Read(ref circle) is null)
                                {
                                    var g = info.Geometry;
                                    Volatile.Write(ref circle, PlayfieldCircle.TouccaReference(info.Client.Width, info.Client.Height, g.OuterScreen.Right - g.ClientScreen.X, g.OuterScreen.Bottom - g.ClientScreen.Y)); calibrationPending = true;
                                }
                            }
                            if (options.Smoke && !options.CalibrateBlack && info.GeometryResolved && smokeGeneration != info.GeometryGeneration && now - geometryChangedAt > Stopwatch.Frequency / 10)
                            {
                                VerifyMarkers(gpu, lease); smokeGeneration = info.GeometryGeneration; smokeChecks++;
                                Console.WriteLine(JsonSerializer.Serialize(new { smoke_marker_check = "passed", info.Id, info.GeometryGeneration, info.Client }));
                            }
                            if (attemptsLeft > 0 && info.GeometryResolved && now - lastSample > Stopwatch.Frequency / 3)
                            {
                                var detection = Detect(gpu, lease);
                                var agreed = consensus.Observe(detection?.Circle);
                                Console.WriteLine(JsonSerializer.Serialize(new { calibration_sample = ++sampleAttempt, detection, agreed = agreed is not null }));
                                lastSample = now; attemptsLeft--;
                                if (agreed is not null) { Volatile.Write(ref circle, agreed); calibrationPending = true; attemptsLeft = 0; smokeChecks++; }
                                if (attemptsLeft == 0 && agreed is null) UpdateStatus("No stable circular silhouette detected. Sample again when the circular area is lit against boot black, or use --toucca-reference.");
                            }
                            int w = Volatile.Read(ref renderWidth), h = Volatile.Read(ref renderHeight);
                            if (w > 0 && h > 0)
                            {
                                presenter.Resize(w, h);
                                bool presented = presenter.Present(lease, Volatile.Read(ref guide) != 0 ? Volatile.Read(ref circle) : null);
                                if (presented && info.Timestamp100ns > 0)
                                {
                                    timings[timingIndex++ % timings.Length] = Math.Max(0, Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency - info.Timestamp100ns / 10000.0);
                                    timingCount = Math.Min(timingCount + 1, timings.Length);
                                }
                            }
                            long interval = Stopwatch.Frequency / options.Capture.FramesPerSecond;
                            next = next == 0 || now - next > interval ? now + interval : next + interval;
                            if (!slowed && options.SlowConsumerMs > 0 && clock.Elapsed.TotalSeconds > 2)
                            { slowed = true; cancellation.Token.WaitHandle.WaitOne(options.SlowConsumerMs); verifyRecovery = true; }
                        }
                    }
                    if (now - report >= Stopwatch.Frequency)
                    {
                        process.Refresh(); var cpu = process.TotalProcessorTime;
                        double elapsed = (now - report) / (double)Stopwatch.Frequency;
                        double[] ordered = timings.Take(timingCount).Order().ToArray();
                        var diagnostics = JsonSerializer.SerializeToElement(capture.Diagnostics());
                        long arrivals = diagnostics.GetProperty("arrivals").GetInt64();
                        var payload = new { capture = diagnostics, presented = presenter.Presents, presents_per_second = (presenter.Presents - priorPresents) / elapsed, arrivals_per_second = (arrivals - priorArrivals) / elapsed, presenter.PresentBusy, qpc_to_present_submit_p95_ms = ordered.Length == 0 ? (double?)null : ordered[(int)((ordered.Length - 1) * .95)], cpu_percent = (cpu - priorCpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100, working_set_bytes = process.WorkingSet64, calibration_pending_confirmation = calibrationPending, recovery_attempts = recovery.Attempts };
                        if (options.Diagnostics) Console.WriteLine(JsonSerializer.Serialize(payload));
                        string reason = diagnostics.GetProperty("reason").GetString() ?? "capture";
                        bool stale = diagnostics.GetProperty("stale").GetBoolean();
                        UpdateStatus($"{reason}{(stale ? " | STALE >500 ms" : "")} | {presenter.Presents - priorPresents} presents | {(calibrationPending ? "Circle suggestion: check guide, adjust arrows/+/-; S confirms and saves" : "C: boot calibration | G: guide | S: save | P: pause | R: retry")} ");
                        report = now; priorPresents = presenter.Presents; priorArrivals = arrivals; priorCpu = cpu;
                    }
                    if (options.CaptureCycles > 1 && clock.Elapsed.TotalSeconds >= 1.25 && smokeChecks > 0 && presenter.Presents >= 20) break;
                    wait.Milliseconds(1);
                }
                if (options.Smoke && (smokeChecks == 0 || presenter.Presents < 20)) throw new InvalidOperationException("Smoke test failed: no verified fixture/calibration or fewer than 20 presentations.");
                if (options.Smoke) Console.WriteLine(JsonSerializer.Serialize(new { capture_smoke = "passed", smokeChecks, presenter.Presents, simulated_device_loss = injected }));
                if (cancellation.IsCancellationRequested) return;
                if (options.CaptureCycles > 1 && ++successfulCycles < options.CaptureCycles)
                {
                    Volatile.Write(ref lastFrame, null);
                    Console.WriteLine(JsonSerializer.Serialize(new { capture_cycle = successfulCycles }));
                    continue;
                }
                if (options.CaptureCycles > 1) { Console.WriteLine(JsonSerializer.Serialize(new { capture_cycles_passed = successfulCycles })); RequestClose(); return; }
                source = null; Volatile.Write(ref lastFrame, null); Volatile.Write(ref circle, null);
                if (options.Hwnd is not null) { UpdateStatus("Selected window closed or stopped. Press R to retry its HWND; use a new process for a new HWND."); WaitForRetry(); }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { capture_error = e.ToString(), hresult = $"0x{e.HResult:X8}", simulated_device_loss = injected }));
                TimeSpan? backoff = recovery.NextDelay(e.HResult);
                if (backoff is not null && source?.IsAlive == true)
                {
                    if (options.CalibrateBlack) Interlocked.Exchange(ref sampleRequests, 1);
                    UpdateStatus($"Rebuilding capture GPU after device loss (attempt {recovery.Attempts}/3).");
                    cancellation.Token.WaitHandle.WaitOne(backoff.Value); continue;
                }
                ExitCode = 1; UpdateStatus($"Capture fault: {e.Message}. Press R to retry.");
                if (options.Seconds > 0 || options.Smoke) { RequestClose(); return; }
                WaitForRetry(); recovery = new(); source = null; ExitCode = 0;
            }
        }
    }
    private void WaitForRetry() { while (!cancellation.IsCancellationRequested && Interlocked.Exchange(ref retry, 0) == 0) cancellation.Token.WaitHandle.WaitOne(100); }
    private static CircleDetection? Detect(GpuDevice gpu, CapturedFrameLease lease)
    {
        var info = lease.Info; byte[] pixels = gpu.Readback(lease.Texture, info.Width, info.Height);
        byte[] client = new byte[info.Client.Width * info.Client.Height * 4];
        for (int y = 0; y < info.Client.Height; y++) Buffer.BlockCopy(pixels, ((info.Client.Y + y) * info.Width + info.Client.X) * 4, client, y * info.Client.Width * 4, info.Client.Width * 4);
        return BlackBackgroundCalibration.Detect(client, info.Client.Width, info.Client.Height, info.Client.Width * 4);
    }
    private static void VerifyMarkers(GpuDevice gpu, CapturedFrameLease lease)
    {
        var info = lease.Info;
        if (!info.GeometryResolved) throw new InvalidOperationException("Fixture client geometry unresolved.");
        byte[] pixels = gpu.Readback(lease.Texture, info.Width, info.Height);
        var r = info.Client;
        var markers = new[] { (r.X + 4, r.Y + 4, 0, 0, 255), (r.Right - 5, r.Y + 4, 0, 255, 0), (r.X + 4, r.Bottom - 5, 255, 0, 0), (r.Right - 5, r.Bottom - 5, 0, 255, 255) };
        foreach (var (x, y, b, g, red) in markers)
        {
            int p = (y * info.Width + x) * 4;
            if (Math.Abs(pixels[p] - b) > 8 || Math.Abs(pixels[p + 1] - g) > 8 || Math.Abs(pixels[p + 2] - red) > 8) throw new InvalidOperationException($"Fixture crop marker mismatch at ({x},{y}): BGRA={pixels[p]},{pixels[p + 1]},{pixels[p + 2]}. Client/titlebar origin needs investigation.");
        }
        // These checks detect a one-pixel origin/border error that large corner patches hide.
        var edges = new[] { (r.X + r.Width / 2, r.Y), (r.X + r.Width / 2, r.Bottom - 1), (r.X, r.Y + r.Height / 2), (r.Right - 1, r.Y + r.Height / 2) };
        foreach (var (x, y) in edges)
        {
            int p = (y * info.Width + x) * 4;
            if (pixels[p] < 240 || pixels[p + 1] < 240 || pixels[p + 2] < 240) throw new InvalidOperationException("Fixture one-pixel client edge mismatch.");
        }
    }
    private void Save()
    {
        var info = Volatile.Read(ref lastFrame); var current = Volatile.Read(ref circle);
        if (options.Capture.FullItem) { status.Text = "Switch to a client crop before saving a playfield profile."; return; }
        if (info is null || current is null || !info.GeometryResolved) { status.Text = "No calibrated client circle to save. Press C or start with --toucca-reference."; return; }
        var (x, y, radius) = current.ToPixels(info.Client.Width, info.Client.Height);
        var c = info.Crop;
        if (!c.Contains(info.Client.X + x - radius, info.Client.Y + y) || !c.Contains(info.Client.X + x + radius, info.Client.Y + y) || !c.Contains(info.Client.X + x, info.Client.Y + y - radius) || !c.Contains(info.Client.X + x, info.Client.Y + y + radius)) { status.Text = "Crop clips the playfield circle. Adjust the crop/profile before saving."; return; }
        string? path = options.SaveProfile;
        if (path is null)
        {
            using var dialog = new SaveFileDialog { Filter = "Calibration profile|*.json", FileName = "capture-profile.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return; path = dialog.FileName;
        }
        try
        {
            var profile = new CaptureProfile(1, info.Client.Width, info.Client.Height, info.Geometry.Dpi, options.Capture.Crop, current, true);
            profile.ValidateFor(info.Client.Width, info.Client.Height);
            File.WriteAllText(path, JsonSerializer.Serialize(profile, Json)); calibrationPending = false;
            status.Text = $"Confirmed profile saved: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { status.Text = $"Could not save profile: {ex.Message}"; }
    }
    private void UpdateStatus(string text)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed) status.Text = text; }); } catch (InvalidOperationException) { }
    }
    private void RequestClose() { if (!IsDisposed) { try { BeginInvoke(Close); } catch (InvalidOperationException) { } } }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        duration.Stop(); cancellation.Cancel();
        if (renderer is not null && !renderer.Wait(TimeSpan.FromSeconds(5))) { e.Cancel = true; status.Text = "GPU teardown still running; retry close when it finishes."; }
        base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing) { if (disposing) { duration.Dispose(); cancellation.Dispose(); } base.Dispose(disposing); }
}
