using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class PhysicalUnitCreationAuthority : IPhysicalUnitCreationAuthority
{
    private readonly EdgeRetailsDbContext _db;
    private readonly ITraceabilityRepository _traceability;
    private readonly IInventoryRepository _inventory;
    private readonly ISequenceHighWaterService _highWater;
    private readonly IResourceLock _resourceLock;
    private readonly IIdGenerator _ids;
    private readonly IClock _clock;

    public PhysicalUnitCreationAuthority(
        EdgeRetailsDbContext db,
        ITraceabilityRepository traceability,
        IInventoryRepository inventory,
        ISequenceHighWaterService highWater,
        IResourceLock resourceLock,
        IIdGenerator ids,
        IClock clock)
    {
        _db = db;
        _traceability = traceability;
        _inventory = inventory;
        _highWater = highWater;
        _resourceLock = resourceLock;
        _ids = ids;
        _clock = clock;
    }

    public async Task<Result<IReadOnlyList<InventoryUnit>>> CreateAsync(
        Guid supplierId,
        Guid productId,
        IReadOnlyList<PhysicalUnitCreationEntry> entries,
        CancellationToken cancellationToken)
    {
        if (supplierId == Guid.Empty || productId == Guid.Empty || entries.Count == 0)
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.physical_unit_intent_invalid",
                "Supplier, product and at least one physical-unit intent are required.");
        }

        await _resourceLock.AcquireAsync("product", productId, cancellationToken);
        await _resourceLock.AcquireAsync(
            "supplier-product",
            $"{supplierId:D}:{productId:D}",
            cancellationToken);

        var supplier = await _db.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == supplierId, cancellationToken);
        var product = await _db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);
        if (supplier is null || !supplier.IsActive || string.IsNullOrWhiteSpace(supplier.DealerCode))
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.supplier_tracking_identity_missing",
                "An active supplier with a permanent DealerCode is required.");
        }
        if (product is null || !product.IsActive || string.IsNullOrWhiteSpace(product.Sku))
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.product_tracking_identity_missing",
                "An active product with a canonical ProductCode/SKU is required.");
        }

        var normalized = new List<(PhysicalUnitCreationEntry Entry, string? Serial, string? Imei1, string? Imei2)>(entries.Count);
        var serials = new HashSet<string>(StringComparer.Ordinal);
        var imeis = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            string? serial;
            string? imei1;
            string? imei2;
            try
            {
                serial = product.SerialTrackingEnabled
                    ? IdentityNormalizationRules.NormalizeOptionalSerialNumber(entry.SerialNumber)
                    : null;
                imei1 = product.ImeiTrackingEnabled
                    ? IdentityNormalizationRules.NormalizeOptionalImei(entry.Imei1)
                    : null;
                imei2 = product.ImeiTrackingEnabled
                    ? IdentityNormalizationRules.NormalizeOptionalImei(entry.Imei2)
                    : null;
            }
            catch (EdgeRetails.Domain.Common.BusinessRuleException ex)
            {
                return Result<IReadOnlyList<InventoryUnit>>.Failure(ex.Code, ex.Message);
            }

            if (product.SerialTrackingEnabled && serial is null)
            {
                return Result<IReadOnlyList<InventoryUnit>>.Failure("identity.serial_required", "Serial number is required for this product.");
            }
            if (product.ImeiTrackingEnabled && imei1 is null)
            {
                return Result<IReadOnlyList<InventoryUnit>>.Failure("identity.imei_required", "IMEI1 is required for this product.");
            }
            if (serial is not null && !serials.Add(serial))
            {
                return Result<IReadOnlyList<InventoryUnit>>.Failure("identity.serial_duplicate", "Duplicate normalized Serial in physical-unit batch.");
            }
            if (imei1 is not null && !imeis.Add(imei1))
            {
                return Result<IReadOnlyList<InventoryUnit>>.Failure("identity.imei_duplicate", "Duplicate normalized IMEI in physical-unit batch.");
            }
            if (imei2 is not null && !imeis.Add(imei2))
            {
                return Result<IReadOnlyList<InventoryUnit>>.Failure("identity.imei_duplicate", "Duplicate normalized IMEI in physical-unit batch.");
            }


            normalized.Add((entry, serial, imei1, imei2));
        }

        if (product.TrackingMode is not (TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container))
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.physical_tracking_required",
                "Physical units require the locked product to use a physical tracking policy.");
        }

        // The standalone path acquires every identity before taking a row lock.
        // Command callers already hold their complete globally sorted logical
        // lock set. PostgresOperationLock treats held keys as transaction-local
        // no-ops rather than reacquiring lower-ranked resources in the row phase.
        foreach (var identityKey in serials.Select(x => $"SERIAL:{x}")
            .Concat(imeis.Select(x => $"IMEI:{x}"))
            .OrderBy(x => x, StringComparer.Ordinal))
        {
            await _resourceLock.AcquireAsync("inventory-identity", identityKey, cancellationToken);
        }

        var discoveredPolicy = (product.TrackingMode, product.SerialTrackingEnabled, product.ImeiTrackingEnabled);
        if (_db.Database.IsRelational())
        {
            product = await _db.Products
                .FromSqlInterpolated($"SELECT * FROM catalog.products WHERE id = {productId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            supplier = await _db.Suppliers
                .FromSqlInterpolated($"SELECT * FROM parties.suppliers WHERE id = {supplierId} FOR SHARE")
                .SingleOrDefaultAsync(cancellationToken);
            if (product is not null)
            {
                await _db.Entry(product).ReloadAsync(cancellationToken);
                if (product.CompanyId is Guid companyId)
                {
                    await _db.Companies.FromSqlInterpolated($"SELECT * FROM catalog.companies WHERE id = {companyId} FOR SHARE")
                        .LoadAsync(cancellationToken);
                }
                if (product.CategoryId is Guid categoryId)
                {
                    await _db.Categories.FromSqlInterpolated($"SELECT * FROM catalog.categories WHERE id = {categoryId} FOR SHARE")
                        .LoadAsync(cancellationToken);
                }
            }
            if (supplier is not null)
            {
                await _db.Entry(supplier).ReloadAsync(cancellationToken);
            }
        }
        if (supplier is null || !supplier.IsActive || string.IsNullOrWhiteSpace(supplier.DealerCode))
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.supplier_tracking_identity_missing",
                "An active supplier with a permanent DealerCode is required.");
        }
        if (product is null || !product.IsActive || string.IsNullOrWhiteSpace(product.Sku))
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.product_tracking_identity_missing",
                "An active product with a canonical ProductCode/SKU is required.");
        }
        if ((product.TrackingMode, product.SerialTrackingEnabled, product.ImeiTrackingEnabled) != discoveredPolicy)
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.physical_unit_policy_changed_retry",
                "Product tracking policy changed during preparation. Retry the complete operation.");
        }

        // Current ownership is checked only after logical identity acquisition.
        // Database uniqueness remains the final authority against bypasses.
        foreach (var value in normalized)
        {
            if (await _inventory.InventoryIdentityExistsAsync(value.Serial, value.Imei1, value.Imei2, cancellationToken))
            {
                return Result<IReadOnlyList<InventoryUnit>>.Failure(
                    "inventory.identity_duplicate",
                    "Serial or IMEI is already assigned to another physical unit.");
            }
        }

        var supplierProduct = _db.SupplierProducts.Local.SingleOrDefault(
            x => x.SupplierId == supplierId && x.ProductId == productId)
            ?? await _traceability.GetSupplierProductForUpdateAsync(
                supplierId, productId, cancellationToken);
        if (supplierProduct is not null && !supplierProduct.IsActive)
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.supplier_product_inactive",
                "Supplier/Product sequence authority is inactive.");
        }
        if (supplierProduct is null)
        {
            supplierProduct = new SupplierProduct
            {
                Id = _ids.NewId(),
                SupplierId = supplierId,
                ProductId = productId,
                NextItemSequence = 1,
                IsActive = true,
                CreatedAt = _clock.UtcNow,
                UpdatedAt = _clock.UtcNow,
                Version = 1
            };
            _traceability.AddSupplierProduct(supplierProduct);
        }

        var machine = _highWater.GetSupplierProductHighWater(supplierId, productId);
        if (machine > supplierProduct.NextItemSequence)
        {
            supplierProduct.NextItemSequence = machine;
        }

        var firstSequence = supplierProduct.NextItemSequence;
        supplierProduct.NextItemSequence = checked(firstSequence + entries.Count);
        supplierProduct.UpdatedAt = _clock.UtcNow;
        supplierProduct.Version++;
        _highWater.RecordSupplierProductHighWater(
            supplierId, productId, supplierProduct.NextItemSequence);

        var dealerSnapshot = supplier.DealerCode.Trim().ToUpperInvariant();
        var skuSnapshot = TraceabilityCodeRules.NormalizeSku(product.Sku);
        var created = new List<InventoryUnit>(entries.Count);
        for (var i = 0; i < normalized.Count; i++)
        {
            var value = normalized[i];
            var sequence = checked(firstSequence + i);
            var unit = new InventoryUnit
            {
                Id = _ids.NewId(),
                ProductId = productId,
                SupplierProductId = supplierProduct.Id,
                OriginType = value.Entry.OriginType,
                ItemSequence = sequence,
                TrackingCode = TraceabilityCodeRules.BuildTrackingCode(dealerSnapshot, skuSnapshot, sequence),
                SupplierCodeSnapshot = dealerSnapshot,
                ProductSkuSnapshot = skuSnapshot,
                SerialNumber = value.Serial,
                Imei1 = value.Imei1,
                Imei2 = value.Imei2,
                Status = value.Entry.Status,
                AcquisitionCost = value.Entry.AcquisitionCost,
                InventoryLotId = value.Entry.InventoryLotId,
                SourcePurchaseItemId = value.Entry.SourcePurchaseItemId,
                SourceWarrantyClaimItemId = value.Entry.SourceWarrantyClaimItemId,
                SourceWarrantyCaseId = value.Entry.SourceWarrantyCaseId,
                SourceStockAdjustmentItemId = value.Entry.SourceStockAdjustmentItemId,
                CreatedAt = _clock.UtcNow,
                Version = 1
            };
            try
            {
                unit.ValidateOriginInvariants();
            }
            catch (EdgeRetails.Domain.Common.BusinessRuleException ex)
            {
                return Result<IReadOnlyList<InventoryUnit>>.Failure(ex.Code, ex.Message);
            }

            _inventory.AddInventoryUnit(unit);
            created.Add(unit);
        }

        return Result<IReadOnlyList<InventoryUnit>>.Success(created);
    }
}
