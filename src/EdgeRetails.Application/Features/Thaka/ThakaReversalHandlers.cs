using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Thaka;

namespace EdgeRetails.Application.Features.Thaka;

public sealed record ReverseThakaMaterialCommand(
    Guid ClientOperationId,
    Guid ProjectId,
    Guid MaterialIssueId,
    string Reason,
    Guid ActorId);

public sealed record ReverseThakaMaterialResult(
    Guid ReversalId,
    string ReversalNumber,
    decimal ReversedCharge,
    decimal RestoredCost,
    bool WasExisting);

public sealed class ReverseThakaMaterialHandler
{
    private readonly IThakaRepository _thaka;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costs;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork; public ReverseThakaMaterialHandler(
        IThakaRepository thaka,
        IInventoryRepository inventory,
        IInventoryCostAllocator costs,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IDocumentNumberService numbers,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _thaka = thaka;
        _inventory = inventory;
        _costs = costs;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _numbers = numbers;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<ReverseThakaMaterialResult>> HandleAsync(
        ReverseThakaMaterialCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Reason))
        {
            return Task.FromResult(Result<ReverseThakaMaterialResult>.Failure(
                "thaka.material_reversal_invalid",
                "Material reversal requires operation id and reason."));
        }
        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ThakaManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<ReverseThakaMaterialResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var byOperation = await _thaka.GetMaterialReversalByOperationIdAsync(
                command.ClientOperationId,
                ct);
            if (byOperation is not null)
            {
                return Result<ReverseThakaMaterialResult>.Success(new(
                    byOperation.Id,
                    byOperation.ReversalNumber,
                    byOperation.ReversedCharge,
                    byOperation.RestoredCost,
                    true));
            }

