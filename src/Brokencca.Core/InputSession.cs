namespace Brokencca.Core;

public interface ITouchSink
{
    void Apply(TouchState state);
    void Reset();
}

public sealed class InputSession(ITouchSink sink, TimeSpan? idleTimeout = null)
{
    private readonly TimeSpan timeout = idleTimeout ?? TimeSpan.FromMilliseconds(500);

    public async Task RunAsync(Stream stream, CancellationToken token)
    {
        sink.Reset();
        uint outgoing = 0;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(3));
            await WireProtocol.WriteAsync(stream, new(MessageType.Hello, outgoing++, WireProtocol.NowUs,
                WireProtocol.HelloPayload), handshake.Token);
            WireMessage hello = await WireProtocol.ReadAsync(stream, handshake.Token);
            if (hello.Type != MessageType.Hello) throw new InvalidDataException("Expected HELLO.");
            uint previous = hello.Sequence;
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(timeout);
                WireMessage message = await WireProtocol.ReadAsync(stream, deadline.Token);
                if (unchecked((int)(message.Sequence - previous)) <= 0)
                    throw new InvalidDataException("Duplicate or stale sequence number.");
                previous = message.Sequence;
                switch (message.Type)
                {
                    case MessageType.Touch: sink.Apply(new(message.Payload)); break;
                    case MessageType.Reset: sink.Reset(); break;
                    // Controller sends full snapshots every 100 ms; other messages cannot renew its lease.
                    default: throw new InvalidDataException("Expected TOUCH or RESET after handshake.");
                }
            }
        }
        finally { sink.Reset(); }
    }
}
