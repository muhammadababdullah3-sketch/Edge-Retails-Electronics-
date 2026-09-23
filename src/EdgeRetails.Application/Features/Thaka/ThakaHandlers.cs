using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Thaka;

namespace EdgeRetails.Application.Features.Thaka;

public sealed record CreateThakaProjectCommand(
    Guid CustomerId,
    string ProjectName,
    string? SiteAddress,
    string? Note,
    DateOnly StartedOn,
    Guid ActorId,
    Guid CorrelationId);

public sealed record IssueThakaMaterialLineInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal EnteredQuantity,
    decimal ExpectedUnitCharge,
    IReadOnlyList<Guid> InventoryUnitIds);

public sealed record IssueThakaMaterialCommand(
    Guid ClientOperationId,
    Guid ProjectId,
    Guid ActorId,
    string? Note,
    IReadOnlyList<IssueThakaMaterialLineInput> Lines); public sealed record IssueThakaMaterialResult(
    Guid MaterialIssueId,
    string ChallanNumber,
    decimal TotalCharge,
    decimal TotalCost,
    bool WasExisting);

internal sealed record PreparedThakaLine(
    IssueThakaMaterialLineInput Input,
    Product Product,
    ProductUnit ProductUnit,
    TransactionQuantitySnapshot Quantity,
    decimal UnitCharge,
    decimal LineCharge,
    IReadOnlyList<InventoryUnit> SerializedUnits);

public sealed class CreateThakaProjectHandler
{
    private readonly IThakaRepository _thaka;
    private readonly IPartyRepository _parties;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;