            await _resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await _thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null || project.Status != ThakaProjectStatus.Active)
            {
                return Result<ReverseThakaMaterialResult>.Failure(
                    "thaka.project_not_active",
                    "Settled Thaka must be reopened before material reversal.");
            }

            var issue = await _thaka.GetIssueForUpdateAsync(command.MaterialIssueId, ct);
            if (issue is null || issue.ProjectId != project.Id)
            {
                return Result<ReverseThakaMaterialResult>.Failure(
                    "thaka.issue_not_found",
                    "Material issue was not found for this project.");
            }

            if (await _thaka.GetMaterialReversalByIssueAsync(issue.Id, ct) is not null)
            {
                return Result<ReverseThakaMaterialResult>.Failure(
                    "thaka.issue_already_reversed",
                    "This material issue has already been reversed.");
            }
            var items = await _thaka.GetIssueItemsAsync(issue.Id, ct);
            foreach (var productId in items.Select(x => x.ProductId).Distinct().OrderBy(x => x))
            {
                await _resourceLock.AcquireAsync("product", productId, ct);
            }

            foreach (var item in items)
            {
                var stock = await _inventory.GetStockBalanceForUpdateAsync(item.ProductId, ct)
                    ?? throw new BusinessRuleException(
                        "thaka.stock_missing",
                        "Stock balance was not found during material reversal.");

                var before = stock.SellableQty;
                var movement = new InventoryMovement
                {
                    ProductId = item.ProductId,
                    MovementType = InventoryMovementType.ThakaReturn,
                    ReferenceType = "THAKA_MATERIAL_REVERSAL",
                    ActorId = command.ActorId,
                    OccurredAt = _clock.UtcNow,
                    CorrelationId = command.ClientOperationId,
                    UnitCostSnapshot = item.UnitCostSnapshot,
                    Reason = command.Reason.Trim()
                };
                _inventory.AddMovement(movement);

                stock.ApplyDelta(InventoryBucket.Sellable, item.BaseQuantity);
                _inventory.AddMovementEffect(new InventoryMovementEffect
                {
                    MovementId = movement.Id,
                    StockBucket = InventoryBucket.Sellable,
                    QuantityDelta = item.BaseQuantity,
                    QuantityBefore = before,
                    QuantityAfter = stock.SellableQty
                });

                var issuedUnits = await _thaka.GetIssueUnitsAsync(item.Id, ct);
                if (issuedUnits.Count > 0)
                {
                    await RestoreSerializedAsync(item, issuedUnits, movement, ct);
                }
                else
                {
                    await RestoreQuantityAsync(item, movement, ct);
                }
            }
            var reversal = new ThakaMaterialReversal
            {
                ProjectId = project.Id,
                MaterialIssueId = issue.Id,
                ReversalNumber = await _numbers.NextAsync("THK-MREV", ct),
                ClientOperationId = command.ClientOperationId,
                ReversedCharge = issue.TotalCharge,
                RestoredCost = issue.TotalCost,
                Reason = command.Reason.Trim(),
                ReversedBy = command.ActorId,
                ReversedAt = _clock.UtcNow
            };
            _thaka.AddMaterialReversal(reversal);

            _audit.Record(
                "THAKA_MATERIAL_REVERSED",
                "THAKA_MATERIAL_REVERSAL",
                reversal.Id,
                command.ActorId,
                command.ClientOperationId,
                reversal.ReversalNumber);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<ReverseThakaMaterialResult>.Success(new(
                reversal.Id,
                reversal.ReversalNumber,
                reversal.ReversedCharge,
                reversal.RestoredCost,
                false));
        }, cancellationToken);
    }

    private async Task RestoreQuantityAsync(
        ThakaMaterialIssueItem item,
        InventoryMovement reversalMovement,
        CancellationToken cancellationToken)
    {
        var consumptions = await _inventory.GetMovementLotConsumptionsAsync(
            item.InventoryMovementId,
            cancellationToken);
        if (QuantityMath.RoundQuantity(consumptions.Sum(x => x.Quantity)) !=
            QuantityMath.RoundQuantity(item.BaseQuantity))
        {
            throw new BusinessRuleException(
                "thaka.reversal_consumption_mismatch",
                "Original material lot-consumption history is incomplete.");
        }
        foreach (var consumption in consumptions)
        {
            var originLot = await _inventory.GetInventoryLotForUpdateAsync(
                consumption.LotId,
                cancellationToken)
                ?? throw new BusinessRuleException(
                    "thaka.reversal_origin_lot_missing",
                    "Original material inventory lot was not found.");

            await _costs.AddCarryingValueAndLotWithIdAsync(
                item.ProductId,
                consumption.Quantity,
                consumption.UnitCostSnapshot,
                reversalMovement.Id,
                originLot.PurchaseItemId,
                InventoryBucket.Sellable,
                cancellationToken);
        }
    }

    private async Task RestoreSerializedAsync(
        ThakaMaterialIssueItem item,
        IReadOnlyList<ThakaMaterialIssueUnit> snapshots,
        InventoryMovement reversalMovement,
        CancellationToken cancellationToken)
    {
        var ids = snapshots.Select(x => x.InventoryUnitId).ToArray();
        var units = await _inventory.GetInventoryUnitsForUpdateAsync(
            item.ProductId,
            ids,
            cancellationToken);

        if (units.Count != ids.Length ||
            units.Any(x => x.Status != InventoryUnitStatus.IssuedThaka))
        {
            throw new BusinessRuleException(
                "thaka.reversal_serial_not_eligible",
                "One or more serialized units are not eligible for Thaka reversal.");
        }

        var snapshotByUnit = snapshots.ToDictionary(x => x.InventoryUnitId);
        foreach (var unit in units.OrderBy(x => x.Id))
        {
            var snapshot = snapshotByUnit[unit.Id];
            var lotId = await _costs.AddCarryingValueAndLotWithIdAsync(
                item.ProductId,
                1m,
                snapshot.UnitCostSnapshot,
                reversalMovement.Id,
                unit.SourcePurchaseItemId,
                InventoryBucket.Sellable,
                cancellationToken); var from = unit.Status;
            unit.Status = InventoryUnitStatus.InStock;
            unit.InventoryLotId = lotId;
            unit.Version++;

            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = reversalMovement.Id,
                InventoryUnitId = unit.Id,
                FromStatus = from,
                ToStatus = unit.Status
            });
        }
    }
}

