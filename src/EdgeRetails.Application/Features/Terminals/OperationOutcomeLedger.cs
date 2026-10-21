using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Exceptions;

namespace EdgeRetails.Application.Features.Terminals;

public enum OperationOutcomeState
{
    NotFound = 0,
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
    OutcomeUnknown = 4
}

public enum OperationClaimStatus
{
    Acquired = 1,
    InFlight = 2,
    AlreadySucceeded = 3,
    AlreadyFailed = 4
}

public sealed class OperationClaimResult
{
    public OperationClaimStatus Status { get; init; }
    public OperationOutcomeRecord? Outcome { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static OperationClaimResult Acquired(OperationOutcomeRecord outcome) =>
        new() { Status = OperationClaimStatus.Acquired, Outcome = outcome };

    public static OperationClaimResult InFlight(OperationOutcomeRecord outcome) =>
        new() { Status = OperationClaimStatus.InFlight, Outcome = outcome };

    public static OperationClaimResult AlreadySucceeded(OperationOutcomeRecord outcome) =>
        new() { Status = OperationClaimStatus.AlreadySucceeded, Outcome = outcome };

    public static OperationClaimResult AlreadyFailed(OperationOutcomeRecord outcome, string? errorCode = null, string? errorMessage = null) =>
        new()
        {
            Status = OperationClaimStatus.AlreadyFailed,
            Outcome = outcome,
            ErrorCode = errorCode ?? outcome.ErrorCode,
            ErrorMessage = errorMessage ?? outcome.ErrorMessage
        };
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
    public string? ResponsePayload { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseContentType { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public interface IOperationOutcomeLedger
{
    Task<OperationOutcomeRecord?> GetOutcomeAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default);

    Task<OperationClaimResult> ClaimAsync(
        Guid clientOperationId,
        string operationType,
        string payloadFingerprint,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
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

    Task RecordSuccessWithResponseAsync(
        Guid clientOperationId,
        string operationType,
        Guid entityId,
        string? responsePayload,
        int responseStatusCode = 200,
        string responseContentType = "application/json",
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

    Task RecordFailureWithResponseAsync(
        Guid clientOperationId,
        string operationType,
        string errorCode,
        string errorMessage,
        string? responsePayload = null,
        int responseStatusCode = 400,
        string responseContentType = "application/json",
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
    public const string DefaultVersion = "v1";

    public static string ComputeSha256(string payload)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public static string ComputeSha256(params string?[] parts)
    {
        var payload = string.Join("\u001F", parts.Select(x => x ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public static string ComputeCanonicalSha256(object? payload)
    {
        var canonicalBytes = CanonicalPayloadSerializer.SerializeToCanonicalBytes(payload);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
    }

    public static string ComputeCanonicalSha256(string rawJson)
    {
        var canonicalBytes = CanonicalPayloadSerializer.SerializeJsonStringToCanonicalBytes(rawJson);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();
    }

    public static string ComputeVersionedFingerprint(string operationType, object? domainProjection, string version = DefaultVersion)
    {
        var canonicalBytes = CanonicalPayloadSerializer.SerializeToCanonicalBytes(domainProjection);
        var canonicalJson = Encoding.UTF8.GetString(canonicalBytes);
        var preimage = $"{version}:{operationType}:{canonicalJson}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preimage))).ToLowerInvariant();
        return $"{version}:sha256:{hash}";
    }

    public static string ComputeVersionedFingerprintFromJson(string operationType, string canonicalJson, string version = DefaultVersion)
    {
        var normalizedBytes = CanonicalPayloadSerializer.SerializeJsonStringToCanonicalBytes(canonicalJson);
        var normalizedJson = Encoding.UTF8.GetString(normalizedBytes);
        var preimage = $"{version}:{operationType}:{normalizedJson}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(preimage))).ToLowerInvariant();
        return $"{version}:sha256:{hash}";
    }
}

public class InMemoryOperationOutcomeLedger : IOperationOutcomeLedger
{
    private readonly ConcurrentDictionary<Guid, OperationOutcomeRecord> _records = new();
    private readonly object _syncRoot = new();

    public Task<OperationOutcomeRecord?> GetOutcomeAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        _records.TryGetValue(clientOperationId, out var record);
        return Task.FromResult(record);
    }

    public Task<OperationClaimResult> ClaimAsync(
        Guid clientOperationId,
        string operationType,
        string payloadFingerprint,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (_records.TryGetValue(clientOperationId, out var existing))
            {
                if (!string.IsNullOrEmpty(existing.OperationType) &&
                    !string.Equals(existing.OperationType, operationType, StringComparison.Ordinal))
                {
                    throw new IdempotencyOperationTypeMismatchException(clientOperationId, existing.OperationType, operationType);
                }

                if (existing.ActorId.HasValue && (!actorId.HasValue || existing.ActorId.Value != actorId.Value))
                {
                    throw new IdempotencyContextMismatchException(clientOperationId, "ActorUserId", existing.ActorId.Value.ToString(), actorId?.ToString() ?? "null");
                }

                if (existing.TerminalId.HasValue && (!terminalId.HasValue || existing.TerminalId.Value != terminalId.Value))
                {
                    throw new IdempotencyContextMismatchException(clientOperationId, "TerminalId", existing.TerminalId.Value.ToString(), terminalId?.ToString() ?? "null");
                }

                if (!string.IsNullOrEmpty(existing.PayloadFingerprint) &&
                    !string.Equals(existing.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal))
                {
                    throw new IdempotencyPayloadMismatchException(clientOperationId, existing.PayloadFingerprint, payloadFingerprint);
                }

                return existing.State switch
                {
                    OperationOutcomeState.Pending => Task.FromResult(OperationClaimResult.InFlight(existing)),
                    OperationOutcomeState.Succeeded => Task.FromResult(OperationClaimResult.AlreadySucceeded(existing)),
                    OperationOutcomeState.Failed => Task.FromResult(OperationClaimResult.AlreadyFailed(existing, existing.ErrorCode, existing.ErrorMessage)),
                    _ => Task.FromResult(OperationClaimResult.InFlight(existing))
                };
            }

            var outcome = new OperationOutcomeRecord
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
            };

            _records[clientOperationId] = outcome;
            return Task.FromResult(OperationClaimResult.Acquired(outcome));
        }
    }

    private static void ValidateIdentityImmutability(
        Guid clientOperationId,
        OperationOutcomeRecord existing,
        string operationType,
        string? payloadFingerprint,
        Guid? actorId,
        Guid? terminalId)
    {
        if (!string.IsNullOrEmpty(existing.OperationType) &&
            !string.Equals(existing.OperationType, operationType, StringComparison.Ordinal))
        {
            throw new IdempotencyOperationTypeMismatchException(clientOperationId, existing.OperationType, operationType);
        }

        if (existing.ActorId.HasValue && actorId.HasValue && existing.ActorId.Value != actorId.Value)
        {
            throw new IdempotencyContextMismatchException(clientOperationId, "ActorUserId", existing.ActorId.Value.ToString(), actorId.Value.ToString());
        }

        if (existing.TerminalId.HasValue && terminalId.HasValue && existing.TerminalId.Value != terminalId.Value)
        {
            throw new IdempotencyContextMismatchException(clientOperationId, "TerminalId", existing.TerminalId.Value.ToString(), terminalId.Value.ToString());
        }

        if (!string.IsNullOrEmpty(existing.PayloadFingerprint) &&
            !string.IsNullOrEmpty(payloadFingerprint) &&
            !string.Equals(existing.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal))
        {
            throw new IdempotencyPayloadMismatchException(clientOperationId, existing.PayloadFingerprint, payloadFingerprint);
        }
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
        lock (_syncRoot)
        {
            if (_records.TryGetValue(clientOperationId, out var existing))
            {
                ValidateIdentityImmutability(clientOperationId, existing, operationType, payloadFingerprint, actorId, terminalId);
                if (existing.State != OperationOutcomeState.Succeeded)
                {
                    existing.State = OperationOutcomeState.Pending;
                    if (!existing.TerminalId.HasValue && terminalId.HasValue) existing.TerminalId = terminalId;
                    if (sessionId.HasValue) existing.SessionId = sessionId;
                }
            }
            else
            {
                _records[clientOperationId] = new OperationOutcomeRecord
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
                };
            }
        }

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
        return RecordSuccessWithResponseAsync(
            clientOperationId,
            operationType,
            entityId,
            responsePayload: null,
            responseStatusCode: 200,
            responseContentType: "application/json",
            documentNumber: documentNumber,
            actorId: actorId,
            terminalId: terminalId,
            sessionId: sessionId,
            payloadFingerprint: payloadFingerprint,
            cancellationToken: cancellationToken);
    }

    public Task RecordSuccessWithResponseAsync(
        Guid clientOperationId,
        string operationType,
        Guid entityId,
        string? responsePayload,
        int responseStatusCode = 200,
        string responseContentType = "application/json",
        string? documentNumber = null,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (_records.TryGetValue(clientOperationId, out var existing))
            {
                ValidateIdentityImmutability(clientOperationId, existing, operationType, payloadFingerprint, actorId, terminalId);
                if (existing.State != OperationOutcomeState.Succeeded)
                {
                    existing.State = OperationOutcomeState.Succeeded;
                    existing.EntityId = entityId;
                    existing.DocumentNumber = documentNumber;
                    existing.WasCommitted = true;
                    existing.ResponsePayload = responsePayload;
                    existing.ResponseStatusCode = responseStatusCode;
                    existing.ResponseContentType = responseContentType;
                    if (!existing.TerminalId.HasValue && terminalId.HasValue) existing.TerminalId = terminalId;
                    if (sessionId.HasValue) existing.SessionId = sessionId;
                    existing.CompletedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                _records[clientOperationId] = new OperationOutcomeRecord
                {
                    ClientOperationId = clientOperationId,
                    State = OperationOutcomeState.Succeeded,
                    OperationType = operationType,
                    EntityId = entityId,
                    DocumentNumber = documentNumber,
                    WasCommitted = true,
                    ResponsePayload = responsePayload,
                    ResponseStatusCode = responseStatusCode,
                    ResponseContentType = responseContentType,
                    ActorId = actorId,
                    TerminalId = terminalId,
                    SessionId = sessionId,
                    PayloadFingerprint = payloadFingerprint,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }
        }

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
        return RecordFailureWithResponseAsync(
            clientOperationId,
            operationType,
            errorCode,
            errorMessage,
            responsePayload: null,
            responseStatusCode: 400,
            responseContentType: "application/json",
            actorId: actorId,
            terminalId: terminalId,
            sessionId: sessionId,
            payloadFingerprint: payloadFingerprint,
            cancellationToken: cancellationToken);
    }

    public Task RecordFailureWithResponseAsync(
        Guid clientOperationId,
        string operationType,
        string errorCode,
        string errorMessage,
        string? responsePayload = null,
        int responseStatusCode = 400,
        string responseContentType = "application/json",
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        lock (_syncRoot)
        {
            if (_records.TryGetValue(clientOperationId, out var existing))
            {
                ValidateIdentityImmutability(clientOperationId, existing, operationType, payloadFingerprint, actorId, terminalId);
                if (existing.State != OperationOutcomeState.Succeeded)
                {
                    existing.State = OperationOutcomeState.Failed;
                    existing.ErrorCode = errorCode;
                    existing.ErrorMessage = errorMessage;
                    existing.WasCommitted = false;
                    existing.ResponsePayload = responsePayload;
                    existing.ResponseStatusCode = responseStatusCode;
                    existing.ResponseContentType = responseContentType;
                    if (!existing.TerminalId.HasValue && terminalId.HasValue) existing.TerminalId = terminalId;
                    if (sessionId.HasValue) existing.SessionId = sessionId;
                    existing.CompletedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                _records[clientOperationId] = new OperationOutcomeRecord
                {
                    ClientOperationId = clientOperationId,
                    State = OperationOutcomeState.Failed,
                    OperationType = operationType,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage,
                    WasCommitted = false,
                    ResponsePayload = responsePayload,
                    ResponseStatusCode = responseStatusCode,
                    ResponseContentType = responseContentType,
                    ActorId = actorId,
                    TerminalId = terminalId,
                    SessionId = sessionId,
                    PayloadFingerprint = payloadFingerprint,
                    CompletedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }
        }

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
        lock (_syncRoot)
        {
            if (_records.TryGetValue(clientOperationId, out var existing))
            {
                ValidateIdentityImmutability(clientOperationId, existing, operationType, payloadFingerprint, actorId, terminalId);
                if (existing.State != OperationOutcomeState.Succeeded)
                {
                    existing.State = OperationOutcomeState.OutcomeUnknown;
                    existing.WasCommitted = false;
                    if (!existing.TerminalId.HasValue && terminalId.HasValue) existing.TerminalId = terminalId;
                    if (sessionId.HasValue) existing.SessionId = sessionId;
                }
            }
            else
            {
                _records[clientOperationId] = new OperationOutcomeRecord
                {
                    ClientOperationId = clientOperationId,
                    State = OperationOutcomeState.OutcomeUnknown,
                    OperationType = operationType,
                    WasCommitted = false,
                    ActorId = actorId,
                    TerminalId = terminalId,
                    SessionId = sessionId,
                    PayloadFingerprint = payloadFingerprint,
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }
        }

        return Task.CompletedTask;
    }
}
