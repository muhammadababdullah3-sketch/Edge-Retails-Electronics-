using EdgeRetails.Application.Exceptions;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class EfOperationOutcomeLedger : IOperationOutcomeLedger
{
    private readonly EdgeRetailsDbContext _db;

    public EfOperationOutcomeLedger(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public async Task<OperationOutcomeRecord?> GetOutcomeAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _db.OperationOutcomes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        return MapToRecord(entity);
    }

    public async Task<OperationClaimResult> ClaimAsync(
        Guid clientOperationId,
        string operationType,
        string payloadFingerprint,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(payloadFingerprint))
        {
            throw new ArgumentException("Payload fingerprint is required for operation claims.", nameof(payloadFingerprint));
        }

        if (_db.Database.IsRelational())
        {
            var newId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;

            // Atomic PostgreSQL claim via INSERT ON CONFLICT DO NOTHING
            var rowsAffected = await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO system.operation_outcomes
                   (id, client_operation_id, operation_type, payload_fingerprint, status, actor_user_id, terminal_id, session_id, was_committed, created_at, updated_at)
                   VALUES ({newId}, {clientOperationId}, {operationType}, {payloadFingerprint}, 'Pending', {actorId}, {terminalId}, {sessionId}, false, {now}, {now})
                   ON CONFLICT (client_operation_id) DO NOTHING;",
                cancellationToken);

            if (rowsAffected > 0)
            {
                var outcome = new OperationOutcome
                {
                    Id = newId,
                    ClientOperationId = clientOperationId,
                    OperationType = operationType,
                    PayloadFingerprint = payloadFingerprint,
                    Status = OperationOutcomeStatus.Pending,
                    ActorUserId = actorId,
                    TerminalId = terminalId,
                    SessionId = sessionId,
                    WasCommitted = false,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                return OperationClaimResult.Acquired(MapToRecord(outcome));
            }

            // Conflict detected: fetch existing record to inspect state
            var existing = await _db.OperationOutcomes
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);

            if (existing is null)
            {
                throw new InvalidOperationException($"Operation claim conflict occurred for ID '{clientOperationId}' but existing record could not be retrieved.");
            }

            // Area 12C: Fail-closed operation-type binding enforcement
            if (!string.IsNullOrEmpty(existing.OperationType) &&
                !string.Equals(existing.OperationType, operationType, StringComparison.Ordinal))
            {
                throw new IdempotencyOperationTypeMismatchException(clientOperationId, existing.OperationType, operationType);
            }

            // Area 12A / MON-P12W1-006: Fail-closed actor context mismatch & supplied-null enforcement
            if (existing.ActorUserId.HasValue && (!actorId.HasValue || existing.ActorUserId.Value != actorId.Value))
            {
                throw new IdempotencyContextMismatchException(clientOperationId, "ActorUserId", existing.ActorUserId.Value.ToString(), actorId?.ToString() ?? "null");
            }

            // Area 12A / MON-P12W1-006: Fail-closed terminal context mismatch & supplied-null enforcement
            if (existing.TerminalId.HasValue && (!terminalId.HasValue || existing.TerminalId.Value != terminalId.Value))
            {
                throw new IdempotencyContextMismatchException(clientOperationId, "TerminalId", existing.TerminalId.Value.ToString(), terminalId?.ToString() ?? "null");
            }

            // Area 12C: Fail-closed tamper detection
            if (!string.IsNullOrEmpty(existing.PayloadFingerprint) &&
                !string.Equals(existing.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal))
            {
                throw new IdempotencyPayloadMismatchException(clientOperationId, existing.PayloadFingerprint, payloadFingerprint);
            }

            return existing.Status switch
            {
                OperationOutcomeStatus.Pending => OperationClaimResult.InFlight(MapToRecord(existing)),
                OperationOutcomeStatus.Succeeded => OperationClaimResult.AlreadySucceeded(MapToRecord(existing)),
                OperationOutcomeStatus.Failed => OperationClaimResult.AlreadyFailed(MapToRecord(existing), existing.ErrorCode, existing.ErrorMessage),
                _ => OperationClaimResult.InFlight(MapToRecord(existing))
            };
        }

        // Fallback for non-relational providers (e.g., InMemory)
        var nonRelationalExisting = await _db.OperationOutcomes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);

        if (nonRelationalExisting is not null)
        {
            if (!string.IsNullOrEmpty(nonRelationalExisting.OperationType) &&
                !string.Equals(nonRelationalExisting.OperationType, operationType, StringComparison.Ordinal))
            {
                throw new IdempotencyOperationTypeMismatchException(clientOperationId, nonRelationalExisting.OperationType, operationType);
            }

            if (nonRelationalExisting.ActorUserId.HasValue && (!actorId.HasValue || nonRelationalExisting.ActorUserId.Value != actorId.Value))
            {
                throw new IdempotencyContextMismatchException(clientOperationId, "ActorUserId", nonRelationalExisting.ActorUserId.Value.ToString(), actorId?.ToString() ?? "null");
            }

            if (nonRelationalExisting.TerminalId.HasValue && (!terminalId.HasValue || nonRelationalExisting.TerminalId.Value != terminalId.Value))
            {
                throw new IdempotencyContextMismatchException(clientOperationId, "TerminalId", nonRelationalExisting.TerminalId.Value.ToString(), terminalId?.ToString() ?? "null");
            }

            if (!string.IsNullOrEmpty(nonRelationalExisting.PayloadFingerprint) &&
                !string.Equals(nonRelationalExisting.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal))
            {
                throw new IdempotencyPayloadMismatchException(clientOperationId, nonRelationalExisting.PayloadFingerprint, payloadFingerprint);
            }

            return nonRelationalExisting.Status switch
            {
                OperationOutcomeStatus.Pending => OperationClaimResult.InFlight(MapToRecord(nonRelationalExisting)),
                OperationOutcomeStatus.Succeeded => OperationClaimResult.AlreadySucceeded(MapToRecord(nonRelationalExisting)),
                OperationOutcomeStatus.Failed => OperationClaimResult.AlreadyFailed(MapToRecord(nonRelationalExisting), nonRelationalExisting.ErrorCode, nonRelationalExisting.ErrorMessage),
                _ => OperationClaimResult.InFlight(MapToRecord(nonRelationalExisting))
            };
        }

        var newOutcome = new OperationOutcome
        {
            Id = Guid.CreateVersion7(),
            ClientOperationId = clientOperationId,
            OperationType = operationType,
            PayloadFingerprint = payloadFingerprint,
            Status = OperationOutcomeStatus.Pending,
            ActorUserId = actorId,
            TerminalId = terminalId,
            SessionId = sessionId,
            WasCommitted = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.OperationOutcomes.Add(newOutcome);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return OperationClaimResult.Acquired(MapToRecord(newOutcome));
        }
        catch (DbUpdateException)
        {
            _db.Entry(newOutcome).State = EntityState.Detached;
            var current = await _db.OperationOutcomes
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);

            if (current is not null)
            {
                if (!string.IsNullOrEmpty(current.OperationType) &&
                    !string.Equals(current.OperationType, operationType, StringComparison.Ordinal))
                {
                    throw new IdempotencyOperationTypeMismatchException(clientOperationId, current.OperationType, operationType);
                }

                if (!string.IsNullOrEmpty(current.PayloadFingerprint) &&
                    !string.Equals(current.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal))
                {
                    throw new IdempotencyPayloadMismatchException(clientOperationId, current.PayloadFingerprint, payloadFingerprint);
                }

                if (current.ActorUserId.HasValue && (!actorId.HasValue || current.ActorUserId.Value != actorId.Value))
                {
                    throw new IdempotencyContextMismatchException(clientOperationId, "ActorUserId", current.ActorUserId.Value.ToString(), actorId?.ToString() ?? "null");
                }

                if (current.TerminalId.HasValue && (!terminalId.HasValue || current.TerminalId.Value != terminalId.Value))
                {
                    throw new IdempotencyContextMismatchException(clientOperationId, "TerminalId", current.TerminalId.Value.ToString(), terminalId?.ToString() ?? "null");
                }

                return current.Status switch
                {
                    OperationOutcomeStatus.Pending => OperationClaimResult.InFlight(MapToRecord(current)),
                    OperationOutcomeStatus.Succeeded => OperationClaimResult.AlreadySucceeded(MapToRecord(current)),
                    OperationOutcomeStatus.Failed => OperationClaimResult.AlreadyFailed(MapToRecord(current), current.ErrorCode, current.ErrorMessage),
                    _ => OperationClaimResult.InFlight(MapToRecord(current))
                };
            }
            throw;
        }
    }

    private static void ValidateIdentityImmutability(
        Guid clientOperationId,
        OperationOutcome existing,
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

        if (existing.ActorUserId.HasValue && actorId.HasValue && existing.ActorUserId.Value != actorId.Value)
        {
            throw new IdempotencyContextMismatchException(clientOperationId, "ActorUserId", existing.ActorUserId.Value.ToString(), actorId.Value.ToString());
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

    public async Task RecordPendingAsync(
        Guid clientOperationId,
        string operationType,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _db.OperationOutcomes
            .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);

        if (existing is null)
        {
            var outcome = new OperationOutcome
            {
                Id = Guid.CreateVersion7(),
                ClientOperationId = clientOperationId,
                OperationType = operationType,
                PayloadFingerprint = payloadFingerprint,
                Status = OperationOutcomeStatus.Pending,
                ActorUserId = actorId,
                TerminalId = terminalId,
                SessionId = sessionId,
                WasCommitted = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _db.OperationOutcomes.Add(outcome);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException)
            {
                _db.Entry(outcome).State = EntityState.Detached;
                existing = await _db.OperationOutcomes
                    .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);
                if (existing is null)
                {
                    throw;
                }
            }
        }

        ValidateIdentityImmutability(clientOperationId, existing, operationType, payloadFingerprint, actorId, terminalId);

        if (existing.Status == OperationOutcomeStatus.Succeeded)
        {
            return;
        }

        if (_db.Database.IsRelational())
        {
            var now = DateTimeOffset.UtcNow;
            await _db.OperationOutcomes
                .Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, OperationOutcomeStatus.Pending)
                    .SetProperty(x => x.WasCommitted, false)
                    .SetProperty(x => x.ResultEntityType, (string?)null)
                    .SetProperty(x => x.ResultEntityId, (Guid?)null)
                    .SetProperty(x => x.DocumentNumber, (string?)null)
                    .SetProperty(x => x.ErrorCode, (string?)null)
                    .SetProperty(x => x.ErrorMessage, (string?)null)
                    .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.TerminalId, x => x.TerminalId ?? terminalId)
                    .SetProperty(x => x.SessionId, x => sessionId ?? x.SessionId)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            return;
        }

        existing.Status = OperationOutcomeStatus.Pending;
        existing.WasCommitted = false;
        if (!existing.TerminalId.HasValue && terminalId.HasValue)
        {
            existing.TerminalId = terminalId;
        }
        if (sessionId.HasValue)
        {
            existing.SessionId = sessionId;
        }
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
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

    public async Task RecordSuccessWithResponseAsync(
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
        var existing = await _db.OperationOutcomes
            .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);

        if (existing is null)
        {
            var outcome = new OperationOutcome
            {
                Id = Guid.CreateVersion7(),
                ClientOperationId = clientOperationId,
                OperationType = operationType,
                PayloadFingerprint = payloadFingerprint,
                Status = OperationOutcomeStatus.Succeeded,
                ResultEntityType = operationType,
                ResultEntityId = entityId,
                DocumentNumber = documentNumber,
                ResponsePayload = responsePayload,
                ResponseStatusCode = responseStatusCode,
                ResponseContentType = responseContentType,
                ActorUserId = actorId,
                TerminalId = terminalId,
                SessionId = sessionId,
                WasCommitted = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            };

            _db.OperationOutcomes.Add(outcome);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException)
            {
                _db.Entry(outcome).State = EntityState.Detached;
                existing = await _db.OperationOutcomes
                    .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);
                if (existing is null)
                {
                    throw;
                }
            }
        }

        ValidateIdentityImmutability(clientOperationId, existing, operationType, payloadFingerprint, actorId, terminalId);

        if (existing.Status == OperationOutcomeStatus.Succeeded)
        {
            return;
        }

        if (_db.Database.IsRelational())
        {
            var now = DateTimeOffset.UtcNow;
            var updatedRows = await _db.OperationOutcomes
                .Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, OperationOutcomeStatus.Succeeded)
                    .SetProperty(x => x.ResultEntityType, operationType)
                    .SetProperty(x => x.ResultEntityId, entityId)
                    .SetProperty(x => x.DocumentNumber, documentNumber)
                    .SetProperty(x => x.WasCommitted, true)
                    .SetProperty(x => x.ErrorCode, (string?)null)
                    .SetProperty(x => x.ErrorMessage, (string?)null)
                    .SetProperty(x => x.ResponsePayload, responsePayload)
                    .SetProperty(x => x.ResponseStatusCode, responseStatusCode)
                    .SetProperty(x => x.ResponseContentType, responseContentType)
                    .SetProperty(x => x.TerminalId, x => x.TerminalId ?? terminalId)
                    .SetProperty(x => x.SessionId, x => sessionId ?? x.SessionId)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.CompletedAt, (DateTimeOffset?)now), cancellationToken);

            if (updatedRows > 0)
            {
                existing.Status = OperationOutcomeStatus.Succeeded;
                existing.ResultEntityType = operationType;
                existing.ResultEntityId = entityId;
                existing.DocumentNumber = documentNumber;
                existing.WasCommitted = true;
                existing.ErrorCode = null;
                existing.ErrorMessage = null;
                existing.ResponsePayload = responsePayload;
                existing.ResponseStatusCode = responseStatusCode;
                existing.ResponseContentType = responseContentType;
                existing.UpdatedAt = now;
                existing.CompletedAt = now;
                return;
            }
        }

        existing.Status = OperationOutcomeStatus.Succeeded;
        existing.ResultEntityType = operationType;
        existing.ResultEntityId = entityId;
        existing.DocumentNumber = documentNumber;
        existing.WasCommitted = true;
        existing.ResponsePayload = responsePayload;
        existing.ResponseStatusCode = responseStatusCode;
        existing.ResponseContentType = responseContentType;
        if (!existing.TerminalId.HasValue && terminalId.HasValue)
        {
            existing.TerminalId = terminalId;
        }
        if (sessionId.HasValue)
        {
            existing.SessionId = sessionId;
        }
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.CompletedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
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

    public async Task RecordFailureWithResponseAsync(
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
        var existing = await _db.OperationOutcomes
            .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);

        if (existing is null)
        {
            var outcome = new OperationOutcome
            {
                Id = Guid.CreateVersion7(),
                ClientOperationId = clientOperationId,
                OperationType = operationType,
                PayloadFingerprint = payloadFingerprint,
                Status = OperationOutcomeStatus.Failed,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                ResponsePayload = responsePayload,
                ResponseStatusCode = responseStatusCode,
                ResponseContentType = responseContentType,
                ActorUserId = actorId,
                TerminalId = terminalId,
                SessionId = sessionId,
                WasCommitted = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            };

            _db.OperationOutcomes.Add(outcome);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException)
            {
                _db.Entry(outcome).State = EntityState.Detached;
                existing = await _db.OperationOutcomes
                    .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);
                if (existing is null)
                {
                    throw;
                }
            }
        }

        ValidateIdentityImmutability(clientOperationId, existing, operationType, payloadFingerprint, actorId, terminalId);

        if (existing.Status == OperationOutcomeStatus.Succeeded)
        {
            return;
        }

        if (_db.Database.IsRelational())
        {
            var now = DateTimeOffset.UtcNow;
            var updatedRows = await _db.OperationOutcomes
                .Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, OperationOutcomeStatus.Failed)
                    .SetProperty(x => x.WasCommitted, false)
                    .SetProperty(x => x.ResultEntityType, (string?)null)
                    .SetProperty(x => x.ResultEntityId, (Guid?)null)
                    .SetProperty(x => x.DocumentNumber, (string?)null)
                    .SetProperty(x => x.ErrorCode, errorCode)
                    .SetProperty(x => x.ErrorMessage, errorMessage)
                    .SetProperty(x => x.ResponsePayload, responsePayload)
                    .SetProperty(x => x.ResponseStatusCode, responseStatusCode)
                    .SetProperty(x => x.ResponseContentType, responseContentType)
                    .SetProperty(x => x.TerminalId, x => x.TerminalId ?? terminalId)
                    .SetProperty(x => x.SessionId, x => sessionId ?? x.SessionId)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.CompletedAt, (DateTimeOffset?)now), cancellationToken);

            if (updatedRows > 0)
            {
                existing.Status = OperationOutcomeStatus.Failed;
                existing.ErrorCode = errorCode;
                existing.ErrorMessage = errorMessage;
                existing.WasCommitted = false;
                existing.ResponsePayload = responsePayload;
                existing.ResponseStatusCode = responseStatusCode;
                existing.ResponseContentType = responseContentType;
                existing.UpdatedAt = now;
                existing.CompletedAt = now;
                return;
            }
        }

        existing.Status = OperationOutcomeStatus.Failed;
        existing.ErrorCode = errorCode;
        existing.ErrorMessage = errorMessage;
        existing.WasCommitted = false;
        existing.ResponsePayload = responsePayload;
        existing.ResponseStatusCode = responseStatusCode;
        existing.ResponseContentType = responseContentType;
        if (!existing.TerminalId.HasValue && terminalId.HasValue)
        {
            existing.TerminalId = terminalId;
        }
        if (sessionId.HasValue)
        {
            existing.SessionId = sessionId;
        }
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        existing.CompletedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordOutcomeUnknownAsync(
        Guid clientOperationId,
        string operationType,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _db.OperationOutcomes
            .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);

        if (existing is null)
        {
            var outcome = new OperationOutcome
            {
                Id = Guid.CreateVersion7(),
                ClientOperationId = clientOperationId,
                OperationType = operationType,
                PayloadFingerprint = payloadFingerprint,
                Status = OperationOutcomeStatus.OutcomeUnknown,
                ActorUserId = actorId,
                TerminalId = terminalId,
                SessionId = sessionId,
                WasCommitted = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            _db.OperationOutcomes.Add(outcome);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException)
            {
                _db.Entry(outcome).State = EntityState.Detached;
                existing = await _db.OperationOutcomes
                    .FirstOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);
                if (existing is null)
                {
                    throw;
                }
            }
        }

        ValidateIdentityImmutability(clientOperationId, existing, operationType, payloadFingerprint, actorId, terminalId);

        if (existing.Status == OperationOutcomeStatus.Succeeded)
        {
            return;
        }

        if (_db.Database.IsRelational())
        {
            var now = DateTimeOffset.UtcNow;
            await _db.OperationOutcomes
                .Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, OperationOutcomeStatus.OutcomeUnknown)
                    .SetProperty(x => x.WasCommitted, false)
                    .SetProperty(x => x.ResultEntityType, (string?)null)
                    .SetProperty(x => x.ResultEntityId, (Guid?)null)
                    .SetProperty(x => x.DocumentNumber, (string?)null)
                    .SetProperty(x => x.ErrorCode, (string?)null)
                    .SetProperty(x => x.ErrorMessage, (string?)null)
                    .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.TerminalId, x => x.TerminalId ?? terminalId)
                    .SetProperty(x => x.SessionId, x => sessionId ?? x.SessionId)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            return;
        }

        existing.Status = OperationOutcomeStatus.OutcomeUnknown;
        existing.WasCommitted = false;
        if (!existing.TerminalId.HasValue && terminalId.HasValue)
        {
            existing.TerminalId = terminalId;
        }
        if (sessionId.HasValue)
        {
            existing.SessionId = sessionId;
        }
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static OperationOutcomeRecord MapToRecord(OperationOutcome entity)
    {
        return new OperationOutcomeRecord
        {
            ClientOperationId = entity.ClientOperationId,
            State = ToRecordState(entity.Status),
            OperationType = entity.OperationType,
            EntityId = entity.ResultEntityId,
            DocumentNumber = entity.DocumentNumber,
            WasCommitted = entity.WasCommitted,
            PayloadFingerprint = entity.PayloadFingerprint,
            ActorId = entity.ActorUserId,
            TerminalId = entity.TerminalId,
            SessionId = entity.SessionId,
            ErrorCode = entity.ErrorCode,
            ErrorMessage = entity.ErrorMessage,
            ResponsePayload = entity.ResponsePayload,
            ResponseStatusCode = entity.ResponseStatusCode,
            ResponseContentType = entity.ResponseContentType,
            CreatedAt = entity.CreatedAt,
            CompletedAt = entity.CompletedAt
        };
    }

    private static OperationOutcomeState ToRecordState(OperationOutcomeStatus status) => status switch
    {
        OperationOutcomeStatus.Pending => OperationOutcomeState.Pending,
        OperationOutcomeStatus.Succeeded => OperationOutcomeState.Succeeded,
        OperationOutcomeStatus.Failed => OperationOutcomeState.Failed,
        OperationOutcomeStatus.OutcomeUnknown => OperationOutcomeState.OutcomeUnknown,
        _ => OperationOutcomeState.NotFound
    };
}
