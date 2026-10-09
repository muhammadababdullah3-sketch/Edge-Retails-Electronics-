using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using System.Text.Json;

namespace EdgeRetails.Application.Features.Catalog;

public sealed record ProductCatalogInput(
    string Name,
    string? Sku = null,
    string? Brand = null,
    string? Model = null,
    Guid? CategoryId = null,
    Guid? CompanyId = null,
    string? ModelCode = null,
    Guid BaseUnitId = default,
    TrackingMode TrackingMode = TrackingMode.Quantity,
    bool SerialTrackingEnabled = false,
    bool ImeiTrackingEnabled = false,
    decimal? ReferencePurchaseCost = null,
    decimal DefaultSalePrice = 0m,
    decimal MinimumStockLevel = 0m,
    int DefaultWarrantyMonths = 0,
    string? AttributesJson = null,
    int AttributesSchemaVersion = 1);

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

public sealed record SaveProductAggregateCommand(
    Guid ActorId,
    Guid? ProductId,
    long? ExpectedVersion,
    ProductCatalogInput Product,
    IReadOnlyList<ProductUnitInput> Units,
    IReadOnlyList<Guid> LinkedSupplierIds,
    Guid? ClientOperationId = null);

public sealed record ProductMutationResult(Guid ProductId, long Version, string? Sku = null);

public static class ProductAggregatePayloadFingerprint
{
    public static string Compute(SaveProductAggregateCommand command)
    {
        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var input = command.Product;
        var normalized = input with
        {
            Name = System.Text.RegularExpressions.Regex.Replace(input.Name?.Trim() ?? string.Empty, @"\s+", " "),
            Sku = Optional(input.Sku)?.ToUpperInvariant(),
            Brand = Optional(input.Brand),
            Model = Optional(input.Model),
            ModelCode = Optional(input.ModelCode)?.ToUpperInvariant(),
            AttributesJson = Optional(input.AttributesJson),
            DefaultSalePrice = decimal.Round(input.DefaultSalePrice, 2, MidpointRounding.AwayFromZero),
            MinimumStockLevel = QuantityMath.RoundQuantity(input.MinimumStockLevel)
        };
        // JSON supplies invariant numeric encoding and unambiguous string boundaries.
        return OperationPayloadFingerprint.ComputeSha256(System.Text.Json.JsonSerializer.Serialize(new
        {
            OperationType = "Catalog.ProductAggregateSave",
            command.ActorId,
            command.ProductId,
            command.ExpectedVersion,
            Product = normalized,
            Units = (command.Units ?? []).OrderBy(x => x.UnitId).Select(x => x with
            {
                FactorToBaseUnit = QuantityMath.RoundFactor(x.FactorToBaseUnit)
            }).ToArray(),
            Suppliers = (command.LinkedSupplierIds ?? []).Distinct().OrderBy(x => x).ToArray()
        }));
    }
}

public sealed record NormalizedProductCatalogData(
    string Sku,
    string? ModelCode,
    Guid? CompanyId);

