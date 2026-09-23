using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;

namespace EdgeRetails.Application.Features.Catalog;

public sealed record ProductCatalogInput(
    string Name,
    string Sku,
    string? Brand,
    string? Model,
    Guid? CategoryId,
    Guid BaseUnitId,
    TrackingMode TrackingMode,
    bool SerialTrackingEnabled,
    bool ImeiTrackingEnabled,
    decimal? ReferencePurchaseCost,
    decimal DefaultSalePrice,
    decimal MinimumStockLevel,
    int DefaultWarrantyMonths,
    string? AttributesJson,
    int AttributesSchemaVersion);

public sealed record CreateProductCommand(
    Guid ActorId,
    ProductCatalogInput Product);

public sealed record UpdateProductCommand(
    Guid ActorId,
    Guid ProductId,
    long ExpectedVersion,
    ProductCatalogInput Product);

public sealed record DeactivateProductCommand(
    Guid ActorId,
    Guid ProductId,
    long ExpectedVersion);

public sealed record ReactivateProductCommand(
    Guid ActorId,
    Guid ProductId,
    long ExpectedVersion);

public sealed record SetSupplierProductActiveCommand(
    Guid ActorId,
    Guid ProductId,
    Guid SupplierId,
    bool IsActive,
    long? ExpectedVersion = null);

public sealed record ProductMutationResult(Guid ProductId, long Version);

internal static class ProductCatalogCommandRules
{
    public static async Task<Result<string>> ValidateAndNormalizeAsync(
        ICatalogRepository catalog,
        ProductCatalogInput input,
        Guid? existingProductId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            return Result<string>.Failure(
                "catalog.product_name_required",
                "Product name is required.");
        }

        string normalizedSku;
        try
        {
            normalizedSku = TraceabilityCodeRules.NormalizeSku(input.Sku);
        }
        catch (BusinessRuleException ex)
        {
            return Result<string>.Failure(ex.Code, ex.Message);
        }

        var duplicate = await catalog.GetProductBySkuAsync(normalizedSku, cancellationToken);
        if (duplicate is not null && duplicate.Id != existingProductId)
        {
            return Result<string>.Failure(
                "catalog.sku_duplicate",
                "SKU is already assigned to another product.");
        }

        var baseUnit = await catalog.GetUnitAsync(input.BaseUnitId, cancellationToken);
        if (baseUnit is null || !baseUnit.IsActive)
        {
            return Result<string>.Failure(
                "catalog.base_unit_unavailable",
                "Selected base unit is unavailable.");
        }

        if (input.CategoryId is Guid categoryId)
        {
            var category = await catalog.GetCategoryAsync(categoryId, cancellationToken);
            if (category is null || !category.IsActive)
            {
                return Result<string>.Failure(
                    "catalog.category_unavailable",
                    "Selected category is unavailable.");
            }
        }

        if (input.ReferencePurchaseCost < 0m ||
            input.DefaultSalePrice < 0m ||
            input.MinimumStockLevel < 0m)
        {
            return Result<string>.Failure(
                "catalog.price_or_threshold_negative",
                "Prices and minimum stock level cannot be negative.");
        }

        if (input.DefaultWarrantyMonths < 0)
        {
            return Result<string>.Failure(
                "catalog.warranty_months_negative",
                "Default warranty months cannot be negative.");
        }

        var probe = new Product
        {
            TrackingMode = input.TrackingMode,
            SerialTrackingEnabled = input.SerialTrackingEnabled,
            ImeiTrackingEnabled = input.ImeiTrackingEnabled,
            AttributesJson = input.AttributesJson,
            AttributesSchemaVersion = input.AttributesSchemaVersion
        };

        try
        {
            probe.ValidateTrackingPolicy();
            probe.ValidateAttributes();
        }
        catch (BusinessRuleException ex)
        {
            return Result<string>.Failure(ex.Code, ex.Message);
        }

