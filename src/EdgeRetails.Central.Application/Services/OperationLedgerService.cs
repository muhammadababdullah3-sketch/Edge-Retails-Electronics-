using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

public class OperationLedgerService : IOperationLedgerService
{
    private readonly ICentralDbContext _dbContext;
    private readonly ICanonicalJsonSerializer _serializer;

    public OperationLedgerService(
        ICentralDbContext dbContext,
        ICanonicalJsonSerializer serializer)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public async Task<OutcomeDto> ExecuteOrReplayAsync<TCommand, TResult>(
        string actorScope,
        Guid? shopId,
        CommandMetaDto meta,
        TCommand payload,
        Func<TCommand, Task<(TResult Result, string? ResultReference, int ResourceVersion, int AuthorityRevision)>> executeAsync,
        string? targetResource = null,
        CancellationToken cancellationToken = default)
    {
        var (outcome, _) = await ExecuteAndCaptureOrReplayAsync(
            actorScope,
            shopId,
            meta,
            payload,
            executeAsync,
            recoverResultAsync: null,
            targetResource,
            cancellationToken);

        return outcome;
    }

    public async Task<(OutcomeDto Outcome, TResult Result)> ExecuteAndCaptureOrReplayAsync<TCommand, TResult>(
        string actorScope,
        Guid? shopId,
        CommandMetaDto meta,
        TCommand payload,
        Func<TCommand, Task<(TResult Result, string? ResultReference, int ResourceVersion, int AuthorityRevision)>> executeAsync,
        Func<CentralOperationRecord, Task<TResult>>? recoverResultAsync = null,
        string? targetResource = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorScope);
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(executeAsync);

        // 1. Recompute canonical payload and SHA-256 fingerprint server-side
        var (canonicalJson, computedSha256) = _serializer.SerializeAndHash(payload);

