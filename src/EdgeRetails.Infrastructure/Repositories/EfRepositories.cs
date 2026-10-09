using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class CatalogRepository : ICatalogRepository
{
    private readonly EdgeRetailsDbContext _db;

    public CatalogRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<Product?> GetProductAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        _db.Products.SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);

    public async Task<Product?> GetProductForUpdateAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .FromSqlInterpolated(
                $"SELECT * FROM catalog.products WHERE id = {productId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        // EF identity resolution can return a master tracked before this lock.
        // Policy-dependent preparation must observe the row that won the boundary.
        if (product is not null)
        {
            await _db.Entry(product).ReloadAsync(cancellationToken);
        }
        return product;
    }

    public Task<Product?> GetProductBySkuAsync(
        string normalizedSku,
        CancellationToken cancellationToken) =>
        _db.Products.SingleOrDefaultAsync(
            x => x.Sku == normalizedSku,
            cancellationToken);

    public async Task<IReadOnlyList<Product>> GetProductsAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = _db.Products.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<Company?> GetCompanyAsync(
        Guid companyId,
        CancellationToken cancellationToken) =>
        _db.Companies.SingleOrDefaultAsync(x => x.Id == companyId, cancellationToken);

    public Task<Company?> GetCompanyForUpdateAsync(
        Guid companyId,
        CancellationToken cancellationToken) =>
        _db.Companies
            .FromSqlInterpolated(
                $"SELECT * FROM catalog.companies WHERE id = {companyId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Company?> GetCompanyByCodeAsync(
        string normalizedCode,
        CancellationToken cancellationToken) =>
        _db.Companies.SingleOrDefaultAsync(
            x => x.Code == normalizedCode,
            cancellationToken);

    public Task<Company?> GetCompanyByNameAsync(
        string name,
        CancellationToken cancellationToken) =>
        _db.Companies.SingleOrDefaultAsync(
            x => x.Name.ToLower() == name.ToLower(),
            cancellationToken);

    public async Task<IReadOnlyList<Company>> GetCompaniesAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = _db.Companies.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public Task<bool> IsCompanyInUseByActiveProductAsync(
        Guid companyId,
        CancellationToken cancellationToken) =>
        _db.Products.AnyAsync(
            x => x.IsActive && x.CompanyId == companyId,
            cancellationToken);

    public Task<Category?> GetCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken) =>
        _db.Categories.SingleOrDefaultAsync(x => x.Id == categoryId, cancellationToken);

    public Task<Category?> GetCategoryForUpdateAsync(
        Guid categoryId,
        CancellationToken cancellationToken) =>
        _db.Categories
            .FromSqlInterpolated(
                $"SELECT * FROM catalog.categories WHERE id = {categoryId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Category?> GetCategoryByIdentitySymbolAsync(
        string normalizedSymbol,
        CancellationToken cancellationToken) =>
        _db.Categories.SingleOrDefaultAsync(
            x => x.IdentitySymbol == normalizedSymbol,
            cancellationToken);

    public Task<Category?> GetCategoryByNameAsync(
        string name,
        CancellationToken cancellationToken) =>
        _db.Categories.SingleOrDefaultAsync(
            x => x.Name.ToLower() == name.ToLower(),
            cancellationToken);

    public async Task<IReadOnlyList<Category>> GetCategoriesAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = _db.Categories.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public Task<bool> IsCategoryInUseByActiveProductAsync(
        Guid categoryId,
        CancellationToken cancellationToken) =>
        _db.Products.AnyAsync(
            x => x.IsActive && x.CategoryId == categoryId,
            cancellationToken);

    public Task<Unit?> GetUnitAsync(
        Guid unitId,
        CancellationToken cancellationToken) =>
        _db.Units.SingleOrDefaultAsync(x => x.Id == unitId, cancellationToken);

    public Task<Unit?> GetUnitForUpdateAsync(
        Guid unitId,
        CancellationToken cancellationToken) =>
        _db.Units
            .FromSqlInterpolated(
                $"SELECT * FROM catalog.units WHERE id = {unitId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<Unit>> GetUnitsAsync(
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var query = _db.Units.AsQueryable();
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        return await query.OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<bool> IsUnitInUseByActiveCatalogAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        if (await _db.Products.AnyAsync(
                x => x.IsActive && x.BaseUnitId == unitId,
                cancellationToken))
        {
            return true;
        }

        return await _db.ProductUnits.AnyAsync(
            x => x.IsActive && x.UnitId == unitId,
            cancellationToken);
    }

    public Task<ProductUnit?> GetProductUnitSnapshotAsync(Guid productUnitId, CancellationToken cancellationToken) =>
        _db.ProductUnits.AsNoTracking().SingleOrDefaultAsync(x => x.Id == productUnitId, cancellationToken);

    public Task<ProductUnit?> GetProductUnitAsync(
        Guid productUnitId,
        CancellationToken cancellationToken) =>
        _db.ProductUnits.SingleOrDefaultAsync(
            x => x.Id == productUnitId,
            cancellationToken);

    public Task<ProductUnit?> GetProductUnitAsync(
        Guid productId,
        Guid unitId,
        CancellationToken cancellationToken) =>
        _db.ProductUnits.SingleOrDefaultAsync(
            x => x.ProductId == productId && x.UnitId == unitId,
            cancellationToken);

    public async Task<IReadOnlyList<ProductUnit>> GetProductUnitsAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        await _db.ProductUnits
            .Where(x => x.ProductId == productId)
            .OrderBy(x => x.UnitId)
            .ToListAsync(cancellationToken);

    public Task<ProductUnitBarcode?> GetBarcodeAsync(
        string barcode,
        CancellationToken cancellationToken) =>
        _db.ProductUnitBarcodes.SingleOrDefaultAsync(
            x => x.Barcode == barcode && x.IsActive,
            cancellationToken);

    public async Task<IReadOnlyList<Product>> GetActiveProductsAsync(
        StocktakeScope scope,
        Guid? categoryId,
        CancellationToken cancellationToken)
    {
        var query = _db.Products.Where(x => x.IsActive);
        if (scope == StocktakeScope.Category)
        {
            query = query.Where(x => x.CategoryId == categoryId);
        }

        return await query
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    public void AddCompany(Company company) =>
        _db.Companies.Add(company);

    public void AddCategory(Category category) =>
        _db.Categories.Add(category);

    public void AddUnit(Unit unit) =>
        _db.Units.Add(unit);

    public void AddProduct(Product product) =>
        _db.Products.Add(product);

    public void AddProductUnit(ProductUnit productUnit) =>
        _db.ProductUnits.Add(productUnit);

    public void AddBarcode(ProductUnitBarcode barcode) =>
        _db.ProductUnitBarcodes.Add(barcode);
}

public sealed class InventoryRepository : IInventoryRepository
{
    private readonly EdgeRetailsDbContext _db;

