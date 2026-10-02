using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Brokencca.Core;
using Brokencca.Host;
using Brokencca.Capture.Windows;
using Brokencca.Video.Windows;
using System.Globalization;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Contains("--help"))
    {
        Console.WriteLine("""
            Brokencca wired input host
              --dry-run              Print changed zones without opening COM ports (default)
              --quiet                Suppress per-state dry-run output
              --diagnostics          Emit one machine-readable timing report per second
              --serial               Enable WACCA serial output; start before the game
              --hook                 Enable the Brokencca MercuryIO DLL backend instead
              --hook-rate 240        Maximum hook callbacks/s (60..1000); game sampling still applies
              --leds                 Forward hook LEDs on a separate USB connection (new IPA required)
              --led-port 24866       Loopback port forwarded to iOS LED listener 24866
              --video               Stream hardware H.264 to the updated iOS app (control v2)
              --video-port 24865     Distinct loopback video port forwarded to iOS 24865
              --hwnd 0x1234          Select the source window, or use --mercury
              --mercury             Select the unique current Mercury window
              --profile <json>      Confirmed capture calibration profile (required for video)
              --video-long-edge 1280 Maximum video dimension (960..1920)
              --video-bitrate 10000000 Target H.264 bitrate (6000000..20000000)
              --video-diagnostics   Emit video stage/frame diagnostics
              --left COM5            Host port paired with game COM3
              --right COM6           Host port paired with game COM4
              --port 24864           Loopback port forwarded to iOS by iproxy
              --iproxy <exe>         Launch an installed libusbmuxd iproxy automatically
              --udid <id>            Select a USB device (requires --iproxy)
              --device-model <text>  Record the tested iOS device model in diagnostics
              --gpu <text>           Record the game PC GPU in diagnostics
            Without --iproxy, start: iproxy -l 24864:24864
            Video also needs: iproxy -l 24864:24864 24865:24865
            Ctrl+C releases touches and exits. Video requires brokencca-video.dll beside the host.
            """);
        return 0;
    }
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    SerialTouchSink? serial = null;
    HookTouchSink? hook = null;
    Process? proxy = null;
    Task? serialTask = null;
    Task? connectionTask = null;
    Task? proxyTask = null;
    Task? diagnosticsTask = null;
    Task? ledTask = null;
    Task? hookTask = null;
    try
    {
        var options = Parse(args);
        if (!int.TryParse(options.GetValueOrDefault("--port", "24864"), out int port) || port is < 1 or > 65535)
            throw new ArgumentException("--port must be between 1 and 65535.");
        bool useSerial = options.ContainsKey("--serial");
        bool useHook = options.ContainsKey("--hook");
        bool useLeds = options.ContainsKey("--leds");
        bool useVideo = options.ContainsKey("--video");
        VideoOptions? video = null;
        if (useVideo)
        {
            if (!options.TryGetValue("--profile", out string? path)) throw new ArgumentException("--video requires a confirmed --profile JSON.");
            if (options.ContainsKey("--mercury") == options.ContainsKey("--hwnd")) throw new ArgumentException("Video requires exactly one of --mercury or --hwnd.");
            var profile = JsonSerializer.Deserialize<CaptureProfile>(File.ReadAllText(path)) ?? throw new ArgumentException("Empty capture profile.");
            profile.ValidateFor(profile.SourceWidth, profile.SourceHeight);
            WindowIdentity source;
            if (options.ContainsKey("--mercury"))
            {
                var candidates = WindowLocator.MercuryCandidates();
                if (candidates.Count != 1) throw new ArgumentException("Select a unique live Mercury window or provide --hwnd.");
                source = candidates[0];
            }
            else source = WindowLocator.Select((nint)long.Parse(options["--hwnd"].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            if (!int.TryParse(options.GetValueOrDefault("--video-port", "24865"), out int videoPort) || videoPort is < 1 or > 65535 || videoPort == port || (useLeds && videoPort == int.Parse(options.GetValueOrDefault("--led-port", "24866"))))
                throw new ArgumentException("Video port must be a distinct valid port.");
            if (!int.TryParse(options.GetValueOrDefault("--video-long-edge", "1280"), out int edge) || edge is < 960 or > 1920)
                throw new ArgumentException("Video long edge must be 960..1920.");
            if (!int.TryParse(options.GetValueOrDefault("--video-bitrate", "10000000"), out int bitrate) || bitrate is < 6000000 or > 20000000)
                throw new ArgumentException("Video bitrate must be 6000000..20000000.");
            video = new(source, profile, videoPort, edge, bitrate, options.ContainsKey("--video-diagnostics"));
        }
        if ((useSerial ? 1 : 0) + (useHook ? 1 : 0) + (options.ContainsKey("--dry-run") ? 1 : 0) > 1)
            throw new ArgumentException("Choose only one of --serial, --hook, or --dry-run.");
        if (useLeds && !useHook) throw new ArgumentException("--leds requires --hook.");
        if (!int.TryParse(options.GetValueOrDefault("--led-port", "24866"), out int ledPort) || ledPort is < 1 or > 65535 || (useLeds && ledPort == port))
            throw new ArgumentException("--led-port must be a distinct valid port.");
        if (!int.TryParse(options.GetValueOrDefault("--hook-rate", "240"), out int hookRate) || hookRate is < 60 or > 1000)
            throw new ArgumentException("--hook-rate must be between 60 and 1000.");
        bool diagnosticMode = options.ContainsKey("--diagnostics") || useVideo;
        bool quiet = options.ContainsKey("--quiet");
        if (options.ContainsKey("--udid") && !options.ContainsKey("--iproxy"))
            throw new ArgumentException("--udid requires --iproxy.");
        if (useSerial)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Serial host requires Windows.");
            string left = options.GetValueOrDefault("--left", "COM5"), right = options.GetValueOrDefault("--right", "COM6");
            if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Serial ports must differ.");
            serial = new(left, right, diagnosticMode ? new HostDiagnostics() : null);
        }
        HostDiagnostics? diagnostics = serial?.Diagnostics ?? (diagnosticMode ? new HostDiagnostics() : null);
        if (video is not null && diagnostics is not null) video = video with { InputOverloaded = () => diagnostics.InputOverloaded };
        ITouchSink sink = serial is null ? new ConsoleTouchSink(quiet) : serial;
        if (useHook)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Hook backend requires Windows.");
            hook = new(diagnostics, hookRate); sink = hook;
        }
        if (options.TryGetValue("--iproxy", out string? executable))
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-l");
            if (options.TryGetValue("--udid", out string? udid))
            { start.ArgumentList.Add("-u"); start.ArgumentList.Add(udid); }
            start.ArgumentList.Add($"{port}:24864");
            if (useLeds) start.ArgumentList.Add($"{ledPort}:24866");
            if (video is not null) start.ArgumentList.Add($"{video.Port}:24865");
            proxy = Process.Start(start) ?? throw new IOException("Could not start iproxy.");
            proxyTask = proxy.WaitForExitAsync(stop.Token);
        }
        Console.WriteLine(useSerial ? "Serial output enabled." : useHook ? "MercuryIO hook output enabled; no COM ports opened." : "Dry run: no serial ports opened.");
        if (diagnostics is not null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "run_info",
                revision = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                sink = useSerial ? "serial" : useHook ? "hook" : quiet ? "dry-run-quiet" : "dry-run-console",
                hook_api = useHook ? "1.0" : null,
                hook_rate = useHook ? hookRate : 0,
                leds = useLeds,
                os = RuntimeInformation.OSDescription,
                host_name = Environment.MachineName,
                device_model = options.GetValueOrDefault("--device-model", "unknown"),
                gpu = options.GetValueOrDefault("--gpu", "unknown"),
                iproxy = options.TryGetValue("--iproxy", out string? iproxyPath)
                    ? FileVersionInfo.GetVersionInfo(Path.GetFullPath(iproxyPath)).FileVersion ?? "unknown"
                    : "external/unknown",
                com = useSerial ? new { left = options.GetValueOrDefault("--left", "COM5"), right = options.GetValueOrDefault("--right", "COM6"), baud = 115200 } : null
            }));
            diagnosticsTask = ReportDiagnosticsAsync(diagnostics, stop.Token);
        }
        Console.WriteLine($"Connecting to iOS through 127.0.0.1:{port}. Ctrl+C to stop.");
        serialTask = serial?.RunAsync(stop.Token);
        if (hook is not null && OperatingSystem.IsWindows())
        {
            hookTask = hook.RunAsync(stop.Token);
            if (useLeds) ledTask = LedForwarder.RunAsync(hook, ledPort, stop.Token);
        }
        connectionTask = ConnectLoopAsync(port, sink, diagnostics, video, stop.Token);
        var tasks = new List<Task> { connectionTask };
        if (serialTask is not null) tasks.Add(serialTask);
        if (hookTask is not null) tasks.Add(hookTask);
        if (ledTask is not null) tasks.Add(ledTask);
        if (proxyTask is not null) tasks.Add(proxyTask);
        Task completed = await Task.WhenAny(tasks);
        await completed;
        if (!stop.IsCancellationRequested) throw new IOException("A required worker or iproxy stopped unexpectedly.");
        return 0;
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    finally
    {
        stop.Cancel();
        foreach (Task? task in new[] { connectionTask, serialTask, hookTask, ledTask, proxyTask, diagnosticsTask })
            if (task is not null) try { await task; } catch (Exception) { /* Error reported by supervisor. */ }
        serial?.Dispose();
        if (hook is not null && OperatingSystem.IsWindows()) hook.Dispose();
        if (proxy is not null)
        {
            if (!proxy.HasExited) proxy.Kill(entireProcessTree: true);
            proxy.Dispose();
        }
    }
}

