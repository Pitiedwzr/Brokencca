using System.IO.Ports;

namespace Brokencca.Host;

internal interface ISerialEndpoint : IDisposable
{
    string PortName { get; }
    int BytesToRead { get; }
    int BytesToWrite { get; }
    event Action? DataAvailable;
    int Read(byte[] buffer, int offset, int count);
    void Write(byte[] buffer, int offset, int count);
}

internal sealed class SerialEndpoint : ISerialEndpoint
{
    private readonly SerialPort port;
    public SerialEndpoint(string name)
    {
        port = new(name, 115200, Parity.None, 8, StopBits.One)
        { ReadTimeout = 100, WriteTimeout = 100, Handshake = Handshake.None };
        port.DataReceived += OnDataReceived;
        try { port.Open(); }
        catch { port.Dispose(); throw; }
    }
    public string PortName => port.PortName;
    public int BytesToRead => port.BytesToRead;
    public int BytesToWrite => port.BytesToWrite;
    public event Action? DataAvailable;
    private void OnDataReceived(object sender, SerialDataReceivedEventArgs args) => DataAvailable?.Invoke();
    public int Read(byte[] buffer, int offset, int count) => port.Read(buffer, offset, count);
    public void Write(byte[] buffer, int offset, int count) => port.Write(buffer, offset, count);
    public void Dispose() { port.DataReceived -= OnDataReceived; port.Dispose(); }
}
