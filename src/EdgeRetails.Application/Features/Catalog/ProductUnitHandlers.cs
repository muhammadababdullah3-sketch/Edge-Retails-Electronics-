using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;

namespace EdgeRetails.Application.Features.Catalog;

public sealed record ProductUnitInput(
    Guid UnitId,
    decimal FactorToBaseUnit,
    bool CanPurchase,
    bool CanSell,
    bool CanUseInThaka,
    bool IsDefaultPurchaseUnit,
    bool IsDefaultSaleUnit);

public sealed record ConfigureProductUnitsCommand(
    Guid ProductId,
    IReadOnlyList<ProductUnitInput> Units,
    Guid ActorId = default,
    long? ExpectedVersion = null);

public sealed class ConfigureProductUnitsHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IApplicationPermissionAuthorizer? _authorizer;
    private readonly IProductCatalogSafetyReadService? _safety;
    private readonly IResourceLock? _resourceLock;

    public ConfigureProductUnitsHandler(
        ICatalogRepository catalog,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IApplicationPermissionAuthorizer? authorizer = null,
        IProductCatalogSafetyReadService? safety = null,
        IResourceLock? resourceLock = null)
    {
        _catalog = catalog;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _authorizer = authorizer;
        _safety = safety;
        _resourceLock = resourceLock;
    }

    public Task<Result> HandleAsync(
        ConfigureProductUnitsCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            // Fail closed. A missing authorizer or an absent actor must deny, never proceed:
            // allowing either to bypass means a caller can mutate catalog units with no permission
            // check at all. This matches the non-nullable, unconditional pattern used by the other
            // catalog mutation handlers.
            if (_authorizer is null)
            {
                return Result.Failure(
                    "catalog.authorization_unavailable",
                    "Catalog unit changes are unavailable because authorization is not configured.");
            }

            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return Result.Failure(auth.Error!.Code, auth.Error.Message);
            }

            if (_resourceLock is not null)
            {
                await _resourceLock.AcquireAsync("product", command.ProductId, ct);
            }

            var product = await _catalog.GetProductForUpdateAsync(command.ProductId, ct)
                          ?? await _catalog.GetProductAsync(command.ProductId, ct);
            if (product is null)
            {
                return Result.Failure("catalog.product_not_found", "Product was not found.");
            }

            if (command.ExpectedVersion.HasValue && product.Version != command.ExpectedVersion.Value)
            {
                return Result.Failure(
                    "catalog.concurrency_conflict",
                    "Product has been modified by another operation.");
            }

            if (command.Units.Count == 0)
            {
                return Result.Failure(
                    "catalog.product_units_required",
                    "At least the base unit configuration is required.");
            }

            if (command.Units.Select(x => x.UnitId).Distinct().Count() != command.Units.Count)
            {
                return Result.Failure(
                    "catalog.duplicate_product_unit",
                    "A unit can only appear once for a product.");
            }

            var unitLookup = new Dictionary<Guid, Unit>();
            foreach (var id in command.Units.Select(u => u.UnitId).Append(product.BaseUnitId).Distinct())
            {
                var u = await _catalog.GetUnitAsync(id, ct);
                if (u != null) unitLookup[id] = u;
            }

            var validation = ProductUnitValidator.ValidateUnitInputs(product.TrackingMode, product.BaseUnitId, command.Units, unitLookup);
            if (validation is not null)
            {
                return Result.Failure(validation.Code, validation.Message);
            }

            var existing = await _catalog.GetProductUnitsAsync(product.Id, ct);
            foreach (var current in existing)
            {
                var incoming = command.Units.SingleOrDefault(x => x.UnitId == current.UnitId);
                if (incoming is null)
                {
                    current.IsActive = false;
                    current.IsDefaultPurchaseUnit = false;
                    current.IsDefaultSaleUnit = false;
                    continue;
                }

                if (QuantityMath.RoundFactor(incoming.FactorToBaseUnit) != QuantityMath.RoundFactor(current.FactorToBaseUnit))
                {
                    if (_safety is not null && await _safety.HasUnitUsageAsync(current.Id, ct))
                    {
                        return Result.Failure(
                            "catalog.product_unit_factor_locked",
                            $"Conversion factor for unit '{current.UnitId}' cannot be modified after commercial or stock history exists.");
                    }
                }

                Apply(current, incoming);
            }

            foreach (var input in command.Units)
            {
                if (existing.Any(x => x.UnitId == input.UnitId))
                {
                    continue;
                }

                var productUnit = new ProductUnit
                {
                    ProductId = product.Id,
                    UnitId = input.UnitId
                };
                Apply(productUnit, input);
                _catalog.AddProductUnit(productUnit);
            }

            product.Version++;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }

    private static void Apply(ProductUnit target, ProductUnitInput input)
    {
        target.FactorToBaseUnit = QuantityMath.RoundFactor(input.FactorToBaseUnit);
        target.CanPurchase = input.CanPurchase;
        target.CanSell = input.CanSell;
        target.CanUseInThaka = input.CanUseInThaka;
        target.IsDefaultPurchaseUnit = input.IsDefaultPurchaseUnit;
        target.IsDefaultSaleUnit = input.IsDefaultSaleUnit;
        target.IsActive = true;
    }
}

public sealed record SetProductUnitBarcodeCommand(Guid ProductUnitId, string Barcode, Guid ActorId = default);

public sealed class SetProductUnitBarcodeHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IApplicationPermissionAuthorizer? _authorizer;

    public SetProductUnitBarcodeHandler(
        ICatalogRepository catalog,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IApplicationPermissionAuthorizer? authorizer = null)
    {
        _catalog = catalog;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _authorizer = authorizer;
    }

    public Task<Result<Guid>> HandleAsync(
        SetProductUnitBarcodeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            // Fail closed, for the same reason as ConfigureProductUnitsHandler: a null authorizer or
            // an empty actor previously skipped the check entirely and let the barcode be written
            // with no permission evaluation.
            if (_authorizer is null)
            {
                return Result<Guid>.Failure(
                    "catalog.authorization_unavailable",
                    "Barcode changes are unavailable because authorization is not configured.");
            }

            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return Result<Guid>.Failure(auth.Error!.Code, auth.Error.Message);
            }

            var productUnit = await _catalog.GetProductUnitAsync(command.ProductUnitId, ct);
            if (productUnit is null || !productUnit.IsActive)
            {
                return Result<Guid>.Failure(
                    "catalog.product_unit_not_found",
                    "Product unit was not found.");
            }

            var normalized = command.Barcode.Trim();
            if (normalized.Length == 0)
            {
                return Result<Guid>.Failure("catalog.barcode_required", "Barcode is required.");
            }

            var existing = await _catalog.GetBarcodeAsync(normalized, ct);
            if (existing is not null && existing.ProductUnitId != productUnit.Id)
            {
                return Result<Guid>.Failure(
                    "catalog.barcode_duplicate",
                    "Barcode is already assigned to another product unit.");
            }

            if (existing is not null)
            {
                existing.IsActive = true;
                await _unitOfWork.SaveChangesAsync(ct);
                return Result<Guid>.Success(existing.Id);
            }

            var barcode = new ProductUnitBarcode
            {
                ProductUnitId = productUnit.Id,
                Barcode = normalized,
                IsActive = true
            };
            _catalog.AddBarcode(barcode);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(barcode.Id);
        }, cancellationToken);
    }
}