static Dictionary<string, string> Parse(string[] args)
{
    var options = new Dictionary<string, string>();
    for (int i = 0; i < args.Length; i++)
    {
        string name = args[i];
        if (name is "--serial" or "--hook" or "--leds" or "--dry-run" or "--quiet" or "--diagnostics" or "--video" or "--mercury" or "--video-diagnostics") { options.Add(name, "true"); continue; }
        if (name is not ("--left" or "--right" or "--port" or "--iproxy" or "--udid" or "--device-model" or "--gpu" or "--hook-rate" or "--led-port" or "--video-port" or "--hwnd" or "--profile" or "--video-long-edge" or "--video-bitrate"))
            throw new ArgumentException($"Unknown option {name}. Use --help.");
        if (++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException($"Missing value for {name}.");
        options.Add(name, args[i]);
    }
    return options;
}

static async Task ConnectLoopAsync(int port, ITouchSink sink, HostDiagnostics? diagnostics, VideoOptions? video, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        using var videoStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? videoTask = null;
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            Console.WriteLine("Connected; negotiating Brokencca protocol.");
            await new InputSession(sink, observer: diagnostics, enableVideo: video is not null,
                videoSessionReady: session => { if (video is not null) videoTask = Task.Run(() => VideoStreamer.RunAsync(video, session, videoStop.Token)); },
                videoSessionEnded: () => videoStop.Cancel()).RunAsync(client.GetStream(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "disconnect",
                reason = DisconnectReason(ex),
                exception = ex.GetType().Name,
                message = ex.Message,
                retry_ms = 1000
            }));
        }
        finally
        {
            sink.Reset(); videoStop.Cancel();
            if (videoTask is not null) try { await videoTask; } catch (OperationCanceledException) { }
        }
        await Task.Delay(1000, token);
    }
}

static string DisconnectReason(Exception ex) => ex switch
{
    OperationCanceledException => "timeout",
    EndOfStreamException => "peer_closed",
    SocketException => "socket_error",
    InvalidDataException => "protocol_error",
    IOException when ex.Message.Contains("overflow", StringComparison.OrdinalIgnoreCase) => "overflow",
    IOException => "io_error",
    _ => "unexpected"
};

static async Task ReportDiagnosticsAsync(HostDiagnostics diagnostics, CancellationToken token)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await timer.WaitForNextTickAsync(token)) Console.WriteLine(diagnostics.TakeReport());
}

sealed class ConsoleTouchSink(bool quiet) : ITouchSink
{
    private TouchState current = TouchState.Empty;
    public void Apply(TouchState state)
    {
        if (current.SameAs(state)) return;
        current = state;
        if (!quiet) Console.WriteLine($"Zones: [{string.Join(", ", Enumerable.Range(0, 240).Where(i => state[i]))}]");
    }
    public void Reset() => Apply(TouchState.Empty);
}
