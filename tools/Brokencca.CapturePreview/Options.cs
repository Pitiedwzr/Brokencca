using System.Globalization;
using Brokencca.Capture.Windows;
using Brokencca.Core;
using System.Text.Json;

namespace Brokencca.CapturePreview;

internal sealed record Options
{
    public bool List { get; private set; }
    public bool Mercury { get; private set; }
    public nint? Hwnd { get; private set; }
    public CaptureOptions Capture { get; private set; } = new(NormalizedCrop.Full);
    public bool Diagnostics { get; private set; }
    public bool Smoke { get; private set; }
    public bool CalibrateBlack { get; private set; }
    public bool TouccaReference { get; private set; }
    public double Seconds { get; private set; }
    public double InjectDeviceLossAt { get; private set; }
    public int SlowConsumerMs { get; private set; }
    public int CaptureCycles { get; private set; } = 1;
    public string? Profile { get; private set; }
    public string? SaveProfile { get; private set; }
    public static Options Parse(string[] args)
    {
        var result = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--list-windows": result.List = true; break;
                case "--mercury": result.Mercury = true; break;
                case "--hwnd": result.Hwnd = (nint)long.Parse(args[++i].Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture); break;
                case "--fps": result.Capture = result.Capture with { FramesPerSecond = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                case "--crop":
                    string crop = args[++i];
                    if (crop == "client") result.Capture = result.Capture with { Crop = NormalizedCrop.Full, FullItem = false };
                    else if (crop == "item") result.Capture = result.Capture with { FullItem = true };
                    else if (crop.StartsWith("normalized:", StringComparison.Ordinal))
                    {
                        double[] values = crop[11..].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                        if (values.Length != 4) throw new ArgumentException("Normalized crop needs x,y,width,height.");
                        result.Capture = result.Capture with { Crop = new(values[0], values[1], values[2], values[3]), FullItem = false };
                    }
                    else throw new ArgumentException("Crop must be client, item, or normalized:x,y,width,height.");
                    break;
                case "--diagnostics": result.Diagnostics = true; break;
                case "--smoke-test": result.Smoke = true; break;
                case "--calibrate-black": result.CalibrateBlack = true; break;
                case "--toucca-reference": result.TouccaReference = true; break;
                case "--profile": result.Profile = args[++i]; break;
                case "--save-profile": result.SaveProfile = args[++i]; break;
                case "--seconds": result.Seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--inject-device-loss-at": result.InjectDeviceLossAt = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--slow-consumer-ms": result.SlowConsumerMs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--capture-cycles": result.CaptureCycles = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        result.Capture.Validate();
        if (result.Profile is not null && !args.Contains("--crop"))
        {
            var profile = JsonSerializer.Deserialize<CaptureProfile>(File.ReadAllText(result.Profile)) ?? throw new ArgumentException("Empty profile.");
            profile.ValidateFor(profile.SourceWidth, profile.SourceHeight);
            result.Capture = result.Capture with { Crop = profile.Crop };
        }
        if (result.Mercury && result.Hwnd is not null) throw new ArgumentException("Choose either --mercury or --hwnd.");
        if (!result.List && !result.Mercury && result.Hwnd is null) throw new ArgumentException("Select --mercury or --hwnd (use --list-windows to see HWNDs).");
        if (!double.IsFinite(result.Seconds) || !double.IsFinite(result.InjectDeviceLossAt) || result.Seconds < 0 || result.InjectDeviceLossAt < 0 || result.SlowConsumerMs is < 0 or > 5000) throw new ArgumentException("Invalid duration or slow-consumer delay.");
        if (result.CaptureCycles is < 1 or > 100 || result.CaptureCycles > 1 && !result.Smoke) throw new ArgumentException("--capture-cycles must be 1..100 and requires --smoke-test for repeated cycles.");
        if (result.Smoke && (result.Capture.FullItem || result.Capture.Crop != NormalizedCrop.Full)) throw new ArgumentException("Fixture smoke test requires the full client crop.");
        return result;
    }
}
