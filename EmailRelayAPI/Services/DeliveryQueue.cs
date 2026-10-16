using System.Collections.Concurrent;
using System.Threading.Channels;

namespace EmailRelayAPI.Services;

/// <summary>In-process work queue shared by the acceptor and the dispatcher workers.</summary>
public interface IDeliveryQueue
{
    void Enqueue(Guid messageId);
    ChannelReader<Guid> Reader { get; }
    bool TryBeginProcessing(Guid messageId);
    void EndProcessing(Guid messageId);
    int InFlightCount { get; }
    int PendingCount { get; }
}

public class DeliveryQueue : IDeliveryQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public ChannelReader<Guid> Reader => _channel.Reader;

    public void Enqueue(Guid messageId)
    {
        if (_inFlight.ContainsKey(messageId)) return;
        if (!_pending.TryAdd(messageId, 0)) return;
        _channel.Writer.TryWrite(messageId);
    }

    public bool TryBeginProcessing(Guid messageId)
    {
        _pending.TryRemove(messageId, out _);
        return _inFlight.TryAdd(messageId, 0);
    }

    public void EndProcessing(Guid messageId) => _inFlight.TryRemove(messageId, out _);

    public int InFlightCount => _inFlight.Count;
    public int PendingCount => _pending.Count;
}
