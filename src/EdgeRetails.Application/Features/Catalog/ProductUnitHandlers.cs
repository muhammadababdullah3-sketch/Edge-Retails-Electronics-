using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
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
    IReadOnlyList<ProductUnitInput> Units);

public sealed class ConfigureProductUnitsHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public ConfigureProductUnitsHandler(
        ICatalogRepository catalog,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        ConfigureProductUnitsCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var product = await _catalog.GetProductAsync(command.ProductId, ct);
            if (product is null)
            {
                return Result.Failure("catalog.product_not_found", "Product was not found.");
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

            var baseInput = command.Units.SingleOrDefault(x => x.UnitId == product.BaseUnitId);
            if (baseInput is null || QuantityMath.RoundFactor(baseInput.FactorToBaseUnit) != 1m)
            {
                return Result.Failure(
                    "catalog.base_unit_required",
                    "Base unit must exist with conversion factor 1.");
            }

            if (command.Units.Count(x => x.CanPurchase && x.IsDefaultPurchaseUnit) > 1)
            {
                return Result.Failure(
                    "catalog.multiple_default_purchase_units",
                    "Only one default purchase unit is allowed.");
            }

            if (command.Units.Count(x => x.CanSell && x.IsDefaultSaleUnit) > 1)
            {
                return Result.Failure(
                    "catalog.multiple_default_sale_units",
                    "Only one default sale unit is allowed.");
            }

            if (command.Units.Any(x => x.IsDefaultPurchaseUnit && !x.CanPurchase) ||
                command.Units.Any(x => x.IsDefaultSaleUnit && !x.CanSell))
            {
                return Result.Failure(
                    "catalog.invalid_default_unit",
                    "A default unit must be enabled for the corresponding operation.");
            }

            foreach (var input in command.Units)
            {
                if (input.FactorToBaseUnit <= 0)
                {
                    return Result.Failure(
                        "catalog.conversion_factor_positive",
                        "Conversion factor must be greater than zero.");
                }

                if (product.TrackingMode == TrackingMode.Serialized &&
                    !QuantityMath.IsWhole(input.FactorToBaseUnit))
                {
                    return Result.Failure(
                        "catalog.serialized_conversion_whole",
                        "Serialized product unit factors must resolve to whole units.");
                }

                var unit = await _catalog.GetUnitAsync(input.UnitId, ct);
                if (unit is null || !unit.IsActive)
                {
                    return Result.Failure(
                        "catalog.unit_unavailable",
                        "One of the selected units is unavailable.");
                }
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

public sealed record SetProductUnitBarcodeCommand(Guid ProductUnitId, string Barcode);

public sealed class SetProductUnitBarcodeHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SetProductUnitBarcodeHandler(
        ICatalogRepository catalog,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        SetProductUnitBarcodeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
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
