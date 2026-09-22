using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Catopumx;

/// <summary>
/// Multi-consumer broadcast of ingested events to every connected SSE
/// client. .NET's <see cref="Channel{T}"/> is single-consumer per item (a
/// write is delivered to exactly one reader), unlike Rust's
/// tokio::sync::broadcast, so fan-out is done explicitly here: each
/// subscriber gets its own bounded channel, and <see cref="Publish"/> writes
/// to all of them. A subscriber that falls behind has its oldest buffered
/// event dropped rather than blocking the publisher or killing the
/// connection, mirroring the original's "lagged" semantics.
/// </summary>
public sealed class EventBus
{
    private const int PerSubscriberCapacity = 256;

    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(PerSubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id) => _subscribers.TryRemove(id, out _);

    /// <summary>
    /// No subscribers is not an error: it just means no dashboard is
    /// currently connected to /api/stream.
    /// </summary>
    public void Publish(string @event)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(@event);
        }
    }
}
