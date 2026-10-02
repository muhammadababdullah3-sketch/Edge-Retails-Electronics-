using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Operations;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class EfOperationOutcomeLedger : IOperationOutcomeLedger
{
    private readonly EdgeRetailsDbContext _db;
    private static readonly SemaphoreSlim[] _stripedGates =
        Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public EfOperationOutcomeLedger(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    private static SemaphoreSlim GetGate(Guid clientOperationId)
    {
        var hash = (uint)clientOperationId.GetHashCode();
        return _stripedGates[hash % 64];
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

    public async Task RecordPendingAsync(
        Guid clientOperationId,
        string operationType,
        Guid? actorId = null,
        Guid? terminalId = null,
        Guid? sessionId = null,
        string? payloadFingerprint = null,
        CancellationToken cancellationToken = default)
    {
        var gate = GetGate(clientOperationId);
        await gate.WaitAsync(cancellationToken);
        try
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

            if (_db.Database.IsRelational())
            {
                var now = DateTimeOffset.UtcNow;
                await _db.OperationOutcomes
                    .Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.OperationType, operationType)
                        .SetProperty(x => x.Status, OperationOutcomeStatus.Pending)
                        .SetProperty(x => x.WasCommitted, false)
                        .SetProperty(x => x.ResultEntityType, (string?)null)
                        .SetProperty(x => x.ResultEntityId, (Guid?)null)
                        .SetProperty(x => x.DocumentNumber, (string?)null)
                        .SetProperty(x => x.ErrorCode, (string?)null)
                        .SetProperty(x => x.ErrorMessage, (string?)null)
                        .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.ActorUserId, x => actorId ?? x.ActorUserId)
                        .SetProperty(x => x.TerminalId, x => terminalId ?? x.TerminalId)
                        .SetProperty(x => x.SessionId, x => sessionId ?? x.SessionId)
                        .SetProperty(x => x.PayloadFingerprint, x => payloadFingerprint ?? x.PayloadFingerprint)
                        .SetProperty(x => x.UpdatedAt, now), cancellationToken);
                return;
            }

            if (existing.Status == OperationOutcomeStatus.Succeeded)
            {
                return;
            }

            existing.OperationType = operationType;
            existing.Status = OperationOutcomeStatus.Pending;
            existing.WasCommitted = false;
            if (actorId.HasValue)
            {
                existing.ActorUserId = actorId;
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
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RecordSuccessAsync(
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
        var gate = GetGate(clientOperationId);
        await gate.WaitAsync(cancellationToken);
        try
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

            if (_db.Database.IsRelational())
            {
                var now = DateTimeOffset.UtcNow;
                await _db.OperationOutcomes
                    .Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.OperationType, operationType)
                        .SetProperty(x => x.Status, OperationOutcomeStatus.Succeeded)
                        .SetProperty(x => x.ResultEntityType, operationType)
                        .SetProperty(x => x.ResultEntityId, entityId)
                        .SetProperty(x => x.DocumentNumber, documentNumber)
                        .SetProperty(x => x.WasCommitted, true)
                        .SetProperty(x => x.ErrorCode, (string?)null)
                        .SetProperty(x => x.ErrorMessage, (string?)null)
                        .SetProperty(x => x.ActorUserId, x => actorId ?? x.ActorUserId)
                        .SetProperty(x => x.TerminalId, x => terminalId ?? x.TerminalId)
                        .SetProperty(x => x.SessionId, x => sessionId ?? x.SessionId)
                        .SetProperty(x => x.PayloadFingerprint, x => payloadFingerprint ?? x.PayloadFingerprint)
                        .SetProperty(x => x.UpdatedAt, now)
                        .SetProperty(x => x.CompletedAt, (DateTimeOffset?)now), cancellationToken);
                return;
            }

            if (existing.Status == OperationOutcomeStatus.Succeeded)
            {
                return;
            }

            existing.OperationType = operationType;
            existing.Status = OperationOutcomeStatus.Succeeded;
            existing.ResultEntityType = operationType;
            existing.ResultEntityId = entityId;
            existing.DocumentNumber = documentNumber;
            existing.WasCommitted = true;
            if (actorId.HasValue)
            {
                existing.ActorUserId = actorId;
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
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.CompletedAt = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RecordFailureAsync(
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
        var gate = GetGate(clientOperationId);
        await gate.WaitAsync(cancellationToken);
        try
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

            if (_db.Database.IsRelational())
            {
                var now = DateTimeOffset.UtcNow;
                await _db.OperationOutcomes
                    .Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.OperationType, operationType)
                        .SetProperty(x => x.Status, OperationOutcomeStatus.Failed)
                        .SetProperty(x => x.WasCommitted, false)
                        .SetProperty(x => x.ResultEntityType, (string?)null)
                        .SetProperty(x => x.ResultEntityId, (Guid?)null)
                        .SetProperty(x => x.DocumentNumber, (string?)null)
                        .SetProperty(x => x.ErrorCode, errorCode)
                        .SetProperty(x => x.ErrorMessage, errorMessage)
                        .SetProperty(x => x.ActorUserId, x => actorId ?? x.ActorUserId)
                        .SetProperty(x => x.TerminalId, x => terminalId ?? x.TerminalId)
                        .SetProperty(x => x.SessionId, x => sessionId ?? x.SessionId)
                        .SetProperty(x => x.PayloadFingerprint, x => payloadFingerprint ?? x.PayloadFingerprint)
                        .SetProperty(x => x.UpdatedAt, now)
                        .SetProperty(x => x.CompletedAt, (DateTimeOffset?)now), cancellationToken);
                return;
            }

            if (existing.Status == OperationOutcomeStatus.Succeeded)
            {
                return;
            }

            existing.OperationType = operationType;
            existing.Status = OperationOutcomeStatus.Failed;
            existing.ErrorCode = errorCode;
            existing.ErrorMessage = errorMessage;
            existing.WasCommitted = false;
            if (actorId.HasValue)
            {
                existing.ActorUserId = actorId;
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
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            existing.CompletedAt = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
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
        var gate = GetGate(clientOperationId);
        await gate.WaitAsync(cancellationToken);
        try
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

            if (_db.Database.IsRelational())
            {
                var now = DateTimeOffset.UtcNow;
                await _db.OperationOutcomes
                    .Where(x => x.Id == existing.Id && x.Status != OperationOutcomeStatus.Succeeded)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.OperationType, operationType)
                        .SetProperty(x => x.Status, OperationOutcomeStatus.OutcomeUnknown)
                        .SetProperty(x => x.WasCommitted, false)
                        .SetProperty(x => x.ResultEntityType, (string?)null)
                        .SetProperty(x => x.ResultEntityId, (Guid?)null)
                        .SetProperty(x => x.DocumentNumber, (string?)null)
                        .SetProperty(x => x.ErrorCode, (string?)null)
                        .SetProperty(x => x.ErrorMessage, (string?)null)
                        .SetProperty(x => x.CompletedAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.ActorUserId, x => actorId ?? x.ActorUserId)
                        .SetProperty(x => x.TerminalId, x => terminalId ?? x.TerminalId)
                        .SetProperty(x => x.SessionId, x => sessionId ?? x.SessionId)
                        .SetProperty(x => x.PayloadFingerprint, x => payloadFingerprint ?? x.PayloadFingerprint)
                        .SetProperty(x => x.UpdatedAt, now), cancellationToken);
                return;
            }

            if (existing.Status == OperationOutcomeStatus.Succeeded)
            {
                return;
            }

            existing.OperationType = operationType;
            existing.Status = OperationOutcomeStatus.OutcomeUnknown;
            existing.WasCommitted = false;
            if (actorId.HasValue)
            {
                existing.ActorUserId = actorId;
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
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
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
