using System.Text.Json;
using Brokencca.Capture.Windows;

namespace Brokencca.CapturePreview;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("CapturePreview --list-windows | (--hwnd 0xHEX | --mercury) [--crop client|item|normalized:x,y,w,h] [--fps 60] [--diagnostics] [--seconds N] [--calibrate-black] [--toucca-reference] [--profile FILE] [--save-profile FILE]\nC: sample boot black; G: toggle circle; arrows: move center; +/-: radius; S: confirm and save; P: pause consumer 250 ms; R: retry/reselect; Esc: stop.\nTest controls: --smoke-test --slow-consumer-ms N --inject-device-loss-at SECONDS --capture-cycles N");
            return 0;
        }
        try
        {
            var options = Options.Parse(args);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles();
            if (options.List)
            {
                foreach (var window in WindowLocator.List())
                {
                    try { Console.WriteLine(JsonSerializer.Serialize(new { hwnd = $"0x{window.Hwnd:X}", window.ProcessId, window.ProcessName, window.Title, window.ClassName, overlay = WindowLocator.IsOverlay(window), geometry = WindowGeometry.Read(window.Hwnd) })); }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                return 0;
            }
            using var form = new PreviewForm(options); Application.Run(form);
            return form.ExitCode;
        }
        catch (Exception e) { Console.Error.WriteLine(e.Message); return 1; }
    }
}
