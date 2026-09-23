using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Production.Outbox;

public interface IOutboxEffectHandler
{
    string EffectType { get; }
    Task ExecuteAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

public sealed class OutboxProcessor
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30)
    ];

    private const int MaxAttempts = 5;

    private readonly IOutboxRepository _repository;
    private readonly IReadOnlyDictionary<string, IOutboxEffectHandler> _handlers;

    public OutboxProcessor(
        IOutboxRepository repository,
        IEnumerable<IOutboxEffectHandler> handlers)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _handlers = (handlers ?? throw new ArgumentNullException(nameof(handlers)))
            .ToDictionary(h => h.EffectType, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<int> ProcessPendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
    {
        var messages = await _repository.GetPendingMessagesAsync(batchSize, cancellationToken);
        var processedCount = 0;

        foreach (var message in messages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!_handlers.TryGetValue(message.EffectType, out var handler))
            {
                await _repository.MarkActionRequiredAsync(
                    message.Id,
                    $"No outbox effect handler registered for '{message.EffectType}'.",
                    cancellationToken);
                continue;
            }

            try
            {
                await handler.ExecuteAsync(message, cancellationToken);
                await _repository.MarkCompletedAsync(message.Id, DateTimeOffset.UtcNow, cancellationToken);
                processedCount++;
            }
            catch (NonRetryableOutboxEffectException ex)
            {
                await _repository.MarkActionRequiredAsync(
                    message.Id,
                    $"Non-retryable outbox effect error: {ex.Message}",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                var attempt = message.AttemptCount + 1;
                if (attempt >= MaxAttempts)
                {
                    await _repository.MarkActionRequiredAsync(
                        message.Id,
                        $"Outbox effect failed after {attempt} attempts: {ex.Message}",
                        cancellationToken);
                }
                else
                {
                    var delay = RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)];
                    var nextAttempt = DateTimeOffset.UtcNow.Add(delay);
                    await _repository.MarkFailedAsync(
                        message.Id,
                        $"{ex.GetType().Name}: {ex.Message}",
                        nextAttempt,
                        cancellationToken);
                }
            }
        }

        return processedCount;
    }
}

public sealed class NonRetryableOutboxEffectException : Exception
{
    public NonRetryableOutboxEffectException(string message) : base(message) { }
    public NonRetryableOutboxEffectException(string message, Exception innerException) : base(message, innerException) { }
}
