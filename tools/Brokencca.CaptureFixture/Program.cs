using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Brokencca.Capture.Windows;

namespace Brokencca.CaptureFixture;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string title = "Brokencca Capture Fixture"; int width = 1280, height = 720, fps = 60; double seconds = 0;
            bool boot = false, borderless = false, lifecycle = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--title": title = args[++i]; break;
                    case "--size": var size = args[++i].Split('x'); width = int.Parse(size[0], CultureInfo.InvariantCulture); height = int.Parse(size[1], CultureInfo.InvariantCulture); break;
                    case "--fps": fps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--seconds": seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--boot-black": boot = true; break;
                    case "--borderless": borderless = true; break;
                    case "--exercise-lifecycle": lifecycle = true; break;
                    case "--help": Console.WriteLine("CaptureFixture [--title TEXT] [--size 1280x720] [--fps 60] [--seconds N] [--boot-black] [--borderless] [--exercise-lifecycle]\nB: boot black; F: borderless; R: resize; M: minimize (auto-restore); T: change title; Esc: close."); return 0;
                    default: throw new ArgumentException($"Unknown option {args[i]}");
                }
            }
            if (width < 100 || height < 100 || width > 8192 || height > 8192 || fps is < 1 or > 60 || !double.IsFinite(seconds) || seconds < 0) throw new ArgumentException("Invalid fixture size/rate/duration.");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            using var form = new FixtureForm(title, width, height, fps, seconds, boot, borderless, lifecycle);
            Application.Run(form); return form.Failed ? 1 : 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}

internal sealed class FixtureForm : Form
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly System.Windows.Forms.Timer events = new() { Interval = 100 };
    private readonly int fps;
    private readonly double seconds;
    private readonly bool lifecycle;
    private readonly string baseTitle;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Task? renderer;
    private int renderWidth, renderHeight, boot, paused, cycles;
    private double restoreAt;
    public bool Failed { get; private set; }
    public FixtureForm(string title, int width, int height, int fps, double seconds, bool boot, bool borderless, bool lifecycle)
    {
        baseTitle = title; Text = title; this.fps = fps; this.seconds = seconds; this.lifecycle = lifecycle;
        this.boot = boot ? 1 : 0; ClientSize = new(width, height); KeyPreview = true;
        if (borderless) FormBorderStyle = FormBorderStyle.None;
        SizeChanged += (_, _) => { Volatile.Write(ref renderWidth, ClientSize.Width); Volatile.Write(ref renderHeight, ClientSize.Height); Volatile.Write(ref paused, WindowState == FormWindowState.Minimized ? 1 : 0); };
        KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.B: Interlocked.Exchange(ref this.boot, this.boot == 0 ? 1 : 0); break;
                case Keys.F: FormBorderStyle = FormBorderStyle == FormBorderStyle.None ? FormBorderStyle.Sizable : FormBorderStyle.None; break;
                case Keys.R: ClientSize = ClientSize.Width == 1280 ? new(960, 600) : new(1280, 720); break;
                case Keys.M: WindowState = FormWindowState.Minimized; restoreAt = clock.Elapsed.TotalSeconds + 1; break;
                case Keys.T: Text = Text == baseTitle ? "Fixture changed title" : baseTitle; break;
                case Keys.Escape: Close(); break;
            }
        };
        events.Tick += (_, _) =>
        {
            double elapsed = clock.Elapsed.TotalSeconds;
            if (restoreAt > 0 && elapsed >= restoreAt) { WindowState = FormWindowState.Normal; restoreAt = 0; }
            if (lifecycle && cycles < 20 && elapsed > 2 + cycles * .6)
            {
                ClientSize = cycles % 2 == 0 ? new(960, 600) : new(1280, 720);
                if (cycles % 4 == 0) { WindowState = FormWindowState.Minimized; restoreAt = elapsed + .2; }
                cycles++;
                Console.WriteLine(JsonSerializer.Serialize(new { fixture_lifecycle_cycle = cycles, elapsed_s = elapsed }));
            }
            if (seconds > 0 && elapsed >= seconds) Close();
        };
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e); renderWidth = ClientSize.Width; renderHeight = ClientSize.Height;
        nint hwnd = Handle;
        Console.WriteLine(JsonSerializer.Serialize(new { fixture_hwnd = $"0x{hwnd:X}", pid = Environment.ProcessId, title = Text, width = renderWidth, height = renderHeight, boot_black = boot != 0 }));
        renderer = Task.Run(() =>
        {
            try
            {
                using var gpu = new GpuDevice(); using var presenter = new GpuPresenter(gpu, hwnd, renderWidth, renderHeight);
                using var wait = new VideoWait();
                long frame = 0, next = 0, report = 0, priorPresents = 0;
                while (!cancellation.IsCancellationRequested)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (Volatile.Read(ref paused) != 0) { cancellation.Token.WaitHandle.WaitOne(20); continue; }
                    if (now < next) { wait.Milliseconds((next - now) * 1000.0 / Stopwatch.Frequency); continue; }
                    long interval = Stopwatch.Frequency / fps;
                    next = next == 0 || now - next > interval ? now + interval : next + interval;
                    int w = Volatile.Read(ref renderWidth), h = Volatile.Read(ref renderHeight);
                    if (w <= 0 || h <= 0) continue;
                    presenter.Resize(w, h); presenter.PresentFixture(frame++, Volatile.Read(ref boot) != 0);
                    if (now - report > Stopwatch.Frequency)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { fixture_presents = presenter.Presents, interval_presents = presenter.Presents - priorPresents, qpc = now, width = w, height = h, circle = new { center_x = .54, center_y = .47, radius = .39 }, gpu.Adapter }));
                        report = now; priorPresents = presenter.Presents;
                    }
                }
            }
            catch (Exception ex) { Failed = true; Console.Error.WriteLine(ex); if (!IsDisposed) BeginInvoke(Close); }
        });
        events.Start();
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        events.Stop(); cancellation.Cancel();
        if (renderer is not null && !renderer.Wait(TimeSpan.FromSeconds(5))) { e.Cancel = true; Console.Error.WriteLine("Fixture GPU teardown still running; retry close."); }
        base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing) { if (disposing) { events.Dispose(); cancellation.Dispose(); } base.Dispose(disposing); }
}