internal static class ProductCatalogCommandRules
{
    public static async Task<Result<NormalizedProductCatalogData>> ValidateAndNormalizeAsync(
        ICatalogRepository catalog,
        ProductCatalogInput input,
        Guid? existingProductId,
        CancellationToken cancellationToken)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(input.Name?.Trim() ?? string.Empty, @"\s+", " ");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result<NormalizedProductCatalogData>.Failure(
                "catalog.product_name_required",
                "Product name is required.");
        }

        if (existingProductId is null)
        {
            if (input.CategoryId is null || input.CategoryId == Guid.Empty)
            {
                return Result<NormalizedProductCatalogData>.Failure(
                    "catalog.category_required",
                    "Category is required for product creation.");
            }
        }

        Category? category = null;
        if (input.CategoryId is Guid categoryId && categoryId != Guid.Empty)
        {
            category = await catalog.GetCategoryAsync(categoryId, cancellationToken);
            if (category is null || !category.IsActive)
            {
                return Result<NormalizedProductCatalogData>.Failure(
                    "catalog.category_unavailable",
                    "Selected category is unavailable.");
            }
        }

        Company? company = null;
        if (input.CompanyId is Guid compId && compId != Guid.Empty)
        {
            company = await catalog.GetCompanyAsync(compId, cancellationToken);
            if (company is null || !company.IsActive)
            {
                return Result<NormalizedProductCatalogData>.Failure(
                    "catalog.company_unavailable",
                    "Selected company is unavailable.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(input.Brand))
        {
            company = await catalog.GetCompanyByNameAsync(input.Brand.Trim(), cancellationToken);
        }

        if (input.BaseUnitId == Guid.Empty)
        {
            return Result<NormalizedProductCatalogData>.Failure(
                "catalog.base_unit_unavailable",
                "Selected base unit is unavailable.");
        }

        var baseUnit = await catalog.GetUnitAsync(input.BaseUnitId, cancellationToken);
        if (baseUnit is null || !baseUnit.IsActive)
        {
            return Result<NormalizedProductCatalogData>.Failure(
                "catalog.base_unit_unavailable",
                "Selected base unit is unavailable.");
        }

        if (input.ReferencePurchaseCost < 0m ||
            input.DefaultSalePrice < 0m ||
            input.MinimumStockLevel < 0m)
        {
            return Result<NormalizedProductCatalogData>.Failure(
                "catalog.price_or_threshold_negative",
                "Prices and minimum stock level cannot be negative.");
        }

        if (input.DefaultWarrantyMonths < 0)
        {
            return Result<NormalizedProductCatalogData>.Failure(
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
            return Result<NormalizedProductCatalogData>.Failure(ex.Code, ex.Message);
        }

        string? normalizedModelCode = null;
        if (!string.IsNullOrWhiteSpace(input.ModelCode))
        {
            try
            {
                normalizedModelCode = TraceabilityCodeRules.NormalizeModelCode(input.ModelCode);
            }
            catch (BusinessRuleException ex)
            {
                return Result<NormalizedProductCatalogData>.Failure(ex.Code, ex.Message);
            }
        }
        else if (!string.IsNullOrWhiteSpace(input.Model))
        {
            normalizedModelCode = TraceabilityCodeRules.SuggestModelCode(input.Model);
        }

        string normalizedSku;
        if (company is not null && category is not null && !string.IsNullOrWhiteSpace(normalizedModelCode))
        {
            normalizedSku = TraceabilityCodeRules.BuildProductCode(company.Code, category.IdentitySymbol, normalizedModelCode);
        }
        else if (!string.IsNullOrWhiteSpace(input.Sku))
        {
            try
            {
                normalizedSku = TraceabilityCodeRules.NormalizeSku(input.Sku);
            }
            catch (BusinessRuleException ex)
            {
                return Result<NormalizedProductCatalogData>.Failure(ex.Code, ex.Message);
            }
        }
        else
        {
            normalizedSku = TraceabilityCodeRules.SuggestProductCode(input.Brand, name, input.Model, category?.Name);
        }

        var duplicate = await catalog.GetProductBySkuAsync(normalizedSku, cancellationToken);
        if (duplicate is not null && duplicate.Id != existingProductId)
        {
            return Result<NormalizedProductCatalogData>.Failure(
                "catalog.sku_duplicate",
                $"SKU '{normalizedSku}' is already assigned to another product.");
        }

        return Result<NormalizedProductCatalogData>.Success(
            new NormalizedProductCatalogData(normalizedSku, normalizedModelCode, company?.Id ?? input.CompanyId));
    }

    public static void Apply(Product product, ProductCatalogInput input, NormalizedProductCatalogData normalized)
    {
        product.Name = System.Text.RegularExpressions.Regex.Replace(input.Name.Trim(), @"\s+", " ");
        product.Sku = normalized.Sku;
        product.Brand = NormalizeOptional(input.Brand);
        product.Model = NormalizeOptional(input.Model);
        product.ModelCode = normalized.ModelCode;
        product.CompanyId = normalized.CompanyId;
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
    private readonly IBusinessAuditWriter? _audit;

    public UpdateProductHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        IProductCatalogSafetyReadService safety,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IBusinessAuditWriter? audit = null)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _safety = safety;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _audit = audit;
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

            var incomingSku = !string.IsNullOrWhiteSpace(command.Product.Sku)
                ? command.Product.Sku
                : product.Sku;

            if (!string.IsNullOrWhiteSpace(incomingSku))
            {
                try
                {
                    var normalizedIncomingSku = TraceabilityCodeRules.NormalizeSku(incomingSku);
                    if (!string.Equals(product.Sku, normalizedIncomingSku, StringComparison.OrdinalIgnoreCase))
                    {
                        if (await _safety.HasStockOrHistoryAsync(product.Id, ct))
                        {
                            return Result<ProductMutationResult>.Failure(
                                "catalog.sku_immutable",
                                "Product SKU is permanent and cannot be modified once stock or inventory history exists.");
                        }
                    }
                }
                catch (BusinessRuleException ex)
                {
                    return Result<ProductMutationResult>.Failure(ex.Code, ex.Message);
                }
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
                command.Product with { Sku = incomingSku },
                product.Id,
                ct);
            if (!validation.IsSuccess || validation.Value is null)
            {
                return Result<ProductMutationResult>.Failure(
                    validation.Error!.Code,
                    validation.Error.Message);
            }

            // Guard the effective values Apply persists, including derived SKU,
            // ModelCode removal, and company resolution by Brand.
            if (!string.Equals(product.ModelCode, validation.Value.ModelCode, StringComparison.OrdinalIgnoreCase) &&
                await _safety.HasStockOrHistoryAsync(product.Id, ct))
            {
                return Result<ProductMutationResult>.Failure("catalog.model_code_immutable", "Product ModelCode cannot change after authoritative history exists.");
            }
            if (product.CompanyId != validation.Value.CompanyId && await _safety.HasStockOrHistoryAsync(product.Id, ct))
            {
                return Result<ProductMutationResult>.Failure("catalog.company_immutable", "Product company cannot change after authoritative history exists.");
            }
            if (product.CategoryId != command.Product.CategoryId && await _safety.HasStockOrHistoryAsync(product.Id, ct))
            {
                return Result<ProductMutationResult>.Failure("catalog.category_immutable", "Product category cannot change after authoritative history exists.");
            }
            if (!string.Equals(product.Sku, validation.Value.Sku, StringComparison.OrdinalIgnoreCase) &&
                await _safety.HasStockOrHistoryAsync(product.Id, ct))
            {
                return Result<ProductMutationResult>.Failure("catalog.sku_immutable", "Product SKU cannot change after authoritative history exists.");
            }
            var beforeIdentity = ProductCatalogAudit.Identity(product);
            var proposed = new Product();
            ProductCatalogCommandRules.Apply(proposed, command.Product, validation.Value);
            var identityChanged = beforeIdentity != ProductCatalogAudit.Identity(proposed);
            if (identityChanged && _audit is null)
            {
                return Result<ProductMutationResult>.Failure("catalog.audit_unavailable", "Significant catalog identity changes require transactional audit authority.");
            }
            ProductCatalogCommandRules.Apply(product, command.Product, validation.Value);
            product.Version++;
            if (identityChanged)
            {
                ProductCatalogAudit.IdentityChanged(_audit!, product, beforeIdentity, command.ActorId, Guid.CreateVersion7(), "UpdateProduct");
            }
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
    private readonly IResourceLock? _resourceLock;
    private readonly IBusinessAuditWriter? _audit;

    public SetSupplierProductActiveHandler(
        ICatalogRepository catalog,
        ITraceabilityRepository traceability,
        IPartyRepository parties,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IResourceLock? resourceLock = null,
        IBusinessAuditWriter? audit = null)
    {
        _catalog = catalog;
        _traceability = traceability;
        _parties = parties;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _resourceLock = resourceLock;
        _audit = audit;
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

            if (_resourceLock is not null)
            {
                await _resourceLock.AcquireAsync("product", command.ProductId, ct);
            }
            var product = await _catalog.GetProductForUpdateAsync(command.ProductId, ct);
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

            if (_resourceLock is not null)
            {
                await _resourceLock.AcquireAsync("supplier-product", $"{command.SupplierId}:{command.ProductId}", ct);
            }

            var link = await _traceability.GetSupplierProductForUpdateAsync(
                command.SupplierId,
                command.ProductId,
                ct);

            if (link is not null && command.ExpectedVersion is long suppliedVersion && link.Version != suppliedVersion)
            {
                return Result<Guid>.Failure("concurrency.stale_supplier_product", "Supplier-product relationship changed. Refresh and try again.");
            }
            var beforePair = link is null ? null : ProductCatalogAudit.Pair(link);
            var pairChanged = link is null && command.IsActive || link is not null && link.IsActive != command.IsActive;
            if (pairChanged && _audit is null)
            {
                return Result<Guid>.Failure("catalog.audit_unavailable", "Supplier-product changes require transactional audit authority.");
            }

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

            if (pairChanged)
            {
                ProductCatalogAudit.PairChanged(_audit!, link, beforePair, command.ActorId, Guid.CreateVersion7(), "SetSupplierProductActive");
            }
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(link.Id);
        }, cancellationToken);
}

public sealed class SaveProductAggregateHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly ITraceabilityRepository _traceability;
    private readonly IPartyRepository _parties;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IProductCatalogSafetyReadService? _safety;
    private readonly IProductManagementReadService? _productReads;
    private readonly IResourceLock? _resourceLock;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;
    private readonly IBusinessAuditWriter? _audit;

    public SaveProductAggregateHandler(
        ICatalogRepository catalog,
        ITraceabilityRepository traceability,
        IPartyRepository parties,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IProductCatalogSafetyReadService? safety = null,
        IProductManagementReadService? productReads = null,
        IResourceLock? resourceLock = null,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null,
        IBusinessAuditWriter? audit = null)
    {
        _catalog = catalog;
        _traceability = traceability;
        _parties = parties;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _safety = safety;
        _productReads = productReads;
        _resourceLock = resourceLock;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
        _audit = audit;
    }

    public async Task<Result<ProductMutationResult>> HandleAsync(
        SaveProductAggregateCommand command,
        CancellationToken cancellationToken)
    {
        var auth = await _authorizer.AuthorizeAsync(
            command.ActorId,
            PermissionKeys.InventoryManage,
            cancellationToken);
        if (!auth.IsSuccess)
        {
            return Result<ProductMutationResult>.Failure(auth.Error!.Code, auth.Error.Message);
        }

        return await _transactions.ExecuteAsync(async ct =>
        {
        if (command.ClientOperationId.HasValue &&
            (command.ClientOperationId == Guid.Empty || _operationLock is null || _outcomeLedger is null))
        {
            return Result<ProductMutationResult>.Failure("operation.ledger_unavailable", "Product save replay protection is unavailable.");
        }
        string? payloadFingerprint = null;
        if (command.ClientOperationId is Guid opId && opId != Guid.Empty)
        {
            payloadFingerprint = ProductAggregatePayloadFingerprint.Compute(command);

            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(opId, ct);
            }

            if (_outcomeLedger is not null)
            {
                var existingOutcome = await _outcomeLedger.GetOutcomeAsync(opId, ct);
                if (existingOutcome is not null && existingOutcome.State == OperationOutcomeState.Succeeded)
                {
                    if (existingOutcome.OperationType != "Catalog.ProductAggregateSave" ||
                        existingOutcome.ActorId != command.ActorId ||
                        !string.Equals(existingOutcome.PayloadFingerprint, payloadFingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<ProductMutationResult>.Failure(
                            "idempotency.payload_mismatch",
                            "The operation was previously executed with different parameters.");
                    }

                    var replayedProduct = existingOutcome.EntityId.HasValue
                        ? await _catalog.GetProductAsync(existingOutcome.EntityId.Value, ct)
                        : null;
                    return Result<ProductMutationResult>.Success(
                        new ProductMutationResult(existingOutcome.EntityId ?? Guid.Empty, replayedProduct?.Version ?? 1L, existingOutcome.DocumentNumber));
                }
            }
        }

        if (_resourceLock is not null && command.ProductId.HasValue && command.ProductId.Value != Guid.Empty)
        {
            await _resourceLock.AcquireAsync("product", command.ProductId.Value, ct);
        }
            var auditCorrelation = command.ClientOperationId is Guid correlation && correlation != Guid.Empty
                ? correlation : Guid.CreateVersion7();
            ProductIdentityAuditSnapshot? beforeIdentity = null;
            Product product;
            if (command.ProductId is null || command.ProductId.Value == Guid.Empty)
            {
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

                if (_audit is null && (command.LinkedSupplierIds?.Count ?? 0) > 0)
                {
                    return Result<ProductMutationResult>.Failure("catalog.audit_unavailable", "Supplier-product changes require transactional audit authority.");
                }

                product = new Product
                {
                    IsActive = true,
                    Version = 1
                };
                ProductCatalogCommandRules.Apply(product, command.Product, validation.Value);
                _catalog.AddProduct(product);
            }
            else
            {
                var existing = await _catalog.GetProductForUpdateAsync(command.ProductId.Value, ct)
                               ?? await _catalog.GetProductAsync(command.ProductId.Value, ct);
                if (existing is null)
                {
                    return Result<ProductMutationResult>.Failure(
                        "catalog.product_not_found",
                        "Product was not found.");
                }

                if (command.ExpectedVersion.HasValue && existing.Version != command.ExpectedVersion.Value)
                {
                    return Result<ProductMutationResult>.Failure(
                        "concurrency.stale_product",
                        "Product has been modified by another operation.");
                }

                if (existing.BaseUnitId != command.Product.BaseUnitId)
                {
                    return Result<ProductMutationResult>.Failure(
                        "catalog.base_unit_change_requires_reconfiguration",
                        "Base unit cannot be changed from the normal edit workflow. Reconfigure product units first.");
                }

                var validation = await ProductCatalogCommandRules.ValidateAndNormalizeAsync(
                    _catalog,
                    command.Product,
                    existing.Id,
                    ct);
                if (!validation.IsSuccess || validation.Value is null)
                {
                    return Result<ProductMutationResult>.Failure(
                        validation.Error!.Code,
                        validation.Error.Message);
                }

                if (_safety is not null && await _safety.HasStockOrHistoryAsync(existing.Id, ct))
                {
                    if (!string.Equals(existing.Sku, validation.Value.Sku, StringComparison.OrdinalIgnoreCase))
                        return Result<ProductMutationResult>.Failure("catalog.sku_immutable", "Product SKU cannot change after authoritative history exists.");
                    if (!string.Equals(existing.ModelCode, validation.Value.ModelCode, StringComparison.OrdinalIgnoreCase))
                        return Result<ProductMutationResult>.Failure("catalog.model_code_immutable", "Product ModelCode cannot change after authoritative history exists.");
                    if (existing.CompanyId != validation.Value.CompanyId)
                        return Result<ProductMutationResult>.Failure("catalog.company_immutable", "Product company cannot change after authoritative history exists.");
                    if (existing.CategoryId != command.Product.CategoryId)
                        return Result<ProductMutationResult>.Failure("catalog.category_immutable", "Product category cannot change after authoritative history exists.");
                    if (existing.TrackingMode != command.Product.TrackingMode ||
                        existing.SerialTrackingEnabled != command.Product.SerialTrackingEnabled ||
                        existing.ImeiTrackingEnabled != command.Product.ImeiTrackingEnabled)
                        return Result<ProductMutationResult>.Failure("catalog.tracking_policy_locked", "Tracking policy cannot change after authoritative history exists.");
                }

                beforeIdentity = ProductCatalogAudit.Identity(existing);
                var proposed = new Product();
                ProductCatalogCommandRules.Apply(proposed, command.Product, validation.Value);
                if (beforeIdentity != ProductCatalogAudit.Identity(proposed) && _audit is null)
                {
                    return Result<ProductMutationResult>.Failure("catalog.audit_unavailable", "Significant catalog identity changes require transactional audit authority.");
                }
                if (_audit is null)
                {
                    // Refuse missing audit authority before changing any catalog
                    // objects, including when only the supplier set changes.
                    var desired = (command.LinkedSupplierIds ?? []).Distinct().ToHashSet();
                    foreach (var supplierId in desired.OrderBy(x => x))
                    {
                        var pair = await _traceability.GetSupplierProductForUpdateAsync(supplierId, existing.Id, ct);
                        if (pair is null || !pair.IsActive)
                        {
                            return Result<ProductMutationResult>.Failure("catalog.audit_unavailable", "Supplier-product changes require transactional audit authority.");
                        }
                    }
                    var configured = _productReads is null ? null : await _productReads.GetProductAsync(existing.Id, ct);
                    if (configured?.SupplierProducts.Any(x => x.IsActive && !desired.Contains(x.SupplierId)) == true)
                    {
                        return Result<ProductMutationResult>.Failure("catalog.audit_unavailable", "Supplier-product changes require transactional audit authority.");
                    }
                }
                ProductCatalogCommandRules.Apply(existing, command.Product, validation.Value);
                existing.Version++;
                product = existing;
            }

            var units = command.Units?.ToList() ?? new List<ProductUnitInput>();
            if (units.All(x => x.UnitId != product.BaseUnitId))
            {
                units.Insert(0, new ProductUnitInput(
                    product.BaseUnitId,
                    1m,
                    true,
                    true,
                    true,
                    true,
                    true));
            }

            if (units.Count == 0)
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.product_units_required",
                    "At least the base unit configuration is required.");
            }

            if (units.Select(x => x.UnitId).Distinct().Count() != units.Count)
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.duplicate_product_unit",
                    "A unit can only appear once for a product.");
            }

            var baseInput = units.SingleOrDefault(x => x.UnitId == product.BaseUnitId);
            if (baseInput is null || QuantityMath.RoundFactor(baseInput.FactorToBaseUnit) != 1m)
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.base_unit_required",
                    "Base unit must exist with conversion factor 1.");
            }

            if (units.Count(x => x.CanPurchase && x.IsDefaultPurchaseUnit) > 1)
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.multiple_default_purchase_units",
                    "Only one default purchase unit is allowed.");
            }

            if (units.Count(x => x.CanSell && x.IsDefaultSaleUnit) > 1)
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.multiple_default_sale_units",
                    "Only one default sale unit is allowed.");
            }

            if (units.Any(x => x.IsDefaultPurchaseUnit && !x.CanPurchase) ||
                units.Any(x => x.IsDefaultSaleUnit && !x.CanSell))
            {
                return Result<ProductMutationResult>.Failure(
                    "catalog.invalid_default_unit",
                    "A default unit must be enabled for the corresponding operation.");
            }

            foreach (var input in units)
            {
                if (input.FactorToBaseUnit <= 0)
                {
                    return Result<ProductMutationResult>.Failure(
                        "catalog.conversion_factor_positive",
                        "Conversion factor must be greater than zero.");
                }

                if (product.TrackingMode == TrackingMode.Container &&
                    !QuantityMath.IsWhole(input.FactorToBaseUnit))
                {
                    return Result<ProductMutationResult>.Failure(
                        "catalog.container_conversion_whole",
                        "Container conversion factor must be an exact whole integer count.");
                }

                if (product.TrackingMode == TrackingMode.Serialized &&
                    !QuantityMath.IsWhole(input.FactorToBaseUnit))
                {
                    return Result<ProductMutationResult>.Failure(
                        "catalog.serialized_conversion_whole",
                        "Serialized product unit factors must resolve to whole units.");
                }

                var unit = await _catalog.GetUnitAsync(input.UnitId, ct);
                if (unit is null || !unit.IsActive)
                {
                    return Result<ProductMutationResult>.Failure(
                        "catalog.unit_unavailable",
                        "One of the selected units is unavailable.");
                }
            }

            var existingUnits = await _catalog.GetProductUnitsAsync(product.Id, ct);
            foreach (var current in existingUnits)
            {
                var incoming = units.SingleOrDefault(x => x.UnitId == current.UnitId);
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
                        return Result<ProductMutationResult>.Failure(
                            "catalog.product_unit_factor_locked",
                            $"Conversion factor for unit '{current.UnitId}' cannot be modified after commercial or stock history exists.");
                    }
                }

                current.FactorToBaseUnit = QuantityMath.RoundFactor(incoming.FactorToBaseUnit);
                current.CanPurchase = incoming.CanPurchase;
                current.CanSell = incoming.CanSell;
                current.CanUseInThaka = incoming.CanUseInThaka;
                current.IsDefaultPurchaseUnit = incoming.IsDefaultPurchaseUnit;
                current.IsDefaultSaleUnit = incoming.IsDefaultSaleUnit;
                current.IsActive = true;
            }

            foreach (var input in units)
            {
                if (existingUnits.Any(x => x.UnitId == input.UnitId))
                {
                    continue;
                }

                var productUnit = new ProductUnit
                {
                    ProductId = product.Id,
                    UnitId = input.UnitId,
                    FactorToBaseUnit = QuantityMath.RoundFactor(input.FactorToBaseUnit),
                    CanPurchase = input.CanPurchase,
                    CanSell = input.CanSell,
                    CanUseInThaka = input.CanUseInThaka,
                    IsDefaultPurchaseUnit = input.IsDefaultPurchaseUnit,
                    IsDefaultSaleUnit = input.IsDefaultSaleUnit,
                    IsActive = true
                };
                _catalog.AddProductUnit(productUnit);
            }

            var desiredSuppliers = (command.LinkedSupplierIds ?? Array.Empty<Guid>()).Distinct().ToHashSet();
            var currentInfo = _productReads is not null && command.ProductId is Guid existingProductId && existingProductId != Guid.Empty
                ? await _productReads.GetProductAsync(existingProductId, ct)
                : null;
            if (_resourceLock is not null)
            {
                // Lock every changed pair, including removals, after the product row.
                foreach (var supId in desiredSuppliers.Concat(currentInfo?.SupplierProducts.Select(x => x.SupplierId) ?? [])
                    .Distinct().OrderBy(x => x))
                {
                    await _resourceLock.AcquireAsync("supplier-product", $"{supId:D}:{product.Id:D}", ct);
                }
            }
            foreach (var supId in desiredSuppliers.OrderBy(x => x))
            {
                var sup = await _parties.GetSupplierAsync(supId, ct);
                if (sup is null || !sup.IsActive)
                {
                    return Result<ProductMutationResult>.Failure(
                        "catalog.supplier_unavailable",
                        "Supplier is unavailable.");
                }

                var link = await _traceability.GetSupplierProductForUpdateAsync(supId, product.Id, ct);
                var beforePair = link is null ? null : ProductCatalogAudit.Pair(link);
                var pairChanged = link is null || !link.IsActive;
                if (pairChanged && _audit is null)
                {
                    return Result<ProductMutationResult>.Failure("catalog.audit_unavailable", "Supplier-product changes require transactional audit authority.");
                }
                if (link is null)
                {
                    link = new SupplierProduct
                    {
                        SupplierId = supId,
                        ProductId = product.Id,
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
                    link.IsActive = true;
                    link.UpdatedAt = _clock.UtcNow;
                    link.Version++;
                }
                if (pairChanged)
                {
                    ProductCatalogAudit.PairChanged(_audit!, link, beforePair, command.ActorId, auditCorrelation, "SaveProductAggregate");
                }
            }

            if (currentInfo is not null)
            {
                    foreach (var existingLink in currentInfo.SupplierProducts.Where(x => x.IsActive).OrderBy(x => x.SupplierId))
                    {
                        if (!desiredSuppliers.Contains(existingLink.SupplierId))
                        {
                            var link = await _traceability.GetSupplierProductForUpdateAsync(existingLink.SupplierId, product.Id, ct);
                            if (link is not null && link.IsActive)
                            {
                                if (_audit is null)
                                {
                                    return Result<ProductMutationResult>.Failure("catalog.audit_unavailable", "Supplier-product changes require transactional audit authority.");
                                }
                                var beforePair = ProductCatalogAudit.Pair(link);
                                link.IsActive = false;
                                link.UpdatedAt = _clock.UtcNow;
                                link.Version++;
                                ProductCatalogAudit.PairChanged(_audit, link, beforePair, command.ActorId, auditCorrelation, "SaveProductAggregate");
                            }
                        }
                    }
            }

            if (beforeIdentity is not null && beforeIdentity != ProductCatalogAudit.Identity(product))
            {
                ProductCatalogAudit.IdentityChanged(_audit!, product, beforeIdentity, command.ActorId, auditCorrelation, "SaveProductAggregate");
            }
            await _unitOfWork.SaveChangesAsync(ct);

            if (_outcomeLedger is not null && command.ClientOperationId is Guid clientOp && clientOp != Guid.Empty)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    clientOp,
                    "Catalog.ProductAggregateSave",
                    product.Id,
                    documentNumber: product.Sku,
                    payloadFingerprint: payloadFingerprint,
                    actorId: command.ActorId,
                    cancellationToken: ct);
            }

            return Result<ProductMutationResult>.Success(new ProductMutationResult(product.Id, product.Version, product.Sku));
        }, cancellationToken);
    }
}

