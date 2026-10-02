using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace EdgeRetails.Application.Features.Terminals;

public enum OperationOutcomeState
{
    NotFound = 0,
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
    OutcomeUnknown = 4
}

public sealed class OperationOutcomeRecord
{
    public Guid ClientOperationId { get; init; }
    public OperationOutcomeState State { get; set; } = OperationOutcomeState.Pending;
    public string OperationType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? DocumentNumber { get; set; }
    public bool WasCommitted { get; set; }
    public string? PayloadFingerprint { get; set; }
    public Guid? ActorId { get; set; }
    public Guid? TerminalId { get; set; }
    public Guid? SessionId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public interface IOperationOutcomeLedger
{
    Task<OperationOutcomeRecord?> GetOutcomeAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default);

    Task RecordPendingAsync(
        Guid clientOperationId,
        string operationType,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default);

    Task RecordSuccessAsync(
        Guid clientOperationId,
        string operationType,
        Guid entityId,
        string? documentNumber = null,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default);

    Task RecordFailureAsync(
        Guid clientOperationId,
        string operationType,
        string errorCode,
        string errorMessage,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default);

    Task RecordOutcomeUnknownAsync(
        Guid clientOperationId,
        string operationType,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default);
}

public static class OperationPayloadFingerprint
{
    public static string ComputeSha256(string payload)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public static string ComputeSha256(params string?[] parts)
    {
        var payload = string.Join("\u001F", parts.Select(x => x ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

public class InMemoryOperationOutcomeLedger : IOperationOutcomeLedger
{
    private readonly ConcurrentDictionary<Guid, OperationOutcomeRecord> _records = new();

    public Task<OperationOutcomeRecord?> GetOutcomeAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        _records.TryGetValue(clientOperationId, out var record);
        return Task.FromResult(record);
    }

    public Task RecordPendingAsync(
        Guid clientOperationId,
        string operationType,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        _records.AddOrUpdate(
            clientOperationId,
            _ => new OperationOutcomeRecord
            {
                ClientOperationId = clientOperationId,
                State = OperationOutcomeState.Pending,
                OperationType = operationType,
                WasCommitted = false,
                ActorId = actorId,
                TerminalId = terminalId,
                SessionId = sessionId,
                PayloadFingerprint = payloadFingerprint,
                CreatedAt = DateTimeOffset.UtcNow
            },
            (_, existing) =>
            {
                if (existing.State == OperationOutcomeState.Succeeded)
                {
                    return existing;
                }

                existing.State = OperationOutcomeState.Pending;
                existing.OperationType = operationType;
                if (actorId.HasValue)
                {
                    existing.ActorId = actorId;
                }
                if (terminalId.HasValue)
                {
                    existing.TerminalId = terminalId;
                }
                if (sessionId.HasValue)
                {
                    existing.SessionId = sessionId;
                }
                if (!string.IsNullOrEmpty(payloadFingerprint))
                {
                    existing.PayloadFingerprint = payloadFingerprint;
                }
                return existing;
            });

        return Task.CompletedTask;
    }

    public Task RecordSuccessAsync(
        Guid clientOperationId,
        string operationType,
        Guid entityId,
        string? documentNumber = null,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        _records.AddOrUpdate(
            clientOperationId,
            _ => new OperationOutcomeRecord
            {
                ClientOperationId = clientOperationId,
                State = OperationOutcomeState.Succeeded,
                OperationType = operationType,
                EntityId = entityId,
                DocumentNumber = documentNumber,
                WasCommitted = true,
                ActorId = actorId,
                TerminalId = terminalId,
                SessionId = sessionId,
                PayloadFingerprint = payloadFingerprint,
                CompletedAt = DateTimeOffset.UtcNow
            },
            (_, existing) =>
            {
                if (existing.State == OperationOutcomeState.Succeeded)
                {
                    return existing;
                }

                existing.State = OperationOutcomeState.Succeeded;
                existing.OperationType = operationType;
                existing.EntityId = entityId;
                existing.DocumentNumber = documentNumber;
                existing.WasCommitted = true;
                if (actorId.HasValue)
                {
                    existing.ActorId = actorId;
                }
                if (terminalId.HasValue)
                {
                    existing.TerminalId = terminalId;
                }
                if (sessionId.HasValue)
                {
                    existing.SessionId = sessionId;
                }
                if (!string.IsNullOrEmpty(payloadFingerprint))
                {
                    existing.PayloadFingerprint = payloadFingerprint;
                }
                existing.CompletedAt = DateTimeOffset.UtcNow;
                return existing;
            });

        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(
        Guid clientOperationId,
        string operationType,
        string errorCode,
        string errorMessage,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        _records.AddOrUpdate(
            clientOperationId,
            _ => new OperationOutcomeRecord
            {
                ClientOperationId = clientOperationId,
                State = OperationOutcomeState.Failed,
                OperationType = operationType,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                WasCommitted = false,
                ActorId = actorId,
                TerminalId = terminalId,
                SessionId = sessionId,
                PayloadFingerprint = payloadFingerprint,
                CompletedAt = DateTimeOffset.UtcNow
            },
            (_, existing) =>
            {
                if (existing.State == OperationOutcomeState.Succeeded)
                {
                    return existing;
                }

                existing.State = OperationOutcomeState.Failed;
                existing.OperationType = operationType;
                existing.ErrorCode = errorCode;
                existing.ErrorMessage = errorMessage;
                existing.WasCommitted = false;
                if (actorId.HasValue)
                {
                    existing.ActorId = actorId;
                }
                if (terminalId.HasValue)
                {
                    existing.TerminalId = terminalId;
                }
                if (sessionId.HasValue)
                {
                    existing.SessionId = sessionId;
                }
                if (!string.IsNullOrEmpty(payloadFingerprint))
                {
                    existing.PayloadFingerprint = payloadFingerprint;
                }
                existing.CompletedAt = DateTimeOffset.UtcNow;
                return existing;
            });

        return Task.CompletedTask;
    }

    public Task RecordOutcomeUnknownAsync(
        Guid clientOperationId,
        string operationType,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        _records.AddOrUpdate(
            clientOperationId,
            _ => new OperationOutcomeRecord
            {
                ClientOperationId = clientOperationId,
                State = OperationOutcomeState.OutcomeUnknown,
                OperationType = operationType,
                WasCommitted = false,
                ActorId = actorId,
                TerminalId = terminalId,
                SessionId = sessionId,
                PayloadFingerprint = payloadFingerprint
            },
            (_, existing) =>
            {
                if (existing.State == OperationOutcomeState.Succeeded)
                {
                    return existing;
                }

                existing.State = OperationOutcomeState.OutcomeUnknown;
                existing.OperationType = operationType;
                existing.WasCommitted = false;
                if (actorId.HasValue)
                {
                    existing.ActorId = actorId;
                }
                if (terminalId.HasValue)
                {
                    existing.TerminalId = terminalId;
                }
                if (sessionId.HasValue)
                {
                    existing.SessionId = sessionId;
                }
                if (!string.IsNullOrEmpty(payloadFingerprint))
                {
                    existing.PayloadFingerprint = payloadFingerprint;
                }
                return existing;
            });

        return Task.CompletedTask;
    }
}
