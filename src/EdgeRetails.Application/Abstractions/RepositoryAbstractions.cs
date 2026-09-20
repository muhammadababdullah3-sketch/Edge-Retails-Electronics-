using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Application.Abstractions;

public interface ICatalogRepository
{
    Task<Product?> GetProductAsync(Guid productId, CancellationToken cancellationToken);
    Task<Unit?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken);
    Task<ProductUnit?> GetProductUnitAsync(Guid productUnitId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProductUnit>> GetProductUnitsAsync(
        Guid productId,
        CancellationToken cancellationToken);
    Task<ProductUnitBarcode?> GetBarcodeAsync(string barcode, CancellationToken cancellationToken);
    Task<IReadOnlyList<Product>> GetActiveProductsAsync(
        StocktakeScope scope,
        Guid? categoryId,
        CancellationToken cancellationToken);
    void AddProductUnit(ProductUnit productUnit);
    void AddBarcode(ProductUnitBarcode barcode);
}

public sealed record LotBucketPosition(
    InventoryLot Lot,
    InventoryLotBucketBalance Balance);

public interface IInventoryRepository
{
    Task<StockBalance?> GetStockBalanceForUpdateAsync(
        Guid productId,
        CancellationToken cancellationToken);
    Task<ProductCostState?> GetCostStateForUpdateAsync(
        Guid productId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<LotBucketPosition>> GetLotBucketPositionsForUpdateAsync(
        Guid productId,
        InventoryBucket bucket,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<InventoryUnit>> GetInventoryUnitsForUpdateAsync(
        Guid productId,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<InventoryUnit>> GetSellableInventoryUnitsAsync(
        Guid productId,
        CancellationToken cancellationToken);
    Task<bool> IsProductBlockedByCountingStocktakeAsync(
        Guid productId,
        CancellationToken cancellationToken);
    void AddStockBalance(StockBalance balance);
    void AddCostState(ProductCostState costState);
    void AddMovement(InventoryMovement movement);
    void AddMovementEffect(InventoryMovementEffect effect);
    void AddMovementUnit(InventoryMovementUnit movementUnit);
    void AddLot(InventoryLot lot);
    void AddLotBucketBalance(InventoryLotBucketBalance balance);

    Task<Stocktake?> GetOpenStocktakeForUpdateAsync(CancellationToken cancellationToken);
    Task<Stocktake?> GetStocktakeForUpdateAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<StocktakeItem>> GetStocktakeItemsAsync(
        Guid stocktakeId,
        CancellationToken cancellationToken);
    Task<StocktakeItem?> GetStocktakeItemForUpdateAsync(
        Guid stocktakeId,
        Guid productId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<StocktakeUnitCheck>> GetStocktakeUnitChecksAsync(
        Guid stocktakeItemId,
        CancellationToken cancellationToken);
    void AddStocktake(Stocktake stocktake);
    void AddStocktakeItem(StocktakeItem item);
    void AddStocktakeUnitCheck(StocktakeUnitCheck unitCheck);
}

public interface IWarrantyRepository
{
    Task<WarrantyClaim?> GetClaimForUpdateAsync(
        Guid claimId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<WarrantyClaimItem>> GetClaimItemsAsync(
        Guid claimId,
        CancellationToken cancellationToken);
    Task<ShopStockWarrantyCase?> GetShopStockCaseForUpdateAsync(
        Guid caseId,
        CancellationToken cancellationToken);
    void AddClaim(WarrantyClaim claim);
    void AddClaimItem(WarrantyClaimItem item);
    void AddClaimItemUnit(WarrantyClaimItemUnit itemUnit);
    void AddClaimEvent(WarrantyClaimEvent claimEvent);
    void AddShopStockCase(ShopStockWarrantyCase warrantyCase);
}

public interface ICashRepository
{
    Task<CashSession?> GetOpenSessionForUpdateAsync(CancellationToken cancellationToken);
    Task<CashSession?> GetSessionForUpdateAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<CashMovement>> GetMovementsAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
    void AddSession(CashSession session);
    void AddMovement(CashMovement movement);
}

public interface IQuotationRepository
{
    Task<Quotation?> GetQuotationForUpdateAsync(
        Guid quotationId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotationItem>> GetItemsAsync(
        Guid quotationId,
        CancellationToken cancellationToken);
    void AddQuotation(Quotation quotation);
    void AddItem(QuotationItem item);
}