internal sealed record ProductIdentityAuditSnapshot(string Name, string? Sku, string? Brand,
    string? Model, string? ModelCode, Guid? CompanyId, Guid? CategoryId, Guid BaseUnitId,
    TrackingMode TrackingMode, bool SerialTrackingEnabled, bool ImeiTrackingEnabled);

internal sealed record SupplierProductAuditSnapshot(Guid Id, Guid ProductId, Guid SupplierId,
    bool IsActive, long NextItemSequence, long Version);

internal static class ProductCatalogAudit
{
    public static ProductIdentityAuditSnapshot Identity(Product product) => new(product.Name, product.Sku,
        product.Brand, product.Model, product.ModelCode, product.CompanyId, product.CategoryId,
        product.BaseUnitId, product.TrackingMode, product.SerialTrackingEnabled, product.ImeiTrackingEnabled);

    public static SupplierProductAuditSnapshot Pair(SupplierProduct pair) => new(pair.Id, pair.ProductId,
        pair.SupplierId, pair.IsActive, pair.NextItemSequence, pair.Version);

    public static void IdentityChanged(IBusinessAuditWriter audit, Product product,
        ProductIdentityAuditSnapshot before, Guid actor, Guid correlation, string context) =>
        audit.Record("PRODUCT_IDENTITY_CHANGED", "PRODUCT", product.Id, actor, correlation,
            JsonSerializer.Serialize(new { Context = context, Before = before, After = Identity(product) }));

    public static void PairChanged(IBusinessAuditWriter audit, SupplierProduct pair,
        SupplierProductAuditSnapshot? before, Guid actor, Guid correlation, string context) =>
        audit.Record(before is null ? "SUPPLIER_PRODUCT_CREATED" : pair.IsActive ? "SUPPLIER_PRODUCT_ACTIVATED" : "SUPPLIER_PRODUCT_DEACTIVATED",
            "SUPPLIER_PRODUCT", pair.Id, actor, correlation,
            JsonSerializer.Serialize(new { Context = context, Before = before, After = Pair(pair) }));
}
