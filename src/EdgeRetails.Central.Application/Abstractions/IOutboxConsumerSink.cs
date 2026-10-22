namespace EdgeRetails.Central.Application.Abstractions;

public interface IOutboxConsumerSink
{
    Task DeliverAsync(
        Guid messageId,
        string eventType,
        string aggregateType,
        string aggregateId,
        string payloadJson,
        CancellationToken cancellationToken = default);

    bool HasProcessed(Guid messageId);
    int GetDeliveryCount(Guid messageId);
    IReadOnlyCollection<Guid> DeliveredMessageIds { get; }
}
