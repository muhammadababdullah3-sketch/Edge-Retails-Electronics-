using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.PerformanceTests;

internal sealed class InMemoryClientOperationIntentStore : IClientOperationIntentStore
{
    private readonly Dictionary<string, (Guid OperationId, string Payload)> _pending = new(StringComparer.Ordinal);

    public Guid GetOrCreate(string operationKey, string payload)
    {
        if (_pending.TryGetValue(operationKey, out var existing) && !string.Equals(existing.Payload, payload, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Payload conflict.");
        }

        if (!_pending.ContainsKey(operationKey))
        {
            _pending[operationKey] = (Guid.CreateVersion7(), payload);
        }

        return _pending[operationKey].OperationId;
    }

    public void Complete(string operationKey, Guid operationId) => _pending.Remove(operationKey);
}
