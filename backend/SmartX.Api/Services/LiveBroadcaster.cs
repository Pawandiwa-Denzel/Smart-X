using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SmartX.Api.Services;

// Fan-out for Server-Sent Events. Every open dashboard tab gets its own small
// bounded channel; if a tab falls behind, the oldest events are dropped for
// that tab only, so one slow browser can never stall the ingestion pipeline.
public class LiveBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel<(string Type, object Payload)>> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public void Publish(string type, object payload)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite((type, payload));
        }
    }

    public async IAsyncEnumerable<SseItem<object>> Subscribe([EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<(string, object)>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[id] = channel;

        try
        {
            yield return new SseItem<object>(new { connectedAt = DateTime.UtcNow }, "hello");

            while (!ct.IsCancellationRequested)
            {
                // Wake up at least every 15s to send a keep-alive ping so
                // proxies don't close an idle connection.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));

                bool hasData;
                try
                {
                    hasData = await channel.Reader.WaitToReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    hasData = false;
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (!hasData)
                {
                    yield return new SseItem<object>(new { at = DateTime.UtcNow }, "ping");
                    continue;
                }

                while (channel.Reader.TryRead(out var evt))
                {
                    yield return new SseItem<object>(evt.Item2, evt.Item1);
                }
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}
