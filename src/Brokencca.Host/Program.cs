using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Brokencca.Core;
using Brokencca.Host;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Contains("--help"))
    {
        Console.WriteLine("""
            Brokencca wired input host
              --dry-run              Print changed zones without opening COM ports (default)
              --serial               Enable WACCA serial output; start before the game
              --left COM5            Host port paired with game COM3
              --right COM6           Host port paired with game COM4
              --port 24864           Loopback port forwarded to iOS by iproxy
              --iproxy <exe>         Launch an installed libusbmuxd iproxy automatically
              --udid <id>            Select a USB device (requires --iproxy)
            Without --iproxy, start: iproxy -l 24864:24864
            Ctrl+C releases touches and exits. Video is not implemented yet.
            """);
        return 0;
    }
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    SerialTouchSink? serial = null;
    Process? proxy = null;
    Task? serialTask = null;
    Task? connectionTask = null;
    Task? proxyTask = null;
    try
    {
        var options = Parse(args);
        if (!int.TryParse(options.GetValueOrDefault("--port", "24864"), out int port) || port is < 1 or > 65535)
            throw new ArgumentException("--port must be between 1 and 65535.");
        bool useSerial = options.ContainsKey("--serial");
        if (useSerial && options.ContainsKey("--dry-run")) throw new ArgumentException("Choose --serial or --dry-run.");
        if (options.ContainsKey("--udid") && !options.ContainsKey("--iproxy"))
            throw new ArgumentException("--udid requires --iproxy.");
        if (useSerial)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Serial host requires Windows.");
            string left = options.GetValueOrDefault("--left", "COM5"), right = options.GetValueOrDefault("--right", "COM6");
            if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Serial ports must differ.");
            serial = new(left, right);
        }
        ITouchSink sink = serial is null ? new ConsoleTouchSink() : serial;
        if (options.TryGetValue("--iproxy", out string? executable))
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-l");
            if (options.TryGetValue("--udid", out string? udid))
            { start.ArgumentList.Add("-u"); start.ArgumentList.Add(udid); }
            start.ArgumentList.Add($"{port}:24864");
            proxy = Process.Start(start) ?? throw new IOException("Could not start iproxy.");
            proxyTask = proxy.WaitForExitAsync(stop.Token);
        }
        Console.WriteLine(useSerial ? "Serial output enabled." : "Dry run: no serial ports opened.");
        Console.WriteLine($"Connecting to iOS through 127.0.0.1:{port}. Ctrl+C to stop.");
        serialTask = serial?.RunAsync(stop.Token);
        connectionTask = ConnectLoopAsync(port, sink, stop.Token);
        var tasks = new List<Task> { connectionTask };
        if (serialTask is not null) tasks.Add(serialTask);
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
        foreach (Task? task in new[] { connectionTask, serialTask, proxyTask })
            if (task is not null) try { await task; } catch (Exception) { /* Error reported by supervisor. */ }
        serial?.Dispose();
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
        if (name is "--serial" or "--dry-run") { options.Add(name, "true"); continue; }
        if (name is not ("--left" or "--right" or "--port" or "--iproxy" or "--udid"))
            throw new ArgumentException($"Unknown option {name}. Use --help.");
        if (++i >= args.Length || args[i].StartsWith("--")) throw new ArgumentException($"Missing value for {name}.");
        options.Add(name, args[i]);
    }
    return options;
}

static async Task ConnectLoopAsync(int port, ITouchSink sink, CancellationToken token)
{
    while (!token.IsCancellationRequested)
    {
        try
        {
            using var client = new TcpClient { NoDelay = true };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            Console.WriteLine("Connected; negotiating Brokencca protocol.");
            await new InputSession(sink).RunAsync(client.GetStream(), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException)
        { Console.Error.WriteLine($"Connection ended: {ex.Message} Retrying in 1 s."); }
        finally { sink.Reset(); }
        await Task.Delay(1000, token);
    }
}

sealed class ConsoleTouchSink : ITouchSink
{
    private TouchState current = TouchState.Empty;
    public void Apply(TouchState state)
    {
        if (current.SameAs(state)) return;
        current = state;
        Console.WriteLine($"Zones: [{string.Join(", ", Enumerable.Range(0, 240).Where(i => state[i]))}]");
    }
    public void Reset() => Apply(TouchState.Empty);
}