public sealed record ReverseThakaPaymentCommand(
    Guid ClientOperationId,
    Guid ProjectId,
    Guid PaymentId,
    string Reason,
    Guid ActorId);

public sealed record ReverseThakaPaymentResult(
    Guid ReversalId,
    string ReversalNumber,
    decimal Amount,
    bool WasExisting);

public sealed class ReverseThakaPaymentHandler
{
    private readonly IThakaRepository _thaka;
    private readonly ICashMovementService _cashMovements;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork; public ReverseThakaPaymentHandler(
        IThakaRepository thaka,
        ICashMovementService cashMovements,
        IOperationLock operationLock,
        IResourceLock resourceLock,
        IDocumentNumberService numbers,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _thaka = thaka;
        _cashMovements = cashMovements;
        _operationLock = operationLock;
        _resourceLock = resourceLock;
        _numbers = numbers;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<ReverseThakaPaymentResult>> HandleAsync(
        ReverseThakaPaymentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Reason))
        {
            return Task.FromResult(Result<ReverseThakaPaymentResult>.Failure(
                "thaka.payment_reversal_invalid",
                "Payment reversal requires operation id and reason."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ThakaManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<ReverseThakaPaymentResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var byOperation = await _thaka.GetPaymentReversalByOperationIdAsync(
                command.ClientOperationId,
                ct); if (byOperation is not null)
            {
                return Result<ReverseThakaPaymentResult>.Success(new(
                    byOperation.Id,
                    byOperation.ReversalNumber,
                    byOperation.Amount,
                    true));
            }

            await _resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await _thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null || project.Status != ThakaProjectStatus.Active)
            {
                return Result<ReverseThakaPaymentResult>.Failure(
                    "thaka.project_not_active",
                    "Settled Thaka must be reopened before payment reversal.");
            }

            var payment = await _thaka.GetPaymentForUpdateAsync(command.PaymentId, ct);
            if (payment is null || payment.ProjectId != project.Id)
            {
                return Result<ReverseThakaPaymentResult>.Failure(
                    "thaka.payment_not_found",
                    "Payment was not found for this project.");
            }

            if (await _thaka.GetPaymentReversalByPaymentAsync(payment.Id, ct) is not null)
            {
                return Result<ReverseThakaPaymentResult>.Failure(
                    "thaka.payment_already_reversed",
                    "This payment has already been reversed.");
            }

            var reversal = new ThakaPaymentReversal
            {
                ProjectId = project.Id,
                PaymentId = payment.Id,
                ReversalNumber = await _numbers.NextAsync("THK-PREV", ct),
                ClientOperationId = command.ClientOperationId,
                Amount = payment.Amount,
                Reason = command.Reason.Trim(),
                ReversedBy = command.ActorId,
                ReversedAt = _clock.UtcNow
            };
            _thaka.AddPaymentReversal(reversal); if (payment.PaymentMethod == ThakaPaymentMethod.Cash)
            {
                var cash = await _cashMovements.RecordAsync(
                    new RecordCashMovementRequest(
                        CashMovementType.ThakaPaymentReversalCashOut,
                        CashMovementDirection.Out,
                        payment.Amount,
                        command.ActorId,
                        "THAKA_PAYMENT_REVERSAL",
                        reversal.Id,
                        command.Reason.Trim(),
                        payment.ReceiptNumber),
                    ct);
                if (!cash.IsSuccess)
                {
                    return Result<ReverseThakaPaymentResult>.Failure(
                        cash.Error!.Code,
                        cash.Error.Message);
                }
            }

            _audit.Record(
                "THAKA_PAYMENT_REVERSED",
                "THAKA_PAYMENT_REVERSAL",
                reversal.Id,
                command.ActorId,
                command.ClientOperationId,
                reversal.ReversalNumber);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<ReverseThakaPaymentResult>.Success(new(
                reversal.Id,
                reversal.ReversalNumber,
                reversal.Amount,
                false));
        }, cancellationToken);
    }
}