    public CreateThakaProjectHandler(
        IThakaRepository thaka,
        IPartyRepository parties,
        IDocumentNumberService numbers,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _thaka = thaka;
        _parties = parties;
        _numbers = numbers;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        CreateThakaProjectCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ProjectName))
        {
            return Task.FromResult(Result<Guid>.Failure(
                "thaka.project_name_required",
                "Thaka project name is required."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ThakaManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<Guid>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            var customer = await _parties.GetCustomerAsync(command.CustomerId, ct);
            if (customer is null || !customer.IsActive || customer.IsWalkIn)
            {
                return Result<Guid>.Failure(
                    "thaka.customer_invalid",
                    "Thaka requires an active named customer.");
            }

            var project = new ThakaProject
            {
                ProjectNumber = await _numbers.NextAsync("THAKA", ct),
                CustomerId = customer.Id,
                ProjectName = command.ProjectName.Trim(),
                SiteAddress = Normalize(command.SiteAddress),
                Note = Normalize(command.Note),
                StartedOn = command.StartedOn,
                CreatedBy = command.ActorId,
                CreatedAt = _clock.UtcNow
            };
            _thaka.AddProject(project); _audit.Record(
                "THAKA_PROJECT_CREATED",
                "THAKA_PROJECT",
                project.Id,
                command.ActorId,
                command.CorrelationId,
                project.ProjectNumber);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(project.Id);
        }, cancellationToken);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed class IssueThakaMaterialHandler
{
    private readonly IThakaRepository _thaka;
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costs;
    private readonly IOperationLock _operationLock;
    private readonly IResourceLock _resourceLock;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork; public IssueThakaMaterialHandler(
        IThakaRepository thaka,
        ICatalogRepository catalog,
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
        _catalog = catalog;
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

    public Task<Result<IssueThakaMaterialResult>> HandleAsync(
        IssueThakaMaterialCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.Lines.Count == 0)
        {
            return Task.FromResult(Result<IssueThakaMaterialResult>.Failure(
                "thaka.issue_invalid",
                "Material issue requires operation id and at least one item."));
        }
        if (command.Lines.GroupBy(x => new { x.ProductId, x.ProductUnitId })
            .Any(x => x.Count() > 1))
        {
            return Task.FromResult(Result<IssueThakaMaterialResult>.Failure(
                "thaka.issue_duplicate_line",
                "The same product and unit may appear only once."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ThakaManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<IssueThakaMaterialResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var existing = await _thaka.GetIssueByOperationIdAsync(
                command.ClientOperationId,
                ct);
            if (existing is not null)
            {
                return Result<IssueThakaMaterialResult>.Success(new(
                    existing.Id,
                    existing.ChallanNumber,
                    existing.TotalCharge,
                    existing.TotalCost,
                    true));
            }

            await _resourceLock.AcquireAsync("thaka-project", command.ProjectId, ct);
            var project = await _thaka.GetProjectForUpdateAsync(command.ProjectId, ct);
            if (project is null || project.Status != ThakaProjectStatus.Active)
            {
                return Result<IssueThakaMaterialResult>.Failure(
                    "thaka.project_not_active",
                    "Material can only be issued to an active Thaka project.");
            }

            foreach (var productId in command.Lines
                .Select(x => x.ProductId)
                .Distinct()
                .OrderBy(x => x))
            {
                await _resourceLock.AcquireAsync("product", productId, ct);
            }
            try
            {
                var prepared = new List<PreparedThakaLine>();
                var stocks = new Dictionary<Guid, StockBalance>();

                foreach (var input in command.Lines)
                {
                    var product = await _catalog.GetProductForUpdateAsync(
                        input.ProductId,
                        ct);
                    if (product is null || !product.IsActive)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "thaka.product_inactive",
                            "One or more products are missing or inactive.");
                    }

                    if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                            product.Id,
                            ct))
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "inventory.stocktake_blocks_product",
                            $"Product '{product.Name}' is locked by an active stocktake.");
                    }

                    var productUnit = await _catalog.GetProductUnitAsync(
                        input.ProductUnitId,
                        ct);
                    if (productUnit is null ||
                        productUnit.ProductId != product.Id ||
                        !productUnit.IsActive ||
                        !productUnit.CanUseInThaka)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "thaka.unit_invalid",
                            $"Selected unit is not valid for '{product.Name}'.");
                    }

                    var authoritativeCharge = Money(product.DefaultSalePrice);
                    if (Money(input.ExpectedUnitCharge) != authoritativeCharge)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "thaka.price_stale",
                            $"Price changed for '{product.Name}'. Refresh before issuing.");
                    }
                    var quantity = TransactionQuantitySnapshot.Create(
                        productUnit,
                        input.EnteredQuantity,
                        product.TrackingMode);

                    var stock = await _inventory.GetStockBalanceForUpdateAsync(
                        product.Id,
                        ct);
                    if (stock is null || stock.SellableQty < quantity.BaseQuantity)
                    {
                        return Result<IssueThakaMaterialResult>.Failure(
                            "thaka.insufficient_stock",
                            $"Insufficient sellable stock for '{product.Name}'.");
                    }
                    stocks[product.Id] = stock;

                    IReadOnlyList<InventoryUnit> serialized = Array.Empty<InventoryUnit>();
                    if (product.TrackingMode == TrackingMode.Serialized)
                    {
                        if (!QuantityMath.IsWhole(quantity.BaseQuantity) ||
                            input.InventoryUnitIds.Count != decimal.ToInt32(quantity.BaseQuantity) ||
                            input.InventoryUnitIds.Distinct().Count() != input.InventoryUnitIds.Count)
                        {
                            return Result<IssueThakaMaterialResult>.Failure(
                                "thaka.serial_selection_invalid",
                                "Serialized issue requires one unique inventory unit per base unit.");
                        }

                        serialized = await _inventory.GetInventoryUnitsForUpdateAsync(
                            product.Id,
                            input.InventoryUnitIds,
                            ct);
                        if (serialized.Count != input.InventoryUnitIds.Count ||
                            serialized.Any(x =>
                                x.Status != InventoryUnitStatus.InStock ||
                                x.InventoryLotId is null))
                        {
                            return Result<IssueThakaMaterialResult>.Failure(
                                "thaka.serial_not_available",
                                "One or more serialized units are not available.");
                        }
                    }

                    prepared.Add(new PreparedThakaLine(
                        input,
                        product,
                        productUnit,
                        quantity,
                        authoritativeCharge,
                        Money(authoritativeCharge * input.EnteredQuantity),
                        serialized));
                }
                var issue = new ThakaMaterialIssue
                {
                    ProjectId = project.Id,
                    ChallanNumber = await _numbers.NextAsync("THK-ISS", ct),
                    ClientOperationId = command.ClientOperationId,
                    Note = Normalize(command.Note),
                    IssuedBy = command.ActorId,
                    IssuedAt = _clock.UtcNow
                };
                _thaka.AddMaterialIssue(issue);

                decimal totalCost = 0m;
                foreach (var line in prepared)
                {
                    var stock = stocks[line.Product.Id];
                    var before = stock.SellableQty;
                    var movement = new InventoryMovement
                    {
                        ProductId = line.Product.Id,
                        MovementType = InventoryMovementType.ThakaOut,
                        ReferenceType = "THAKA_MATERIAL_ISSUE",
                        ReferenceId = issue.Id,
                        ActorId = command.ActorId,
                        OccurredAt = _clock.UtcNow,
                        CorrelationId = command.ClientOperationId,
                        Note = issue.ChallanNumber
                    };
                    _inventory.AddMovement(movement);

                    decimal lineCost;
                    if (line.Product.TrackingMode == TrackingMode.Serialized)
                    {
                        lineCost = await ConsumeSerializedAsync(line, movement, ct);
                    }
                    else
                    {
                        lineCost = await _costs.RemoveCarryingValueAsync(
                            line.Product.Id,
                            line.Quantity.BaseQuantity,
                            null,
                            ct);
                        var unitCost = Cost(lineCost / line.Quantity.BaseQuantity);
                        await _costs.ConsumeBucketAsync(
                            line.Product.Id,
                            InventoryBucket.Sellable,
                            line.Quantity.BaseQuantity,
                            movement.Id,
                            unitCost,
                            ct);
                    }
                    stock.ApplyDelta(
                        InventoryBucket.Sellable,
                        -line.Quantity.BaseQuantity);
                    var costPerBase = Cost(lineCost / line.Quantity.BaseQuantity);
                    movement.UnitCostSnapshot = costPerBase;

                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = InventoryBucket.Sellable,
                        QuantityDelta = -line.Quantity.BaseQuantity,
                        QuantityBefore = before,
                        QuantityAfter = stock.SellableQty
                    });

                    var item = new ThakaMaterialIssueItem
                    {
                        MaterialIssueId = issue.Id,
                        InventoryMovementId = movement.Id,
                        ProductId = line.Product.Id,
                        ProductUnitId = line.ProductUnit.Id,
                        ProductNameSnapshot = line.Product.Name,
                        SkuSnapshot = line.Product.Sku,
                        EnteredQuantity = line.Quantity.EnteredQuantity,
                        FactorToBaseSnapshot = line.Quantity.FactorToBaseSnapshot,
                        BaseQuantity = line.Quantity.BaseQuantity,
                        UnitCharge = line.UnitCharge,
                        LineCharge = line.LineCharge,
                        UnitCostSnapshot = costPerBase,
                        TotalCostSnapshot = Cost(lineCost),
                        GrossProfitSnapshot = Money(line.LineCharge - lineCost)
                    };
                    _thaka.AddMaterialIssueItem(item);

                    foreach (var unit in line.SerializedUnits)
                    {
                        _thaka.AddMaterialIssueUnit(new ThakaMaterialIssueUnit
                        {
                            MaterialIssueItemId = item.Id,
                            InventoryUnitId = unit.Id,
                            UnitCostSnapshot = unit.AcquisitionCost
                        });
                    }

                    totalCost += lineCost;
                }
                issue.TotalCharge = Money(prepared.Sum(x => x.LineCharge));
                issue.TotalCost = Cost(totalCost);
                issue.GrossProfit = Money(issue.TotalCharge - totalCost);

                _audit.Record(
                    "THAKA_MATERIAL_ISSUED",
                    "THAKA_MATERIAL_ISSUE",
                    issue.Id,
                    command.ActorId,
                    command.ClientOperationId,
                    $"{issue.ChallanNumber}: {issue.TotalCharge:0.00}");

                await _unitOfWork.SaveChangesAsync(ct);
                return Result<IssueThakaMaterialResult>.Success(new(
                    issue.Id,
                    issue.ChallanNumber,
                    issue.TotalCharge,
                    issue.TotalCost,
                    false));
            }
            catch (BusinessRuleException ex)
            {
                return Result<IssueThakaMaterialResult>.Failure(ex.Code, ex.Message);
            }
        }, cancellationToken);
    }

    private async Task<decimal> ConsumeSerializedAsync(
        PreparedThakaLine line,
        InventoryMovement movement,
        CancellationToken cancellationToken)
    {
        decimal totalCost = 0m;
        foreach (var unit in line.SerializedUnits.OrderBy(x => x.Id))
        {
            var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                unit.InventoryLotId!.Value,
                InventoryBucket.Sellable,
                cancellationToken)
                ?? throw new BusinessRuleException(
                    "thaka.serial_lot_missing",
                    "Serialized unit inventory lot was not found."); if (lotBalance.Quantity < 1m)
            {
                throw new BusinessRuleException(
                    "thaka.serial_lot_insufficient",
                    "Serialized unit inventory lot is no longer sellable.");
            }

            lotBalance.Quantity = QuantityMath.RoundQuantity(
                lotBalance.Quantity - 1m);
            _inventory.AddLotConsumption(new InventoryLotConsumption
            {
                LotId = unit.InventoryLotId.Value,
                MovementId = movement.Id,
                Quantity = 1m,
                UnitCostSnapshot = unit.AcquisitionCost,
                TotalCostSnapshot = unit.AcquisitionCost,
                OccurredAt = _clock.UtcNow
            });

            totalCost += await _costs.RemoveCarryingValueAsync(
                line.Product.Id,
                1m,
                unit.AcquisitionCost,
                cancellationToken);

            var from = unit.Status;
            unit.Status = InventoryUnitStatus.IssuedThaka;
            unit.Version++;
            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = unit.Id,
                FromStatus = from,
                ToStatus = unit.Status
            });
        }

        return Cost(totalCost);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
    private static decimal Money(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}