    public InventoryRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<StockBalance?> GetStockBalanceAsync(Guid productId, CancellationToken cancellationToken) =>
        _db.StockBalances.AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == productId, cancellationToken);

    public Task<StockBalance?> GetStockBalanceForUpdateAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        _db.StockBalances
            .FromSqlInterpolated(
                $"SELECT * FROM inventory.stock_balances WHERE product_id = {productId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ProductCostState?> GetCostStateForUpdateAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var tracked = _db.ProductCostStates.Local
            .SingleOrDefault(x => x.ProductId == productId);

        return tracked is not null
            ? Task.FromResult<ProductCostState?>(tracked)
            : _db.ProductCostStates
                .FromSqlInterpolated(
                    $"SELECT * FROM inventory.cost_states WHERE product_id = {productId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LotBucketPosition>> GetLotBucketPositionsForUpdateAsync(
        Guid productId,
        InventoryBucket bucket,
        CancellationToken cancellationToken)
    {
        var balances = await _db.InventoryLotBucketBalances
            .FromSqlInterpolated(
                $"""
                 SELECT b.*
                 FROM inventory.lot_bucket_balances b
                 INNER JOIN inventory.lots l ON l.id = b.lot_id
                 WHERE l.product_id = {productId}
                   AND b.stock_bucket = {(int)bucket}
                   AND b.quantity > 0
                 ORDER BY l.created_at, l.id
                 FOR UPDATE OF b
                 """)
            .ToListAsync(cancellationToken);

        if (balances.Count == 0)
        {
            return Array.Empty<LotBucketPosition>();
        }

        var lotIds = balances.Select(x => x.LotId).Distinct().ToArray();
        var lots = await _db.InventoryLots
            .Where(x => lotIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return balances
            .Select(balance => new LotBucketPosition(lots[balance.LotId], balance))
            .OrderBy(x => x.Lot.CreatedAt)
            .ThenBy(x => x.Lot.Id)
            .ToArray();
    }

    public async Task<IReadOnlyList<LotBucketPosition>> GetPurchaseItemLotPositionsForUpdateAsync(
        Guid purchaseItemId,
        InventoryBucket bucket,
        CancellationToken cancellationToken)
    {
        var balances = await _db.InventoryLotBucketBalances
            .FromSqlInterpolated(
                $"""
                 SELECT b.*
                 FROM inventory.lot_bucket_balances b
                 INNER JOIN inventory.lots l ON l.id = b.lot_id
                 WHERE l.purchase_item_id = {purchaseItemId}
                   AND b.stock_bucket = {(int)bucket}
                   AND b.quantity > 0
                 ORDER BY l.created_at, l.id
                 FOR UPDATE OF b
                 """)
            .ToListAsync(cancellationToken);

        if (balances.Count == 0)
        {
            return Array.Empty<LotBucketPosition>();
        }

        var lotIds = balances.Select(x => x.LotId).Distinct().ToArray();
        var lots = await _db.InventoryLots
            .Where(x => lotIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return balances
            .Select(balance => new LotBucketPosition(lots[balance.LotId], balance))
            .OrderBy(x => x.Lot.CreatedAt)
            .ThenBy(x => x.Lot.Id)
            .ToArray();
    }

    public Task<InventoryLotBucketBalance?> GetLotBucketBalanceForUpdateAsync(
        Guid lotId,
        InventoryBucket bucket,
        CancellationToken cancellationToken) =>
        _db.InventoryLotBucketBalances
            .FromSqlInterpolated(
                $"SELECT * FROM inventory.lot_bucket_balances WHERE lot_id = {lotId} AND stock_bucket = {(int)bucket} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<InventoryLot?> GetInventoryLotForUpdateAsync(
        Guid lotId,
        CancellationToken cancellationToken)
    {
        var provisional = _db.InventoryLots.Local.SingleOrDefault(x => x.Id == lotId);
        if (provisional is not null && _db.Entry(provisional).State == EntityState.Added)
        {
            return Task.FromResult<InventoryLot?>(provisional);
        }
        return _db.InventoryLots
            .FromSqlInterpolated(
                $"SELECT * FROM inventory.lots WHERE id = {lotId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<decimal> GetPhysicalUnitBaseQuantitySnapshotAsync(
        InventoryUnit unit, CancellationToken cancellationToken)
        => await GetPhysicalUnitBaseQuantitySnapshotAsync(unit, new HashSet<Guid>(), cancellationToken);

    private Task<decimal> GetPhysicalUnitBaseQuantitySnapshotAsync(
        InventoryUnit unit, HashSet<Guid> recoveryLots, CancellationToken cancellationToken)
        => GetPhysicalUnitBaseQuantitySnapshotAsync(unit, unit.InventoryLotId ?? Guid.Empty, recoveryLots, cancellationToken);

    private async Task<decimal> GetPhysicalUnitBaseQuantitySnapshotAsync(
        InventoryUnit unit, Guid lotId, HashSet<Guid> recoveryLots, CancellationToken cancellationToken)
    {
        var mode = await _db.Products.AsNoTracking().Where(x => x.Id == unit.ProductId)
            .Select(x => x.TrackingMode).SingleAsync(cancellationToken);
        if (mode != TrackingMode.Container)
        {
            return 1m;
        }

        var lot = await _db.InventoryLots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == lotId, cancellationToken);
        if (lot is null)
        {
            throw new BusinessRuleException("inventory.physical_quantity_reconciliation_required",
                "Container has no authoritative received-lot quantity snapshot. Reconcile its history before transition.");
        }

        if (await IsRecoveryLotAsync(lot, cancellationToken))
        {
            var carryingMovement = await GetInventoryMovementEvidenceAsync(lot.SourceMovementId, cancellationToken)
                ?? throw RecoveryLotInvalid();
            var source = await ValidateRecoveredLotAsync(unit, lot, carryingMovement, recoveryLots, cancellationToken);
            return source.BaseQuantity;
        }

        // The received movement's effects and exact-unit links survive later
        // returns/replacements. Current lot balance or order quantity cannot
        // establish the size of an original pack after partial intake.
        var received = await _db.InventoryMovementEffects.AsNoTracking()
            .Where(x => x.MovementId == lot.SourceMovementId && x.QuantityDelta > 0m)
            .SumAsync(x => x.QuantityDelta, cancellationToken);
        var count = await _db.InventoryMovementUnits.AsNoTracking()
            .Where(x => x.MovementId == lot.SourceMovementId)
            .Select(x => x.InventoryUnitId).Distinct().CountAsync(cancellationToken);
        if (received <= 0m || count == 0)
        {
            throw new BusinessRuleException("inventory.physical_quantity_reconciliation_required",
                "Container received movement has no authoritative physical quantity. Reconcile its history before transition.");
        }
        var quantity = received / count;
        if (decimal.Abs(unit.AcquisitionCost - lot.EffectiveUnitCost * quantity) > 0.000001m &&
            !await HasFrozenCompletedReceiptAllocationAsync(unit, lot, lotId, received, count, quantity, cancellationToken))
        {
            throw new BusinessRuleException("inventory.physical_cost_reconciliation_required",
                "Container acquisition cost is inconsistent with its received pack snapshot. Reconcile historical cost before transition; no value was rewritten.");
        }
        return quantity;
    }

    public async Task<decimal> GetPhysicalUnitCarryingValueSnapshotAsync(
        InventoryUnit unit, CancellationToken cancellationToken)
    {
        if (unit.InventoryLotId is not Guid lotId)
        {
            return unit.AcquisitionCost;
        }
        var lot = await _db.InventoryLots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == lotId, cancellationToken);
        if (lot is null)
        {
            return unit.AcquisitionCost;
        }
        if (!await IsRecoveryLotAsync(lot, cancellationToken))
        {
            return unit.AcquisitionCost;
        }
        var evidence = await GetInventoryMovementEvidenceAsync(lot.SourceMovementId, cancellationToken)
            ?? throw RecoveryLotInvalid();
        var source = await ValidateRecoveredLotAsync(unit, lot, evidence, new HashSet<Guid>(), cancellationToken);
        return source.RemovedValue;
    }

    private Task<bool> IsRecoveryLotAsync(InventoryLot lot, CancellationToken cancellationToken) =>
        _db.InventoryMovements.AsNoTracking().AnyAsync(x =>
            x.Id == lot.SourceMovementId && x.ReferenceType == "InventoryLossRecoveryGain", cancellationToken);

    private async Task<MissingRecoverySource> ValidateRecoveredLotAsync(
        InventoryUnit unit, InventoryLot lot, InventoryMovementEvidence found,
        HashSet<Guid> recoveryLots, CancellationToken cancellationToken)
    {
        if (recoveryLots.Count >= 128 || !recoveryLots.Add(lot.Id))
        {
            throw RecoveryLotInvalid();
        }
        var movement = found.Movement;
        if (movement.MovementType != InventoryMovementType.StockAdjustment ||
            movement.ProductId != unit.ProductId || lot.ProductId != unit.ProductId ||
            movement.ReferenceId is not Guid missingId || movement.RecognizedLossAmount != 0m ||
            found.Units.Count != 1 || found.Effects.Count != 1 || found.Consumptions.Count != 0)
        {
            throw RecoveryLotInvalid();
        }
        var link = found.Units[0];
        var effect = found.Effects[0];
        if (link.MovementId != movement.Id || link.InventoryUnitId != unit.Id ||
            link.FromStatus != InventoryUnitStatus.Missing ||
            link.ToStatus is not (InventoryUnitStatus.InStock or InventoryUnitStatus.Damaged or InventoryUnitStatus.Defective) ||
            effect.MovementId != movement.Id || effect.QuantityDelta <= 0m || effect.QuantityBefore < 0m ||
            effect.QuantityBefore + effect.QuantityDelta != effect.QuantityAfter ||
            InventoryUnitAccountingPolicy.GetRule(link.ToStatus).AuthoritativeBucket != effect.StockBucket)
        {
            throw RecoveryLotInvalid();
        }
        var missing = await GetInventoryMovementEvidenceAsync(missingId, cancellationToken)
            ?? throw RecoveryLotInvalid();
        var source = await FoundRecoveryAuthority.ValidateSourceAsync(this, unit, missing, cancellationToken);
        FoundRecoveryAuthority.ValidateFound(found, source);
        var references = await GetMovementsByReferenceAsync("InventoryLossRecoveryGain", missingId, cancellationToken);
        if (references.Count != 1 || references[0].Movement.Id != movement.Id ||
            source.UnitId != unit.Id || source.BaseQuantity != effect.QuantityDelta ||
            lot.ReceivedQuantity != source.BaseQuantity || lot.PurchaseItemId != unit.SourcePurchaseItemId ||
            lot.OriginalUnitCost != decimal.Round(source.RemovedValue / source.BaseQuantity, 6, MidpointRounding.AwayFromZero) ||
            lot.EffectiveUnitCost != lot.OriginalUnitCost)
        {
            throw RecoveryLotInvalid();
        }

        // Prove the frozen pack through the source lot, retaining the ordinary
        // receipt allocation and acquisition corruption checks at its origin.
        var quantity = await GetPhysicalUnitBaseQuantitySnapshotAsync(unit, source.LotId, recoveryLots, cancellationToken);
        if (quantity != source.BaseQuantity)
        {
            throw RecoveryLotInvalid();
        }
        return source;
    }

    private static BusinessRuleException RecoveryLotInvalid() => new(
        "inventory.recovery_lot_reconciliation_required",
        "Recovered carrying lot does not conclusively reconcile to its exact Missing source and original physical quantity.");

    private async Task<bool> HasFrozenCompletedReceiptAllocationAsync(
        InventoryUnit unit, InventoryLot lot, Guid effectiveLotId, decimal received, int count,
        decimal quantity, CancellationToken cancellationToken)
    {
        // A completed purchase absorbs its exact landed residual into physical
        // acquisition amounts. Prove that allocation from original receipt
        // links and frozen line value; never enlarge the corruption tolerance.
        if (unit.SourcePurchaseItemId is not Guid itemId || lot.PurchaseItemId != itemId ||
            (unit.InventoryLotId != lot.Id && effectiveLotId != lot.Id))
        {
            return false;
        }
        // Returns create new carrying lots. Recover the unique original intake
        // from its immutable creation link, rather than counting returned lots
        // as another purchase receipt or allocating over the return's unit set.
        var receipts = await _db.InventoryLots.AsNoTracking().Where(x =>
            x.PurchaseItemId == itemId && x.ProductId == unit.ProductId &&
            _db.InventoryMovements.Any(m => m.Id == x.SourceMovementId &&
                m.MovementType == InventoryMovementType.PurchaseIn) &&
            _db.InventoryMovementUnits.Any(link => link.MovementId == x.SourceMovementId &&
                link.InventoryUnitId == unit.Id && link.FromStatus == null))
            .ToArrayAsync(cancellationToken);
        if (receipts.Length != 1)
        {
            return false;
        }
        var receipt = receipts[0];
        if (lot.Id != receipt.Id && lot.EffectiveUnitCost !=
            decimal.Round(unit.AcquisitionCost / quantity, 6, MidpointRounding.AwayFromZero))
        {
            return false;
        }
        received = await _db.InventoryMovementEffects.AsNoTracking()
            .Where(x => x.MovementId == receipt.SourceMovementId && x.QuantityDelta > 0m)
            .SumAsync(x => x.QuantityDelta, cancellationToken);
        count = await _db.InventoryMovementUnits.AsNoTracking()
            .Where(x => x.MovementId == receipt.SourceMovementId && x.FromStatus == null)
            .Select(x => x.InventoryUnitId).Distinct().CountAsync(cancellationToken);
        if (received <= 0m || count == 0 || receipt.ReceivedQuantity != received || received / count != quantity)
        {
            return false;
        }
        var item = await _db.PurchaseItems.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == itemId, cancellationToken);
        if (item is null || item.ProductId != unit.ProductId || item.EffectiveLineCost < 0m ||
            item.EnteredQuantity <= 0m || quantity <= 0m || decimal.Truncate(quantity) != quantity ||
            item.FactorToBaseSnapshot != quantity || item.BaseQuantity != item.EnteredQuantity * quantity)
        {
            return false;
        }
        var totalReceived = await _db.InventoryLots.AsNoTracking().Where(x => x.PurchaseItemId == itemId &&
            _db.InventoryMovements.Any(m => m.Id == x.SourceMovementId && m.MovementType == InventoryMovementType.PurchaseIn))
            .SumAsync(x => x.ReceivedQuantity, cancellationToken);
        var originalUnits = _db.InventoryUnits.AsNoTracking()
            .Where(x => x.SourcePurchaseItemId == itemId && x.ProductId == unit.ProductId);
        var totalAcquisition = await originalUnits.SumAsync(x => x.AcquisitionCost, cancellationToken);
        if (totalReceived != item.BaseQuantity || totalAcquisition != item.EffectiveLineCost)
        {
            return false;
        }
        var receiptUnits = originalUnits.Where(x => _db.InventoryMovementUnits.Any(link =>
            link.MovementId == receipt.SourceMovementId && link.InventoryUnitId == x.Id && link.FromStatus == null));
        if (await receiptUnits.CountAsync(cancellationToken) != count)
        {
            return false;
        }
        var selected = await receiptUnits.SingleOrDefaultAsync(x => x.Id == unit.Id, cancellationToken);
        if (selected is null || selected.AcquisitionCost != unit.AcquisitionCost)
        {
            return false;
        }
        var receiptAcquisition = await receiptUnits.SumAsync(x => x.AcquisitionCost, cancellationToken);
        var priorAcquisition = totalAcquisition - receiptAcquisition;
        var allocatedReceiptValue = item.EffectiveLineCost - priorAcquisition;
        if (allocatedReceiptValue < 0m ||
            receipt.EffectiveUnitCost != decimal.Round(allocatedReceiptValue / received, 6, MidpointRounding.AwayFromZero))
        {
            return false;
        }
        // ItemSequence retains creation order even when prior reservations left
        // gaps. Reconstruct the same cumulative six-place allocation as intake.
        var index = await receiptUnits.CountAsync(x => x.ItemSequence < selected.ItemSequence, cancellationToken);
        var expected = decimal.Round(allocatedReceiptValue * (index + 1m) / count, 6, MidpointRounding.AwayFromZero) -
            decimal.Round(allocatedReceiptValue * index / count, 6, MidpointRounding.AwayFromZero);
        return unit.AcquisitionCost == expected;
    }

    public Task<bool> HasPurchaseItemConsumptionAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken) =>
        (from consumption in _db.InventoryLotConsumptions
         join lot in _db.InventoryLots on consumption.LotId equals lot.Id
         where lot.PurchaseItemId == purchaseItemId &&
               consumption.Quantity > 0
         select consumption.Id).AnyAsync(cancellationToken);

    public async Task<decimal> GetPurchaseItemReceivedBaseQuantityAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken) =>
        await _db.InventoryLots
            .Where(x => x.PurchaseItemId == purchaseItemId &&
                _db.InventoryMovements.Any(m => m.Id == x.SourceMovementId &&
                    m.MovementType == InventoryMovementType.PurchaseIn))
            .SumAsync(x => (decimal?)x.ReceivedQuantity, cancellationToken) ?? 0m;

    public async Task<decimal> GetPurchaseItemReceivedCarryingValueAsync(
        Guid purchaseItemId,
        bool physical,
        CancellationToken cancellationToken)
    {
        if (physical)
        {
            return await _db.InventoryUnits.Where(x => x.SourcePurchaseItemId == purchaseItemId)
                .SumAsync(x => (decimal?)x.AcquisitionCost, cancellationToken) ?? 0m;
        }

        // Original receipt provenance survives consumption and bucket movement.
        // Return lots retain PurchaseItemId for origin, but are not new intake.
        // Do not restrict this authority to remaining positive lot balances.
        var lots = await _db.InventoryLots.AsNoTracking()
            .Where(x => x.PurchaseItemId == purchaseItemId &&
                _db.InventoryMovements.Any(m => m.Id == x.SourceMovementId &&
                    m.MovementType == InventoryMovementType.PurchaseIn))
            .Select(x => new { x.ReceivedQuantity, x.EffectiveUnitCost })
            .ToArrayAsync(cancellationToken);
        return lots.Sum(x => decimal.Round(x.ReceivedQuantity * x.EffectiveUnitCost, 6, MidpointRounding.AwayFromZero));
    }

    public async Task<IReadOnlyList<InventoryLotConsumption>> GetMovementLotConsumptionsAsync(
        Guid movementId,
        CancellationToken cancellationToken) =>
        await _db.InventoryLotConsumptions
            .Where(x => x.MovementId == movementId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InventoryUnit>> GetInventoryUnitsForUpdateAsync(
        Guid productId,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        CancellationToken cancellationToken)
    {
        if (inventoryUnitIds.Count == 0)
        {
            return Array.Empty<InventoryUnit>();
        }

        var ids = inventoryUnitIds.ToArray();
        return await _db.InventoryUnits
            .FromSqlInterpolated(
                $"SELECT * FROM inventory.units WHERE product_id = {productId} AND id = ANY({ids}) FOR UPDATE")
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryUnit>> GetSellableInventoryUnitsAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        await _db.InventoryUnits
            .Where(x => x.ProductId == productId && x.Status == InventoryUnitStatus.InStock)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public async Task<bool> InventoryIdentityExistsAsync(
        string? serialNumber,
        string? imei1,
        string? imei2,
        CancellationToken cancellationToken)
    {
        var serial = IdentityNormalizationRules.NormalizeOptionalSerialNumber(serialNumber);
        var normalizedImei1 = IdentityNormalizationRules.NormalizeOptionalImei(imei1);
        var normalizedImei2 = IdentityNormalizationRules.NormalizeOptionalImei(imei2);

        var normalized = new[] { serial, normalizedImei1, normalizedImei2 }
            .Where(x => x is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length > 0 && await _db.InventoryUnitIdentityOwnerships
            .AnyAsync(x => normalized.Contains(x.NormalizedValue), cancellationToken);
    }

    public async Task ReleaseManufacturerIdentityOwnershipForReceiptVoidAsync(
        IReadOnlyCollection<Guid> inventoryUnitIds,
        CancellationToken cancellationToken)
    {
        if (inventoryUnitIds.Count == 0)
        {
            return;
        }

        var ids = inventoryUnitIds.Distinct().ToArray();
        await _db.InventoryUnitIdentityOwnerships
            .Where(x => _db.InventoryUnitIdentityClaims.Any(c =>
                ids.Contains(c.InventoryUnitId) && c.Id == x.InventoryUnitIdentityClaimId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<bool> IsProductBlockedByCountingStocktakeAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        (from item in _db.StocktakeItems
         join stocktake in _db.Stocktakes on item.StocktakeId equals stocktake.Id
         where item.ProductId == productId &&
               (stocktake.Status == StocktakeStatus.Counting ||
                stocktake.Status == StocktakeStatus.Review)
         select item.Id).AnyAsync(cancellationToken);

    public async Task<InventoryMovementEvidence?> GetInventoryMovementEvidenceAsync(
        Guid movementId, CancellationToken cancellationToken)
    {
        var movements = await _db.InventoryMovements.AsNoTracking()
            .Where(x => x.Id == movementId).ToArrayAsync(cancellationToken);
        var evidence = await ReadMovementEvidenceAsync(movements, cancellationToken);
        return evidence.SingleOrDefault();
    }

    public async Task<IReadOnlyList<InventoryMovementEvidence>> GetUnitMovementEvidenceAsync(
        Guid inventoryUnitId, CancellationToken cancellationToken)
    {
        var movements = await _db.InventoryMovements.AsNoTracking()
            .Where(x => _db.InventoryMovementUnits.Any(link =>
                link.MovementId == x.Id && link.InventoryUnitId == inventoryUnitId))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
            .ToArrayAsync(cancellationToken);
        return await ReadMovementEvidenceAsync(movements, cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryMovementEvidence>> GetMovementsByReferenceAsync(
        string referenceType, Guid referenceId, CancellationToken cancellationToken)
    {
        // This read is deliberately global: a conflicting reference must not
        // disappear because it links a different product or physical identity.
        var movements = await _db.InventoryMovements.AsNoTracking()
            .Where(x => x.ReferenceType == referenceType && x.ReferenceId == referenceId)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
            .ToArrayAsync(cancellationToken);
        return await ReadMovementEvidenceAsync(movements, cancellationToken);
    }

    private async Task<IReadOnlyList<InventoryMovementEvidence>> ReadMovementEvidenceAsync(
        IReadOnlyList<InventoryMovement> movements, CancellationToken cancellationToken)
    {
        if (movements.Count == 0)
        {
            return Array.Empty<InventoryMovementEvidence>();
        }
        var ids = movements.Select(x => x.Id).ToArray();
        // Load every linked row, including siblings of the selected identity.
        // Restricting these rows to one unit would hide grouped legacy sources.
        var units = await _db.InventoryMovementUnits.AsNoTracking()
            .Where(x => ids.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(cancellationToken);
        var effects = await _db.InventoryMovementEffects.AsNoTracking()
            .Where(x => ids.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(cancellationToken);
        var consumptions = await _db.InventoryLotConsumptions.AsNoTracking()
            .Where(x => ids.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(cancellationToken);
        return movements.Select(movement => new InventoryMovementEvidence(
            movement,
            units.Where(x => x.MovementId == movement.Id).ToArray(),
            effects.Where(x => x.MovementId == movement.Id).ToArray(),
            consumptions.Where(x => x.MovementId == movement.Id).ToArray())).ToArray();
    }

    public Task<InventoryMovement?> GetMovementByCorrelationIdAsync(
        Guid correlationId,
        CancellationToken cancellationToken) =>
        _db.InventoryMovements
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CorrelationId == correlationId, cancellationToken);

    public async Task<IReadOnlyList<InventoryUnit>> GetUnitsForMovementAsync(
        Guid movementId,
        CancellationToken cancellationToken) =>
        await (from mu in _db.InventoryMovementUnits.AsNoTracking()
               join u in _db.InventoryUnits.AsNoTracking() on mu.InventoryUnitId equals u.Id
               where mu.MovementId == movementId
               orderby u.ItemSequence, u.CreatedAt
               select u)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InventoryUnit>> GetUnitsByPurchaseItemAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken) =>
        await _db.InventoryUnits
            .AsNoTracking()
            .Where(x => x.SourcePurchaseItemId == purchaseItemId)
            .OrderBy(x => x.ItemSequence)
            .ThenBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> GetMovementUnitIdsByReferenceAsync(
        string referenceType,
        Guid referenceId,
        CancellationToken cancellationToken) =>
        await (from m in _db.InventoryMovements.AsNoTracking()
               join mu in _db.InventoryMovementUnits.AsNoTracking() on m.Id equals mu.MovementId
               where m.ReferenceType == referenceType && m.ReferenceId == referenceId
               select mu.InventoryUnitId)
            .Distinct()
            .ToListAsync(cancellationToken);

    public void AddStockBalance(StockBalance balance) => _db.StockBalances.Add(balance);
    public void AddCostState(ProductCostState costState) => _db.ProductCostStates.Add(costState);
    public void AddInventoryUnit(InventoryUnit unit)
    {
        var rawSerial = unit.SerialNumber;
        var rawImei1 = unit.Imei1;
        var rawImei2 = unit.Imei2;

        unit.SerialNumber = IdentityNormalizationRules.NormalizeOptionalSerialNumber(rawSerial);
        unit.Imei1 = IdentityNormalizationRules.NormalizeOptionalImei(rawImei1);
        unit.Imei2 = IdentityNormalizationRules.NormalizeOptionalImei(rawImei2);

        _db.InventoryUnits.Add(unit);
        AddIdentityClaim(
            unit,
            ManufacturerIdentifierType.Serial,
            ManufacturerIdentifierSlot.Serial,
            rawSerial,
            unit.SerialNumber);
        AddIdentityClaim(
            unit,
            ManufacturerIdentifierType.Imei,
            ManufacturerIdentifierSlot.Imei1,
            rawImei1,
            unit.Imei1);
        AddIdentityClaim(
            unit,
            ManufacturerIdentifierType.Imei,
            ManufacturerIdentifierSlot.Imei2,
            rawImei2,
            unit.Imei2);
    }

    private void AddIdentityClaim(
        InventoryUnit unit,
        ManufacturerIdentifierType type,
        ManufacturerIdentifierSlot slot,
        string? rawValue,
        string? normalizedValue)
    {
        if (normalizedValue is null)
        {
            return;
        }

        var claim = new InventoryUnitIdentityClaim
        {
            InventoryUnitId = unit.Id,
            IdentifierType = type,
            IdentifierSlot = slot,
            RawValue = rawValue?.Trim() ?? normalizedValue,
            NormalizedValue = normalizedValue,
            NormalizationVersion = IdentityNormalizationRules.ManufacturerIdentityNormalizationVersion,
            CreatedAt = unit.CreatedAt
        };
        _db.InventoryUnitIdentityClaims.Add(claim);
        _db.InventoryUnitIdentityOwnerships.Add(new InventoryUnitIdentityOwnership
        {
            NormalizedValue = normalizedValue,
            IdentityClaim = claim,
            InventoryUnitIdentityClaimId = claim.Id
        });
    }
    public void AddMovement(InventoryMovement movement) => _db.InventoryMovements.Add(movement);
    public void AddMovementEffect(InventoryMovementEffect effect) =>
        _db.InventoryMovementEffects.Add(effect);
    public void AddMovementUnit(InventoryMovementUnit movementUnit) =>
        _db.InventoryMovementUnits.Add(movementUnit);
    public void AddLot(InventoryLot lot) => _db.InventoryLots.Add(lot);
    public void AddLotBucketBalance(InventoryLotBucketBalance balance) =>
        _db.InventoryLotBucketBalances.Add(balance);
    public void AddLotConsumption(InventoryLotConsumption consumption) =>
        _db.InventoryLotConsumptions.Add(consumption);

    public Task<Stocktake?> GetOpenStocktakeForUpdateAsync(
        CancellationToken cancellationToken) =>
        _db.Stocktakes
            .FromSqlRaw(
                "SELECT * FROM inventory.stocktakes WHERE status IN (1,2,3) ORDER BY created_at LIMIT 1 FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Stocktake?> GetStocktakeForUpdateAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken) =>
        _db.Stocktakes
            .FromSqlInterpolated(
                $"SELECT * FROM inventory.stocktakes WHERE id = {stocktakeId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<StocktakeItem>> GetStocktakeItemsAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken) =>
        await _db.StocktakeItems
            .Where(x => x.StocktakeId == stocktakeId)
            .OrderBy(x => x.ProductId)
            .ToListAsync(cancellationToken);

    public Task<StocktakeItem?> GetStocktakeItemForUpdateAsync(
        Guid stocktakeId,
        Guid productId,
        CancellationToken cancellationToken) =>
        _db.StocktakeItems
            .FromSqlInterpolated(
                $"SELECT * FROM inventory.stocktake_items WHERE stocktake_id = {stocktakeId} AND product_id = {productId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<StocktakeUnitCheck>> GetStocktakeUnitChecksAsync(
        Guid stocktakeItemId,
        CancellationToken cancellationToken) =>
        await _db.StocktakeUnitChecks
            .Where(x => x.StocktakeItemId == stocktakeItemId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public void AddStocktake(Stocktake stocktake) => _db.Stocktakes.Add(stocktake);
    public void AddStocktakeItem(StocktakeItem item) => _db.StocktakeItems.Add(item);
    public void AddStocktakeUnitCheck(StocktakeUnitCheck unitCheck) =>
        _db.StocktakeUnitChecks.Add(unitCheck);
    public void RemoveStocktakeUnitCheck(StocktakeUnitCheck unitCheck) =>
        _db.StocktakeUnitChecks.Remove(unitCheck);

    public Task<StockAdjustment?> GetStockAdjustmentAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        _db.StockAdjustments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<StockAdjustmentItem?> GetStockAdjustmentItemAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        _db.StockAdjustmentItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public void AddStockAdjustment(StockAdjustment adjustment) =>
        _db.StockAdjustments.Add(adjustment);

    public void AddStockAdjustmentItem(StockAdjustmentItem item) =>
        _db.StockAdjustmentItems.Add(item);
}

public sealed class WarrantyRepository : IWarrantyRepository
{
    private readonly EdgeRetailsDbContext _db;

    public WarrantyRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<WarrantyClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken) =>
        _db.WarrantyClaims.AsNoTracking().SingleOrDefaultAsync(x => x.Id == claimId, cancellationToken);

    public Task<WarrantyClaim?> GetClaimForUpdateAsync(
        Guid claimId,
        CancellationToken cancellationToken) =>
        _db.WarrantyClaims
            .FromSqlInterpolated(
                $"SELECT * FROM warranty.claims WHERE id = {claimId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<WarrantyClaim?> GetClaimByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.WarrantyClaims
            .FromSqlInterpolated(
                $"SELECT * FROM warranty.claims WHERE client_operation_id = {clientOperationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<WarrantyOperation?> GetOperationForReplayAsync(Guid clientOperationId, CancellationToken cancellationToken) =>
        _db.WarrantyOperations.AsNoTracking().SingleOrDefaultAsync(x => x.ClientOperationId == clientOperationId, cancellationToken);

    public async Task<IReadOnlyList<WarrantyClaimItem>> GetClaimItemsForDiscoveryAsync(Guid claimId, CancellationToken cancellationToken) =>
        await _db.WarrantyClaimItems.AsNoTracking().Where(x => x.ClaimId == claimId).OrderBy(x => x.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WarrantyClaimItemUnit>> GetClaimUnitsForDiscoveryAsync(Guid claimId, CancellationToken cancellationToken) =>
        await (from unit in _db.WarrantyClaimItemUnits.AsNoTracking()
            join item in _db.WarrantyClaimItems.AsNoTracking() on unit.ClaimItemId equals item.Id
            where item.ClaimId == claimId
            orderby unit.Id
            select unit).ToListAsync(cancellationToken);

    public Task<WarrantyOperation?> GetOperationByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.WarrantyOperations
            .FromSqlInterpolated(
                $"SELECT * FROM warranty.operations WHERE client_operation_id = {clientOperationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<WarrantyClaimItem>> GetClaimItemsAsync(
        Guid claimId,
        CancellationToken cancellationToken) =>
        await _db.WarrantyClaimItems
            .Where(x => x.ClaimId == claimId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public Task<WarrantyClaimItem?> GetClaimItemForUpdateAsync(
        Guid claimItemId,
        CancellationToken cancellationToken) =>
        _db.WarrantyClaimItems
            .FromSqlInterpolated(
                $"SELECT * FROM warranty.claim_items WHERE id = {claimItemId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<WarrantyClaimItemUnit>> GetClaimItemUnitsAsync(
        Guid claimItemId,
        CancellationToken cancellationToken) =>
        await _db.WarrantyClaimItemUnits
            .Where(x => x.ClaimItemId == claimItemId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WarrantyClaimItemUnit>> GetClaimUnitsAsync(
        Guid claimId,
        CancellationToken cancellationToken) =>
        await (
            from unit in _db.WarrantyClaimItemUnits
            join item in _db.WarrantyClaimItems on unit.ClaimItemId equals item.Id
            where item.ClaimId == claimId
            orderby unit.Id
            select unit)
            .ToListAsync(cancellationToken);

    public Task<bool> HasActiveClaimForUnitAsync(
        Guid inventoryUnitId,
        CancellationToken cancellationToken) =>
        (
            from unit in _db.WarrantyClaimItemUnits
            join item in _db.WarrantyClaimItems on unit.ClaimItemId equals item.Id
            join claim in _db.WarrantyClaims on item.ClaimId equals claim.Id
            where unit.OriginalInventoryUnitId == inventoryUnitId &&
                  claim.Status != WarrantyClaimStatus.Closed &&
                  claim.Status != WarrantyClaimStatus.Cancelled
            select unit.Id)
            .AnyAsync(cancellationToken);

    public Task<bool> IsUnitTerminallyResolvedAsync(
        Guid inventoryUnitId,
        CancellationToken cancellationToken) =>
        (
            from unit in _db.WarrantyClaimItemUnits
            join item in _db.WarrantyClaimItems on unit.ClaimItemId equals item.Id
            join claim in _db.WarrantyClaims on item.ClaimId equals claim.Id
            where unit.OriginalInventoryUnitId == inventoryUnitId &&
                  claim.Status == WarrantyClaimStatus.Closed &&
                  (item.ResolutionType == WarrantyResolutionType.Replaced ||
                   item.ResolutionType == WarrantyResolutionType.Refunded)
            select unit.Id)
            .AnyAsync(cancellationToken);

    public async Task<decimal> GetActiveClaimedQuantityAsync(
        Guid saleItemId,
        CancellationToken cancellationToken)
    {
        var quantities = await (
            from item in _db.WarrantyClaimItems
            join claim in _db.WarrantyClaims on item.ClaimId equals claim.Id
            where item.OriginalSaleItemId == saleItemId &&
                  claim.Status != WarrantyClaimStatus.Closed &&
                  claim.Status != WarrantyClaimStatus.Cancelled
            select item.Quantity)
            .ToListAsync(cancellationToken);

        return QuantityMath.RoundQuantity(quantities.Sum());
    }

    public async Task<decimal> GetTerminallyRemovedQuantityAsync(
        Guid saleItemId,
        CancellationToken cancellationToken)
    {
        var quantities = await (
            from item in _db.WarrantyClaimItems
            join claim in _db.WarrantyClaims on item.ClaimId equals claim.Id
            where item.OriginalSaleItemId == saleItemId &&
                  claim.Status == WarrantyClaimStatus.Closed &&
                  (item.ResolutionType == WarrantyResolutionType.Replaced ||
                   item.ResolutionType == WarrantyResolutionType.Refunded)
            select item.Quantity)
            .ToListAsync(cancellationToken);

        return QuantityMath.RoundQuantity(quantities.Sum());
    }

    public Task<ShopStockWarrantyCase?> GetShopStockCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken) =>
        _db.ShopStockWarrantyCases
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == caseId, cancellationToken);

    public Task<ShopStockWarrantyCase?> GetShopStockCaseForUpdateAsync(
        Guid caseId,
        CancellationToken cancellationToken) =>
        _db.ShopStockWarrantyCases
            .FromSqlInterpolated(
                $"SELECT * FROM warranty.shop_stock_cases WHERE id = {caseId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public void AddClaim(WarrantyClaim claim) => _db.WarrantyClaims.Add(claim);
    public void AddClaimItem(WarrantyClaimItem item) => _db.WarrantyClaimItems.Add(item);
    public void AddClaimItemUnit(WarrantyClaimItemUnit itemUnit) =>
        _db.WarrantyClaimItemUnits.Add(itemUnit);
    public void AddClaimEvent(WarrantyClaimEvent claimEvent) =>
        _db.WarrantyClaimEvents.Add(claimEvent);
    public void AddOperation(WarrantyOperation operation) =>
        _db.WarrantyOperations.Add(operation);
    public void AddShopStockCase(ShopStockWarrantyCase warrantyCase) =>
        _db.ShopStockWarrantyCases.Add(warrantyCase);

    public Task<ShopWarrantySendAllocation?> GetShopWarrantySendAllocationByCaseIdAsync(
        Guid caseId,
        CancellationToken cancellationToken) =>
        _db.ShopWarrantySendAllocations
            .Where(x => x.CaseId == caseId)
            .OrderBy(x => x.OccurredAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ShopWarrantyResolutionAllocation>> GetShopWarrantyResolutionAllocationsBySendIdAsync(
        Guid sendAllocationId,
        CancellationToken cancellationToken) =>
        await _db.ShopWarrantyResolutionAllocations
            .Where(x => x.SendAllocationId == sendAllocationId)
            .OrderBy(x => x.OccurredAt)
            .ToListAsync(cancellationToken);

    public void AddShopWarrantySendAllocation(ShopWarrantySendAllocation allocation) =>
        _db.ShopWarrantySendAllocations.Add(allocation);

    public async Task<IReadOnlyList<ShopWarrantySendAllocation>> GetShopWarrantySendAllocationsByCaseIdAsync(
        Guid caseId, CancellationToken cancellationToken) =>
        await _db.ShopWarrantySendAllocations.AsNoTracking().Where(x => x.CaseId == caseId)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ShopWarrantyResolutionAllocation>> GetShopWarrantyResolutionAllocationsByCaseIdAsync(
        Guid caseId, CancellationToken cancellationToken) =>
        await _db.ShopWarrantyResolutionAllocations.AsNoTracking()
            .Where(x => _db.ShopWarrantySendAllocations.Any(s => s.Id == x.SendAllocationId && s.CaseId == caseId))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InventoryMovementUnit>> GetShopWarrantyMovementUnitsAsync(
        Guid caseId, CancellationToken cancellationToken) =>
        await _db.InventoryMovementUnits.AsNoTracking()
            .Where(x => _db.InventoryMovements.Any(m => m.Id == x.MovementId &&
                m.ReferenceType == "SHOP_WARRANTY" && m.ReferenceId == caseId &&
                (m.MovementType == InventoryMovementType.SendToSupplierWarranty ||
                 m.MovementType == InventoryMovementType.ReceiveRepairedFromSupplier ||
                 m.MovementType == InventoryMovementType.ReceiveReplacementFromSupplier ||
                 m.MovementType == InventoryMovementType.WarrantyRejectedReturn ||
                 m.MovementType == InventoryMovementType.WriteOffToScrap ||
                 m.MovementType == InventoryMovementType.WarrantyCreditResolution)))
            .OrderBy(x => x.MovementId).ThenBy(x => x.InventoryUnitId).ToListAsync(cancellationToken);

    public void AddShopWarrantyResolutionAllocation(ShopWarrantyResolutionAllocation allocation) =>
        _db.ShopWarrantyResolutionAllocations.Add(allocation);
}

public sealed class CashRepository : ICashRepository
{
    private readonly EdgeRetailsDbContext _db;

    public CashRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<CashSession?> GetOpenSessionForUpdateAsync(
        CancellationToken cancellationToken) =>
        _db.CashSessions
            .FromSqlRaw(
                "SELECT * FROM finance.cash_sessions WHERE status = 1 ORDER BY opened_at LIMIT 1 FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<CashSession?> GetSessionForUpdateAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        _db.CashSessions
            .FromSqlInterpolated(
                $"SELECT * FROM finance.cash_sessions WHERE id = {sessionId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CashMovement>> GetMovementsAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await _db.CashMovements
            .Where(x => x.CashSessionId == sessionId)
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public void AddSession(CashSession session) => _db.CashSessions.Add(session);
    public void AddMovement(CashMovement movement) => _db.CashMovements.Add(movement);
}

public sealed class QuotationRepository : IQuotationRepository
{
    private readonly EdgeRetailsDbContext _db;

    public QuotationRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<Quotation?> GetQuotationAsync(
        Guid quotationId,
        CancellationToken cancellationToken) =>
        _db.Quotations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == quotationId, cancellationToken);

    public Task<Quotation?> GetQuotationForUpdateAsync(
        Guid quotationId,
        CancellationToken cancellationToken) =>
        _db.Quotations
            .FromSqlInterpolated(
                $"SELECT * FROM sales.quotations WHERE id = {quotationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<QuotationItem>> GetItemsAsync(
        Guid quotationId,
        CancellationToken cancellationToken) =>
        await _db.QuotationItems
            .Where(x => x.QuotationId == quotationId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public Task<QuotationOperation?> GetOperationByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.QuotationOperations.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public void AddQuotation(Quotation quotation) => _db.Quotations.Add(quotation);
    public void AddItem(QuotationItem item) => _db.QuotationItems.Add(item);
    public void RemoveItem(QuotationItem item) => _db.QuotationItems.Remove(item);
    public void AddOperation(QuotationOperation operation) =>
        _db.QuotationOperations.Add(operation);
}


public sealed class SalesRepository : ISalesRepository
{
    private readonly EdgeRetailsDbContext _db;

    public SalesRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SoldSourceCapacity>> GetSoldSourceCapacityForUpdateAsync(
        Guid saleItemId, CancellationToken cancellationToken)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new BusinessRuleException("sales.source_transaction_required", "Sold-source capacity requires the business transaction.");
        }
        var item = await _db.SaleItems.FromSqlInterpolated(
            $"SELECT * FROM sales.sale_items WHERE id={saleItemId} FOR NO KEY UPDATE").SingleOrDefaultAsync(cancellationToken)
            ?? throw new BusinessRuleException("sales.source_item_missing", "Original SaleItem was not found.");
        var movement = await _db.InventoryMovements.FromSqlInterpolated(
            $"SELECT * FROM inventory.movements WHERE id={item.InventoryMovementId} FOR NO KEY UPDATE").SingleOrDefaultAsync(cancellationToken);
        var consumed = await _db.InventoryLotConsumptions.FromSqlInterpolated(
            $"SELECT * FROM inventory.lot_consumptions WHERE movement_id={item.InventoryMovementId} ORDER BY id FOR NO KEY UPDATE")
            .ToArrayAsync(cancellationToken);
        if (movement is null || movement.ProductId != item.ProductId || movement.MovementType != InventoryMovementType.SaleOut ||
            movement.ReferenceType != "SALE" || movement.ReferenceId != item.SaleId ||
            consumed.Length == 0 || consumed.Sum(x => x.Quantity) != item.BaseQuantity)
        {
            throw new BusinessRuleException("sales.source_reconciliation_required", "Original sold-source event quantity or ownership is inconsistent.");
        }
        var lotIds = consumed.Select(x => x.LotId).Distinct().OrderBy(x => x).ToArray();
        var lots = await _db.InventoryLots.FromSqlInterpolated(
            $"SELECT * FROM inventory.lots WHERE id=ANY({lotIds}) ORDER BY id FOR NO KEY UPDATE").ToArrayAsync(cancellationToken);
        if (lots.Length != lotIds.Length || lots.Any(x => x.ProductId != item.ProductId))
        {
            throw new BusinessRuleException("sales.source_reconciliation_required", "Original sold lots do not belong to the SaleItem.");
        }
        var purchaseIds = lots.Where(x => x.PurchaseItemId.HasValue).Select(x => x.PurchaseItemId!.Value).Distinct().ToArray();
        var provenance = await (from pi in _db.PurchaseItems.AsNoTracking()
            join p in _db.Purchases.AsNoTracking() on pi.PurchaseId equals p.Id
            where purchaseIds.Contains(pi.Id)
            select new { pi.Id, pi.ProductId, p.SupplierId }).ToArrayAsync(cancellationToken);
        if (provenance.Length != purchaseIds.Length || provenance.Any(x => x.ProductId != item.ProductId))
        {
            throw new BusinessRuleException("sales.source_reconciliation_required", "Original purchase provenance cannot be proved.");
        }
        // Read fresh committed lifecycle status without acquiring claim-header locks after sale/source locks.
        var claims = await (from ci in _db.WarrantyClaimItems.AsNoTracking()
            join c in _db.WarrantyClaims.AsNoTracking() on ci.ClaimId equals c.Id
            where ci.OriginalSaleItemId == item.Id &&
                ((c.Status != WarrantyClaimStatus.Closed && c.Status != WarrantyClaimStatus.Cancelled) ||
                 (c.Status == WarrantyClaimStatus.Closed &&
                  (ci.ResolutionType == WarrantyResolutionType.Replaced || ci.ResolutionType == WarrantyResolutionType.Refunded)))
            select new { ci.Id, ci.Quantity }).ToArrayAsync(cancellationToken);
        var claimIds = claims.Select(x => x.Id).ToArray();
        var claimFacts = await _db.WarrantyClaimSourceAllocations.AsNoTracking()
            .Where(x => claimIds.Contains(x.ClaimItemId)).ToArrayAsync(cancellationToken);
        var returns = await _db.SaleReturnItems.AsNoTracking().Where(x => x.SaleItemId == item.Id).ToArrayAsync(cancellationToken);
        var returnIds = returns.Select(x => x.Id).ToArray();
        var returnFacts = await _db.SaleReturnSourceAllocations.AsNoTracking()
            .Where(x => returnIds.Contains(x.SaleReturnItemId)).ToArrayAsync(cancellationToken);
        if (claims.Any(x => claimFacts.Where(a => a.ClaimItemId == x.Id).Sum(a => a.BaseQuantity) != x.Quantity) ||
            returns.Any(x => returnFacts.Where(a => a.SaleReturnItemId == x.Id).Sum(a => a.BaseQuantity) != x.BaseQuantity))
        {
            throw new BusinessRuleException("sales.source_reconciliation_required", "Historical claim/return sold-source allocation is ambiguous or absent.");
        }
        var consumptionIds = consumed.Select(x => x.Id).ToHashSet();
        if (claimFacts.Any(x => !consumptionIds.Contains(x.SaleConsumptionId)) ||
            returnFacts.Any(x => !consumptionIds.Contains(x.SaleConsumptionId)))
        {
            throw new BusinessRuleException("sales.source_reconciliation_required", "Source facts belong to a different sold event.");
        }
        // Include the current transaction's unsaved allocations, without counting flushed facts twice.
        var pendingClaims = _db.ChangeTracker.Entries<WarrantyClaimSourceAllocation>()
            .Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray();
        var pendingReturns = _db.ChangeTracker.Entries<SaleReturnSourceAllocation>()
            .Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToArray();
        var result = new List<SoldSourceCapacity>();
        foreach (var source in consumed.OrderBy(x => x.Id))
        {
            var lot = lots.Single(x => x.Id == source.LotId);
            Guid? supplierId = lot.PurchaseItemId is Guid purchaseItemId
                ? provenance.Single(x => x.Id == purchaseItemId).SupplierId : null;
            var used = claimFacts.Where(x => x.SaleConsumptionId == source.Id).Sum(x => x.BaseQuantity)
                + returnFacts.Where(x => x.SaleConsumptionId == source.Id).Sum(x => x.BaseQuantity)
                + pendingClaims.Where(x => x.SaleConsumptionId == source.Id).Sum(x => x.BaseQuantity)
                + pendingReturns.Where(x => x.SaleConsumptionId == source.Id).Sum(x => x.BaseQuantity);
            var remaining = QuantityMath.RoundQuantity(source.Quantity - used);
            if (remaining < 0m)
            {
                throw new BusinessRuleException("sales.source_capacity_exceeded", "Committed sold-source capacity is over-consumed.");
            }
            result.Add(new(source.Id, lot.Id, lot.PurchaseItemId, supplierId, source.Quantity, remaining));
        }
        return result;
    }

    public void AddReturnSourceAllocation(SaleReturnSourceAllocation allocation)
        => _db.SaleReturnSourceAllocations.Add(allocation);

    public void AddClaimSourceAllocation(WarrantyClaimSourceAllocation allocation)
        => _db.WarrantyClaimSourceAllocations.Add(allocation);

    public Task<Sale?> GetSaleByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.Sales.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<Sale?> GetSaleForUpdateAsync(
        Guid saleId,
        CancellationToken cancellationToken) =>
        _db.Sales
            .FromSqlInterpolated(
                $"SELECT * FROM sales.sales WHERE id = {saleId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SaleItem>> GetSaleItemsAsync(
        Guid saleId,
        CancellationToken cancellationToken) =>
        await _db.SaleItems
            .Where(x => x.SaleId == saleId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public Task<SaleItem?> GetSaleItemForUpdateAsync(
        Guid saleItemId,
        CancellationToken cancellationToken) =>
        _db.SaleItems
            .FromSqlInterpolated(
                $"SELECT * FROM sales.sale_items WHERE id = {saleItemId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SaleItemUnit>> GetSaleItemUnitsAsync(
        Guid saleItemId,
        CancellationToken cancellationToken) =>
        await _db.SaleItemUnits
            .Where(x => x.SaleItemId == saleItemId)
            .OrderBy(x => x.InventoryUnitId)
            .ToListAsync(cancellationToken);

    public Task<SalePayment?> GetSalePaymentAsync(
        Guid saleId,
        CancellationToken cancellationToken) =>
        _db.SalePayments.SingleOrDefaultAsync(
            x => x.SaleId == saleId,
            cancellationToken);

    public async Task<decimal> GetReturnedBaseQuantityAsync(
        Guid saleItemId,
        CancellationToken cancellationToken) =>
        await _db.SaleReturnItems
            .Where(x => x.SaleItemId == saleItemId)
            .SumAsync(x => (decimal?)x.BaseQuantity, cancellationToken)
        ?? 0m;

    public async Task<decimal> GetRefundedAmountAsync(
        Guid saleItemId,
        CancellationToken cancellationToken) =>
        await _db.SaleReturnItems
            .Where(x => x.SaleItemId == saleItemId)
            .SumAsync(x => (decimal?)x.RefundAmount, cancellationToken)
        ?? 0m;

    public async Task<decimal> GetReturnedOriginalCostAmountAsync(
        Guid saleItemId,
        CancellationToken cancellationToken) =>
        await _db.SaleReturnItems
            .Where(x => x.SaleItemId == saleItemId)
            .SumAsync(x => (decimal?)x.OriginalCostAmount, cancellationToken)
        ?? 0m;

    public async Task<IReadOnlySet<Guid>> GetReturnedInventoryUnitIdsAsync(
        Guid saleItemId,
        CancellationToken cancellationToken)
    {
        var ids = await (
            from unit in _db.SaleReturnItemUnits
            join item in _db.SaleReturnItems
                on unit.SaleReturnItemId equals item.Id
            where item.SaleItemId == saleItemId
            select unit.InventoryUnitId)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public Task<SaleReturn?> GetReturnByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.SaleReturns.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public void AddSale(Sale sale) => _db.Sales.Add(sale);
    public void AddSaleItem(SaleItem item) => _db.SaleItems.Add(item);
    public void AddSalePayment(SalePayment payment) => _db.SalePayments.Add(payment);
    public void AddSaleItemUnit(SaleItemUnit itemUnit) => _db.SaleItemUnits.Add(itemUnit);
    public void AddReturn(SaleReturn saleReturn) => _db.SaleReturns.Add(saleReturn);
    public void AddReturnItem(SaleReturnItem item) => _db.SaleReturnItems.Add(item);
    public void AddReturnItemUnit(SaleReturnItemUnit itemUnit) => _db.SaleReturnItemUnits.Add(itemUnit);
}

public sealed class PurchasingRepository : IPurchasingRepository
{
    private readonly EdgeRetailsDbContext _db;

    public PurchasingRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<Purchase?> GetPurchaseByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.Purchases.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<Purchase?> GetPurchaseAsync(Guid purchaseId, CancellationToken cancellationToken) =>
        _db.Purchases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseId, cancellationToken);

    public Task<Purchase?> GetPurchaseForUpdateAsync(
        Guid purchaseId,
        CancellationToken cancellationToken) =>
        _db.Purchases
            .FromSqlInterpolated(
                $"SELECT * FROM purchasing.purchases WHERE id = {purchaseId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<Purchase?> GetPurchaseBySupplierInvoiceAsync(
        Guid supplierId,
        string normalizedSupplierInvoiceNumber,
        CancellationToken cancellationToken) =>
        _db.Purchases.SingleOrDefaultAsync(
            x => x.SupplierId == supplierId &&
                 x.NormalizedSupplierInvoiceNumber == normalizedSupplierInvoiceNumber,
            cancellationToken);

    public async Task<IReadOnlyList<PurchaseItem>> GetPurchaseItemsForDiscoveryAsync(Guid purchaseId, CancellationToken cancellationToken) =>
        await _db.PurchaseItems.AsNoTracking().Where(x => x.PurchaseId == purchaseId).OrderBy(x => x.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PurchaseItem>> GetPurchaseItemsAsync(
        Guid purchaseId,
        CancellationToken cancellationToken) =>
        await _db.PurchaseItems
            .Where(x => x.PurchaseId == purchaseId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public Task<PurchaseItem?> GetPurchaseItemForUpdateAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken) =>
        _db.PurchaseItems
            .FromSqlInterpolated(
                $"SELECT * FROM purchasing.purchase_items WHERE id = {purchaseItemId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PurchaseItemUnit>> GetPurchaseItemUnitsAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken) =>
        await _db.PurchaseItemUnits
            .Where(x => x.PurchaseItemId == purchaseItemId)
            .OrderBy(x => x.InventoryUnitId)
            .ToListAsync(cancellationToken);

    public Task<PurchaseReturn?> GetReturnByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.PurchaseReturns.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public async Task<decimal> GetReturnedBaseQuantityAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken) =>
        await _db.PurchaseReturnItems
            .Where(x => x.PurchaseItemId == purchaseItemId)
            .SumAsync(x => (decimal?)x.BaseQuantity, cancellationToken)
        ?? 0m;

    public Task<bool> HasCompletedReturnAsync(
        Guid purchaseId,
        CancellationToken cancellationToken) =>
        _db.PurchaseReturns.AnyAsync(
            x => x.PurchaseId == purchaseId &&
                 x.Status == PurchaseReturnStatus.Completed,
            cancellationToken);

    public Task<PurchaseVoid?> GetVoidByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.PurchaseVoids.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<bool> HasVoidAsync(
        Guid purchaseId,
        CancellationToken cancellationToken) =>
        _db.PurchaseVoids.AnyAsync(
            x => x.PurchaseId == purchaseId,
            cancellationToken);

    public void AddPurchase(Purchase purchase) => _db.Purchases.Add(purchase);
    public void AddPurchaseItem(PurchaseItem item) => _db.PurchaseItems.Add(item);
    public void AddPurchaseItemUnit(PurchaseItemUnit itemUnit) => _db.PurchaseItemUnits.Add(itemUnit);
    public void AddReturn(PurchaseReturn purchaseReturn) => _db.PurchaseReturns.Add(purchaseReturn);
    public void AddReturnItem(PurchaseReturnItem item) => _db.PurchaseReturnItems.Add(item);
    public void AddReturnItemUnit(PurchaseReturnItemUnit itemUnit) => _db.PurchaseReturnItemUnits.Add(itemUnit);
    public void AddVoid(PurchaseVoid purchaseVoid) => _db.PurchaseVoids.Add(purchaseVoid);
}



public sealed class PartyRepository : IPartyRepository
{
    private readonly EdgeRetailsDbContext _db;

    public PartyRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<EdgeRetails.Domain.Parties.Customer?> GetCustomerAsync(
        Guid customerId,
        CancellationToken cancellationToken) =>
        _db.Customers.SingleOrDefaultAsync(
            x => x.Id == customerId,
            cancellationToken);

    public Task<EdgeRetails.Domain.Parties.Customer?> GetCustomerForUpdateAsync(
        Guid customerId,
        CancellationToken cancellationToken) =>
        _db.Customers
            .FromSqlInterpolated(
                $"SELECT * FROM parties.customers WHERE id = {customerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<EdgeRetails.Domain.Parties.Supplier?> GetSupplierAsync(
        Guid supplierId,
        CancellationToken cancellationToken) =>
        _db.Suppliers.SingleOrDefaultAsync(
            x => x.Id == supplierId,
            cancellationToken);

    public Task<EdgeRetails.Domain.Parties.Supplier?> GetSupplierForUpdateAsync(
        Guid supplierId,
        CancellationToken cancellationToken) =>
        _db.Suppliers
            .FromSqlInterpolated(
                $"SELECT * FROM parties.suppliers WHERE id = {supplierId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<EdgeRetails.Domain.Parties.Customer>> GetCustomersAsync(
        bool includeInactive,
        CancellationToken cancellationToken) =>
        await _db.Customers
            .AsNoTracking()
            .Where(x => includeInactive || x.IsActive)
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<EdgeRetails.Domain.Parties.Supplier>> GetSuppliersAsync(
        bool includeInactive,
        CancellationToken cancellationToken) =>
        await _db.Suppliers
            .AsNoTracking()
            .Where(x => includeInactive || x.IsActive)
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

    public void AddCustomer(EdgeRetails.Domain.Parties.Customer customer) =>
        _db.Customers.Add(customer);

    public void AddSupplier(EdgeRetails.Domain.Parties.Supplier supplier) =>
        _db.Suppliers.Add(supplier);
}