        return Result<string>.Success(normalizedSku);
    }

    public static void Apply(Product product, ProductCatalogInput input, string normalizedSku)
    {
        product.Name = input.Name.Trim();
        product.Sku = normalizedSku;
        product.Brand = NormalizeOptional(input.Brand);
        product.Model = NormalizeOptional(input.Model);
        product.CategoryId = input.CategoryId;
        product.BaseUnitId = input.BaseUnitId;
        product.TrackingMode = input.TrackingMode;
        product.SerialTrackingEnabled = input.SerialTrackingEnabled;
        product.ImeiTrackingEnabled = input.ImeiTrackingEnabled;
        product.ReferencePurchaseCost = input.ReferencePurchaseCost;
        product.DefaultSalePrice = decimal.Round(input.DefaultSalePrice, 2, MidpointRounding.AwayFromZero);
        product.MinimumStockLevel = QuantityMath.RoundQuantity(input.MinimumStockLevel);
        product.DefaultWarrantyMonths = input.DefaultWarrantyMonths;
        product.AttributesJson = NormalizeOptional(input.AttributesJson);
        product.AttributesSchemaVersion = input.AttributesSchemaVersion;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class CreateProductHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public CreateProductHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<ProductMutationResult>> HandleAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return Result<ProductMutationResult>.Failure(
                    auth.Error!.Code,
                    auth.Error.Message);
            }

            var validation = await ProductCatalogCommandRules.ValidateAndNormalizeAsync(
                _catalog,
                command.Product,
                null,
                ct);
            if (!validation.IsSuccess || validation.Value is null)
            {
                return Result<ProductMutationResult>.Failure(
                    validation.Error!.Code,
                    validation.Error.Message);
            }

            var product = new Product
            {
                IsActive = true,
                Version = 1
            };
            ProductCatalogCommandRules.Apply(product, command.Product, validation.Value);

            _catalog.AddProduct(product);
            _catalog.AddProductUnit(new ProductUnit
            {
                ProductId = product.Id,
                UnitId = product.BaseUnitId,
                FactorToBaseUnit = 1m,
                CanPurchase = true,
                CanSell = true,
                CanUseInThaka = true,
                IsDefaultPurchaseUnit = true,
                IsDefaultSaleUnit = true,
                IsActive = true
            });

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<ProductMutationResult>.Success(
                new ProductMutationResult(product.Id, product.Version));
        }, cancellationToken);
}

public sealed class UpdateProductHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly IProductCatalogSafetyReadService _safety;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateProductHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        IProductCatalogSafetyReadService safety,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _safety = safety;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<ProductMutationResult>> HandleAsync(
        UpdateProductCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return Result<ProductMutationResult>.Failure(
                    auth.Error!.Code,
                    auth.Error.Message);
            }

            var product = await _catalog.GetProductForUpdateAsync(command.ProductId, ct);
            if (product is null)
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.product_not_found",
                    "Product was not found.");
            }

            if (product.Version != command.ExpectedVersion)
            {
                return Result<ProductMutationResult>.Failure(
                    "concurrency.stale_product",
                    "Product was changed by another operation. Refresh and try again.");
            }

            if (product.BaseUnitId != command.Product.BaseUnitId)
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.base_unit_change_requires_reconfiguration",
                    "Base unit cannot be changed through normal product editing. Use the catalog unit reconfiguration workflow.");
            }

            var trackingPolicyChanged =
                product.TrackingMode != command.Product.TrackingMode ||
                product.SerialTrackingEnabled != command.Product.SerialTrackingEnabled ||
                product.ImeiTrackingEnabled != command.Product.ImeiTrackingEnabled;

            if (trackingPolicyChanged &&
                await _safety.HasStockOrHistoryAsync(product.Id, ct))
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.tracking_policy_locked",
                    "Tracking policy cannot be changed after stock or inventory history exists.");
            }

            var validation = await ProductCatalogCommandRules.ValidateAndNormalizeAsync(
                _catalog,
                command.Product,
                product.Id,
                ct);
            if (!validation.IsSuccess || validation.Value is null)
            {
                return Result<ProductMutationResult>.Failure(
                    validation.Error!.Code,
                    validation.Error.Message);
            }

            ProductCatalogCommandRules.Apply(product, command.Product, validation.Value);
            product.Version++;
            await _unitOfWork.SaveChangesAsync(ct);

            return Result<ProductMutationResult>.Success(
                new ProductMutationResult(product.Id, product.Version));
        }, cancellationToken);
}

