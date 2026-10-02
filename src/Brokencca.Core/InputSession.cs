namespace Brokencca.Core;

public interface ITouchSink
{
    void Apply(TouchState state);
    void Reset();
}

public interface ITimedTouchSink : ITouchSink
{
    void Apply(TouchState state, long arrivalTimestamp);
}

public interface IInputSessionObserver
{
    void MessageReceived(MessageType type, uint sequence, ulong senderTimestampUs, long arrivalTimestamp);
    void SequenceGap(uint missingMessages);
    void SinkCompleted(MessageType type, long startedTimestamp, long completedTimestamp);
}

public sealed class InputSession(ITouchSink sink, TimeSpan? idleTimeout = null, IInputSessionObserver? observer = null,
    bool enableVideo = false, Action<ReadOnlyMemory<byte>>? videoSessionReady = null, Action? videoSessionEnded = null)
{
    private readonly TimeSpan timeout = idleTimeout ?? TimeSpan.FromMilliseconds(500);

    public async Task RunAsync(Stream stream, CancellationToken token)
    {
        sink.Reset();
        uint outgoing = 0;
        byte[]? sessionToken = enableVideo ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(16) : null;
        bool videoReadyInvoked = false;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(3));
            byte version = enableVideo ? (byte)2 : (byte)1;
            byte[] helloPayload = enableVideo ? WireProtocol.VideoHelloPayload(sessionToken!) : WireProtocol.HelloPayload;
            await WireProtocol.WriteAsync(stream, new(MessageType.Hello, outgoing++, WireProtocol.NowUs,
                helloPayload, version), handshake.Token);
            WireMessage hello = await WireProtocol.ReadAsync(stream, handshake.Token);
            if (hello.Type != MessageType.Hello || hello.Version != version || !hello.Payload.AsSpan().SequenceEqual(helloPayload))
                throw new InvalidDataException("Expected matching HELLO. Update the iOS app for video mode.");
            uint previous = hello.Sequence;
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(timeout);
                WireMessage message = await WireProtocol.ReadAsync(stream, deadline.Token);
                if (message.Version != version) throw new InvalidDataException("Control version changed during session.");
                long arrived = System.Diagnostics.Stopwatch.GetTimestamp();
                observer?.MessageReceived(message.Type, message.Sequence, message.TimestampUs, arrived);
                int distance = unchecked((int)(message.Sequence - previous));
                if (distance <= 0)
                    throw new InvalidDataException("Duplicate or stale sequence number.");
                if (distance > 1) observer?.SequenceGap((uint)(distance - 1));
                previous = message.Sequence;
                long sinkStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                switch (message.Type)
                {
                    case MessageType.Touch:
                        var state = new TouchState(message.Payload);
                        if (sessionToken is not null && !videoReadyInvoked && !state.SameAs(TouchState.Empty))
                            throw new InvalidDataException("Video session requires an initial empty TOUCH.");
                        if (sink is ITimedTouchSink timed) timed.Apply(state, arrived);
                        else sink.Apply(state);
                        if (sessionToken is not null && !videoReadyInvoked)
                        {
                            videoReadyInvoked = true;
                            videoSessionReady?.Invoke(sessionToken);
                        }
                        break;
                    case MessageType.Reset: sink.Reset(); break;
                    // Controller sends full snapshots every 100 ms; other messages cannot renew its lease.
                    default: throw new InvalidDataException("Expected TOUCH or RESET after handshake.");
                }
                observer?.SinkCompleted(message.Type, sinkStarted, System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }
        finally { if (videoReadyInvoked) videoSessionEnded?.Invoke(); sink.Reset(); }
    }
}
