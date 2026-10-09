using System.Text.Json;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Application.Features.Finance;

public sealed record SupplierOpeningBalanceCommand(
    Guid SupplierId,
    SupplierAccountDirection Direction,
    decimal Amount,
    DateTimeOffset EffectiveAt,
    string Reason,
    Guid ActorId,
    Guid ClientOperationId,
    string? CutoverReference = null);

public sealed record SupplierOpeningBalanceResult(Guid EntryId, bool WasExisting);

public sealed class SupplierOpeningBalanceHandler(
    ISupplierAccountRepository accounts,
    IPartyRepository parties,
    IApplicationPermissionAuthorizer authorization,
    IOperationLock operationLock,
    IResourceLock resourceLock,
    IDocumentNumberService numbers,
    IClock clock,
    IBusinessAuditWriter audit,
    ITransactionRunner transactions,
    IUnitOfWork unitOfWork,
    IOperationOutcomeLedger outcomes)
{
    private const string SourceType = "SupplierOpeningBalance";

    public Task<Result<SupplierOpeningBalanceResult>> HandleAsync(
        SupplierOpeningBalanceCommand command,
        CancellationToken cancellationToken)
    {
        var amount = MoneyRoundingPolicy.Round(command.Amount);
        if (command.SupplierId == Guid.Empty || command.ActorId == Guid.Empty || command.ClientOperationId == Guid.Empty ||
            !Enum.IsDefined(command.Direction) || amount <= 0m || command.EffectiveAt == default ||
            string.IsNullOrWhiteSpace(command.Reason))
        {
            return Task.FromResult(Result<SupplierOpeningBalanceResult>.Failure(
                "supplier.opening_invalid", "Opening balance requires supplier, actor, operation, explicit direction, positive money, effective context and reason."));
        }
        var metadata = JsonSerializer.Serialize(new
        {
            Reason = command.Reason.Trim(), CutoverReference = command.CutoverReference?.Trim(), command.EffectiveAt
        });
        if (metadata.Length > 1000)
        {
            return Task.FromResult(Result<SupplierOpeningBalanceResult>.Failure(
                "supplier.opening_context_too_long", "Opening reason and provenance exceed the supported context length."));
        }
        var fingerprint = OperationPayloadFingerprint.ComputeSha256(JsonSerializer.Serialize(new
        {
            command.SupplierId, command.ActorId, command.Direction, Amount = amount, Context = metadata
        }));

        return transactions.ExecuteAsync(async ct =>
        {
            var allowed = await authorization.AuthorizeAsync(command.ActorId, PermissionKeys.SupplierAccountAdjust, ct);
            if (!allowed.IsSuccess)
            {
                return Result<SupplierOpeningBalanceResult>.Failure(allowed.Error!.Code, allowed.Error.Message);
            }
            await operationLock.AcquireAsync(command.ClientOperationId, ct);
            await resourceLock.AcquireAsync("supplier-account", command.SupplierId, ct);
            var prior = await outcomes.GetOutcomeAsync(command.ClientOperationId, ct);
            if (prior is not null && (prior.OperationType != SourceType || prior.PayloadFingerprint != fingerprint))
            {
                return Result<SupplierOpeningBalanceResult>.Failure(
                    "idempotency.payload_mismatch", "Opening operation was previously submitted with different authority or parameters.");
            }
            var hasSource = await accounts.HasSourceEntryAsync(
                SupplierAccountEntryType.OpeningBalance, SourceType, command.ClientOperationId, ct);
            if (hasSource)
            {
                if (prior?.State != OperationOutcomeState.Succeeded || prior.EntityId is not { } entryId || !prior.WasCommitted)
                {
                    return Result<SupplierOpeningBalanceResult>.Failure(
                        "supplier.opening_replay_requires_review", "Opening source exists without proven replay authority; review is required.");
                }
                return Result<SupplierOpeningBalanceResult>.Success(new(entryId, true));
            }
            if (prior?.State == OperationOutcomeState.Succeeded)
            {
                return Result<SupplierOpeningBalanceResult>.Failure(
                    "supplier.opening_source_missing", "Committed opening replay authority has no canonical source entry.");
            }
            if (await parties.GetSupplierForUpdateAsync(command.SupplierId, ct) is null)
            {
                return Result<SupplierOpeningBalanceResult>.Failure("supplier.not_found", "Supplier was not found.");
            }

            var entry = new SupplierAccountEntry
            {
                EntryNumber = await numbers.NextAsync("SAE", ct), SupplierId = command.SupplierId,
                EntryType = SupplierAccountEntryType.OpeningBalance, Direction = command.Direction, Amount = amount,
                ReferenceType = SourceType, ReferenceId = command.ClientOperationId,
                OccurredAt = command.EffectiveAt.ToUniversalTime(), CreatedAt = clock.UtcNow,
                ActorId = command.ActorId, ClientOperationId = command.ClientOperationId, Note = metadata
            };
            entry.ValidateDirection();
            accounts.AddEntry(entry);
            audit.Record("SUPPLIER_OPENING_BALANCE_POSTED", "SUPPLIER_ACCOUNT_ENTRY", entry.Id,
                command.ActorId, command.ClientOperationId, metadata);
            await outcomes.RecordSuccessAsync(command.ClientOperationId, SourceType, entry.Id, entry.EntryNumber,
                actorId: command.ActorId, payloadFingerprint: fingerprint, cancellationToken: ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result<SupplierOpeningBalanceResult>.Success(new(entry.Id, false));
        }, cancellationToken);
    }
}