        if (!string.IsNullOrWhiteSpace(meta.PayloadSha256) &&
            !string.Equals(computedSha256, meta.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException(
                "PAYLOAD_HASH_MISMATCH",
                "operation.payload_hash_mismatch",
                $"Provided payloadSha256 does not match server-computed canonical hash. Expected: {computedSha256}, Provided: {meta.PayloadSha256}");
        }

        if (meta.ClientOperationId == Guid.Empty || meta.FingerprintVersion != 1 || meta.ExpectedVersion < 0)
        {
            throw new CentralDomainException("INVALID_OPERATION_META", "operation.invalid_meta", "Unsupported operation identity/version.");
        }
        var ownsTransaction = _dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await _dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        if (_dbContext.Database.ProviderName?.Contains("Npgsql") == true)
        {
            await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({meta.ClientOperationId.ToString()}, 0))", cancellationToken);
            if (!string.IsNullOrWhiteSpace(targetResource))
            {
                await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({targetResource}, 1))", cancellationToken);
            }
        }

        // 2. Check for existing operation record
        var existing = await _dbContext.CentralOperationRecords
            .FirstOrDefaultAsync(r => r.ClientOperationId == meta.ClientOperationId, cancellationToken);

        if (existing is not null)
        {
            // Verify idempotency match
            if (existing.MatchesIdempotency(actorScope, shopId, meta.OperationType, meta.FingerprintVersion, computedSha256, targetResource))
            {
                TResult recovered = default!;
                if (recoverResultAsync is not null)
                {
                    recovered = await recoverResultAsync(existing);
                }
                if (ownsTransaction && transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                return (MapToOutcome(existing), recovered);
            }

            // Conflict detected: client re-used ClientOperationId with different scope, type, or payload
            throw new CentralDomainException(
                "OPERATION_ID_CONFLICT",
                "operation.id_conflict",
                $"ClientOperationId '{meta.ClientOperationId}' was previously recorded with differing operation identity parameters.");
        }

        // 3. Begin execution within transaction
        try
        {
            var now = DateTimeOffset.UtcNow;
            var operationRecord = CentralOperationRecord.CreatePending(
                clientOperationId: meta.ClientOperationId,
                operationType: meta.OperationType,
                actorScope: actorScope,
                shopId: shopId,
                fingerprintVersion: meta.FingerprintVersion,
                payloadSha256: computedSha256,
                canonicalPayload: canonicalJson,
                expectedVersion: meta.ExpectedVersion,
                policyVersion: meta.PolicyVersion,
                reason: meta.Reason,
                sensitiveApprovalId: meta.SensitiveApprovalId,
                createdAtUtc: now,
                targetResource: targetResource);

            _dbContext.CentralOperationRecords.Add(operationRecord);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Execute actual domain work
            var (result, resultRef, resVersion, authRevision) = await executeAsync(payload);

            var completedNow = DateTimeOffset.UtcNow;
            operationRecord.MarkSucceeded(
                resultReference: resultRef ?? targetResource,
                resourceVersion: resVersion,
                authorityRevision: authRevision,
                completedAtUtc: completedNow);

            await _dbContext.SaveChangesAsync(cancellationToken);
            if (ownsTransaction && transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return (MapToOutcome(operationRecord), result);
        }
        catch (CentralDomainException)
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            if (_dbContext is DbContext concrete)
            {
                concrete.ChangeTracker.Clear();
            }
            throw;
        }
        catch (DbUpdateConcurrencyException concEx)
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            if (_dbContext is DbContext concrete)
            {
                concrete.ChangeTracker.Clear();
            }
            throw new CentralDomainException(
                "CONCURRENCY_CONFLICT",
                "operation.concurrency_conflict",
                $"Database concurrency conflict detected. Expected version {meta.ExpectedVersion} has been modified by another transaction: {concEx.Message}");
        }
        catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx))
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            if (_dbContext is DbContext concrete)
            {
                concrete.ChangeTracker.Clear();
            }

            // Reconcile race condition: another concurrent thread won the insert.
            // Query the canonical winner record.
            for (var retry = 0; retry < 5; retry++)
            {
                var winner = await _dbContext.CentralOperationRecords
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.ClientOperationId == meta.ClientOperationId, cancellationToken);

                if (winner is not null && winner.State != OperationState.PENDING)
                {
                    if (winner.MatchesIdempotency(actorScope, shopId, meta.OperationType, meta.FingerprintVersion, computedSha256, targetResource))
                    {
                        TResult recovered = default!;
                        if (recoverResultAsync is not null)
                        {
                            recovered = await recoverResultAsync(winner);
                        }
                        return (MapToOutcome(winner), recovered);
                    }

                    throw new CentralDomainException(
                        "OPERATION_ID_CONFLICT",
                        "operation.id_conflict",
                        $"ClientOperationId '{meta.ClientOperationId}' was concurrently recorded with differing operation identity parameters.");
                }

                await Task.Delay(50, cancellationToken);
            }

            var finalWinner = await _dbContext.CentralOperationRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.ClientOperationId == meta.ClientOperationId, cancellationToken);

            if (finalWinner is not null)
            {
                if (finalWinner.MatchesIdempotency(actorScope, shopId, meta.OperationType, meta.FingerprintVersion, computedSha256, targetResource))
                {
                    TResult recovered = default!;
                    if (recoverResultAsync is not null)
                    {
                        recovered = await recoverResultAsync(finalWinner);
                    }
                    return (MapToOutcome(finalWinner), recovered);
                }

                throw new CentralDomainException(
                    "OPERATION_ID_CONFLICT",
                    "operation.id_conflict",
                    $"ClientOperationId '{meta.ClientOperationId}' was concurrently recorded with differing operation identity parameters.");
            }

            throw new CentralDomainException(
                "OPERATION_RACE_UNRESOLVED",
                "operation.race_unresolved",
                "Concurrent operation identity conflict could not be reconciled.");
        }
        catch (Exception ex)
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            if (_dbContext is DbContext concrete)
            {
                concrete.ChangeTracker.Clear();
            }
            throw new CentralDomainException(
                "OPERATION_EXECUTION_FAILED",
                "operation.failed",
                $"Operation failed during execution: {ex.Message}");
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("IX_operation_records_ClientOperationId", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("23505", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<OperationStatusDto?> GetOperationStatusAsync(
        Guid clientOperationId,
        string actorScope,
        CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.CentralOperationRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ClientOperationId == clientOperationId, cancellationToken);

        if (record is null)
        {
            return null;
        }

        // Scoping check: callers can only query operations within their authorized scope
        if (!string.Equals(record.ActorScope, actorScope, StringComparison.Ordinal))
        {
            return null;
        }

        return new OperationStatusDto
        {
            ClientOperationId = record.ClientOperationId,
            OperationType = record.OperationType,
            State = record.State.ToString(),
            OutcomeId = record.OutcomeId,
            ResultReference = record.ResultReference,
            ResourceVersion = record.ResourceVersion,
            AuthorityRevision = record.AuthorityRevision,
            CreatedAtUtc = record.CreatedAtUtc,
            CompletedAtUtc = record.CompletedAtUtc,
            SafeError = record.ErrorCode is not null ? new ErrorDto
            {
                Code = record.ErrorCode,
                RequestId = record.Id,
                Retryable = record.Retryable,
                SafeMessageKey = record.ErrorMessageKey ?? "error.unknown"
            } : null
        };
    }

    public async Task<OutcomeDto?> GetOutcomeAsync(
        Guid clientOperationId,
        string actorScope,
        CancellationToken cancellationToken = default)
    {
        var record = await _dbContext.CentralOperationRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ClientOperationId == clientOperationId, cancellationToken);

        if (record is null)
        {
            return null;
        }

        var isAuthorized = string.Equals(record.ActorScope, actorScope, StringComparison.Ordinal);

        if (!isAuthorized)
        {
            return null;
        }

        return MapToOutcome(record);
    }

    public async Task<OutcomeDto?> GetLicensingOutcomeAsync(
        Guid clientOperationId,
        string? shopScope,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(shopScope, out var shopId) || shopId == Guid.Empty)
        {
            return null;
        }

        var record = await _dbContext.CentralOperationRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ClientOperationId == clientOperationId, cancellationToken);

        if (record is null)
        {
            return null;
        }

        if (record.ShopId != shopId)
        {
            return null;
        }

        return MapToOutcome(record);
    }

    private static OutcomeDto MapToOutcome(CentralOperationRecord record)
    {
        return new OutcomeDto
        {
            ClientOperationId = record.ClientOperationId,
            OperationType = record.OperationType,
            PayloadSha256 = record.PayloadSha256,
            OutcomeId = record.OutcomeId,
            ResultReference = record.ResultReference,
            State = record.State.ToString(),
            ResourceVersion = record.ResourceVersion,
            AuthorityRevision = record.AuthorityRevision,
            SafeError = record.ErrorCode is not null ? new ErrorDto
            {
                Code = record.ErrorCode,
                RequestId = record.Id,
                Retryable = record.Retryable,
                CurrentVersion = record.ResourceVersion,
                OutcomeReference = record.ResultReference,
                SafeMessageKey = record.ErrorMessageKey ?? "error.unknown"
            } : null
        };
    }
}
