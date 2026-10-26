using System.Collections.Concurrent;
using EdgeRetails.Central.Application.Abstractions;

namespace EdgeRetails.Central.Application.Services;

public class InMemoryDeduplicatingOutboxSink : IOutboxConsumerSink
{
    private readonly ConcurrentDictionary<Guid, int> _processedMessageDeliveries = new();
    private readonly ConcurrentBag<Guid> _distinctDeliveredIds = new();

    public Task DeliverAsync(
        Guid messageId,
        string eventType,
        string aggregateType,
        string aggregateId,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        var count = _processedMessageDeliveries.AddOrUpdate(messageId, 1, (_, current) => current + 1);
        if (count == 1)
        {
            _distinctDeliveredIds.Add(messageId);
        }

        return Task.CompletedTask;
    }

    public bool HasProcessed(Guid messageId) => _processedMessageDeliveries.ContainsKey(messageId);

    public int GetDeliveryCount(Guid messageId) => _processedMessageDeliveries.TryGetValue(messageId, out var count) ? count : 0;

    public IReadOnlyCollection<Guid> DeliveredMessageIds => _distinctDeliveredIds.ToArray();
}
