using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

    public Task<Product?> GetProductForUpdateAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        _db.Products
            .FromSqlInterpolated(
                $"SELECT * FROM catalog.products WHERE id = {productId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

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
        CancellationToken cancellationToken) =>
        _db.InventoryLots
            .FromSqlInterpolated(
                $"SELECT * FROM inventory.lots WHERE id = {lotId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<bool> HasPurchaseItemConsumptionAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken) =>
        (from consumption in _db.InventoryLotConsumptions
         join lot in _db.InventoryLots on consumption.LotId equals lot.Id
         where lot.PurchaseItemId == purchaseItemId &&
               consumption.Quantity > 0
         select consumption.Id).AnyAsync(cancellationToken);

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

    public Task<bool> InventoryIdentityExistsAsync(
        string? serialNumber,
        string? imei1,
        string? imei2,
        CancellationToken cancellationToken) =>
        _db.InventoryUnits.AnyAsync(
            x =>
                (serialNumber != null && x.SerialNumber == serialNumber) ||
                (imei1 != null && (x.Imei1 == imei1 || x.Imei2 == imei1)) ||
                (imei2 != null && (x.Imei1 == imei2 || x.Imei2 == imei2)),
            cancellationToken);

    public Task<bool> IsProductBlockedByCountingStocktakeAsync(
        Guid productId,
        CancellationToken cancellationToken) =>
        (from item in _db.StocktakeItems
         join stocktake in _db.Stocktakes on item.StocktakeId equals stocktake.Id
         where item.ProductId == productId &&
               (stocktake.Status == StocktakeStatus.Counting ||
                stocktake.Status == StocktakeStatus.Review)
         select item.Id).AnyAsync(cancellationToken);

    public void AddStockBalance(StockBalance balance) => _db.StockBalances.Add(balance);
    public void AddCostState(ProductCostState costState) => _db.ProductCostStates.Add(costState);
    public void AddInventoryUnit(InventoryUnit unit) => _db.InventoryUnits.Add(unit);
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