public sealed class DeactivateProductHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public DeactivateProductHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<ProductMutationResult>> HandleAsync(
        DeactivateProductCommand command,
        CancellationToken cancellationToken) =>
        SetActiveAsync(command.ActorId, command.ProductId, command.ExpectedVersion, false, cancellationToken);

    private Task<Result<ProductMutationResult>> SetActiveAsync(
        Guid actorId,
        Guid productId,
        long expectedVersion,
        bool isActive,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var auth = await _authorizer.AuthorizeAsync(actorId, PermissionKeys.InventoryManage, ct);
            if (!auth.IsSuccess)
            {
                return Result<ProductMutationResult>.Failure(auth.Error!.Code, auth.Error.Message);
            }

            var product = await _catalog.GetProductForUpdateAsync(productId, ct);
            if (product is null)
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.product_not_found",
                    "Product was not found.");
            }

            if (product.Version != expectedVersion)
            {
                return Result<ProductMutationResult>.Failure(
                    "concurrency.stale_product",
                    "Product was changed by another operation. Refresh and try again.");
            }

            product.IsActive = isActive;
            product.Version++;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<ProductMutationResult>.Success(
                new ProductMutationResult(product.Id, product.Version));
        }, cancellationToken);
}

public sealed class ReactivateProductHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public ReactivateProductHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<ProductMutationResult>> HandleAsync(
        ReactivateProductCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return Result<ProductMutationResult>.Failure(auth.Error!.Code, auth.Error.Message);
            }

            var product = await _catalog.GetProductForUpdateAsync(command.ProductId, ct);
            if (product is null)
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.product_not_found",
                    "Product was not found.");
            }

            if (product.Version != command.ExpectedVersion)
            {
                return Result<ProductMutationResult>.Failure(
                    "concurrency.stale_product",
                    "Product was changed by another operation. Refresh and try again.");
            }

            product.IsActive = true;
            product.Version++;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<ProductMutationResult>.Success(
                new ProductMutationResult(product.Id, product.Version));
        }, cancellationToken);
}

public sealed class SetSupplierProductActiveHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly ITraceabilityRepository _traceability;
    private readonly IPartyRepository _parties;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SetSupplierProductActiveHandler(
        ICatalogRepository catalog,
        ITraceabilityRepository traceability,
        IPartyRepository parties,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _catalog = catalog;
        _traceability = traceability;
        _parties = parties;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result<Guid>> HandleAsync(
        SetSupplierProductActiveCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return Result<Guid>.Failure(auth.Error!.Code, auth.Error.Message);
            }

            var product = await _catalog.GetProductAsync(command.ProductId, ct);
            var supplier = await _parties.GetSupplierAsync(command.SupplierId, ct);
            if (product is null)
            {
                return Result<Guid>.Failure("catalog.product_not_found", "Product was not found.");
            }
            if (supplier is null || !supplier.IsActive)
            {
                return Result<Guid>.Failure(
                    "catalog.supplier_unavailable",
                    "Supplier is unavailable.");
            }

            var link = await _traceability.GetSupplierProductForUpdateAsync(
                command.SupplierId,
                command.ProductId,
                ct);

            if (link is null)
            {
                if (!command.IsActive)
                {
                    return Result<Guid>.Failure(
                        "catalog.supplier_product_not_found",
                        "Supplier-product relationship was not found.");
                }

                link = new SupplierProduct
                {
                    SupplierId = command.SupplierId,
                    ProductId = command.ProductId,
                    NextItemSequence = 1,
                    IsActive = true,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow,
                    Version = 1
                };
                _traceability.AddSupplierProduct(link);
            }
            else
            {
                if (command.ExpectedVersion is long expected && link.Version != expected)
                {
                    return Result<Guid>.Failure(
                        "concurrency.stale_supplier_product",
                        "Supplier-product relationship changed. Refresh and try again.");
                }

                link.IsActive = command.IsActive;
                link.UpdatedAt = _clock.UtcNow;
                link.Version++;
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(link.Id);
        }, cancellationToken);
}
