using EdgeRetails.Central.Domain.Outbox;

namespace EdgeRetails.Central.Application.Abstractions;

public interface IOutboxDispatcher
{
    Task EnqueueAsync(
        string messageType,
        string payloadJson,
        Guid? clientOperationId = null,
        CancellationToken cancellationToken = default);

    Task<int> DispatchPendingMessagesAsync(
        int batchSize = 50,
        int maxRetries = CentralOutboxMessage.DefaultMaxRetries,
        CancellationToken cancellationToken = default);
}
