using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Application.Abstractions;

public interface ICatalogRepository
{
    Task<Product?> GetProductAsync(Guid productId, CancellationToken cancellationToken);
    Task<Product?> GetProductForUpdateAsync(Guid productId, CancellationToken cancellationToken);
    Task<Product?> GetProductBySkuAsync(string normalizedSku, CancellationToken cancellationToken);
    Task<IReadOnlyList<Product>> GetProductsAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken);
    Task<Category?> GetCategoryForUpdateAsync(Guid categoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Category>> GetCategoriesAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<bool> IsCategoryInUseByActiveProductAsync(Guid categoryId, CancellationToken cancellationToken);
    Task<Unit?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken);
    Task<Unit?> GetUnitForUpdateAsync(Guid unitId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Unit>> GetUnitsAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<bool> IsUnitInUseByActiveCatalogAsync(Guid unitId, CancellationToken cancellationToken);
    Task<ProductUnit?> GetProductUnitAsync(Guid productUnitId, CancellationToken cancellationToken);
    Task<ProductUnit?> GetProductUnitAsync(Guid productId, Guid unitId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProductUnit>> GetProductUnitsAsync(
        Guid productId,
        CancellationToken cancellationToken);
    Task<ProductUnitBarcode?> GetBarcodeAsync(string barcode, CancellationToken cancellationToken);
    Task<IReadOnlyList<Product>> GetActiveProductsAsync(
        StocktakeScope scope,
        Guid? categoryId,
        CancellationToken cancellationToken);
    void AddCategory(Category category);
    void AddUnit(Unit unit);
    void AddProduct(Product product);
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
    Task<IReadOnlyList<LotBucketPosition>> GetPurchaseItemLotPositionsForUpdateAsync(
        Guid purchaseItemId,
        InventoryBucket bucket,
        CancellationToken cancellationToken);
    Task<InventoryLotBucketBalance?> GetLotBucketBalanceForUpdateAsync(
        Guid lotId,
        InventoryBucket bucket,
        CancellationToken cancellationToken);
    Task<InventoryLot?> GetInventoryLotForUpdateAsync(
        Guid lotId,
        CancellationToken cancellationToken);
    Task<bool> HasPurchaseItemConsumptionAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<InventoryLotConsumption>> GetMovementLotConsumptionsAsync(
        Guid movementId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<InventoryUnit>> GetInventoryUnitsForUpdateAsync(
        Guid productId,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<InventoryUnit>> GetSellableInventoryUnitsAsync(
        Guid productId,
        CancellationToken cancellationToken);
    Task<bool> InventoryIdentityExistsAsync(
        string? serialNumber,
        string? imei1,
        string? imei2,
        CancellationToken cancellationToken);
    Task<bool> IsProductBlockedByCountingStocktakeAsync(
        Guid productId,
        CancellationToken cancellationToken);
    void AddStockBalance(StockBalance balance);
    void AddCostState(ProductCostState costState);
    void AddInventoryUnit(InventoryUnit unit);
    void AddMovement(InventoryMovement movement);
    void AddMovementEffect(InventoryMovementEffect effect);
    void AddMovementUnit(InventoryMovementUnit movementUnit);
    void AddLot(InventoryLot lot);
    void AddLotBucketBalance(InventoryLotBucketBalance balance);
    void AddLotConsumption(InventoryLotConsumption consumption);

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
    void RemoveStocktakeUnitCheck(StocktakeUnitCheck unitCheck);

    Task<StockAdjustment?> GetStockAdjustmentAsync(Guid id, CancellationToken cancellationToken);
    Task<StockAdjustmentItem?> GetStockAdjustmentItemAsync(Guid id, CancellationToken cancellationToken);
    void AddStockAdjustment(StockAdjustment adjustment);
    void AddStockAdjustmentItem(StockAdjustmentItem item);
}

public interface IWarrantyRepository
{
    Task<WarrantyClaim?> GetClaimForUpdateAsync(
        Guid claimId,
        CancellationToken cancellationToken);
    Task<WarrantyClaim?> GetClaimByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<WarrantyOperation?> GetOperationByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<WarrantyClaimItem>> GetClaimItemsAsync(
        Guid claimId,
        CancellationToken cancellationToken);
    Task<WarrantyClaimItem?> GetClaimItemForUpdateAsync(
        Guid claimItemId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<WarrantyClaimItemUnit>> GetClaimItemUnitsAsync(
        Guid claimItemId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<WarrantyClaimItemUnit>> GetClaimUnitsAsync(
        Guid claimId,
        CancellationToken cancellationToken);
    Task<bool> HasActiveClaimForUnitAsync(
        Guid inventoryUnitId,
        CancellationToken cancellationToken);
    Task<bool> IsUnitTerminallyResolvedAsync(
        Guid inventoryUnitId,
        CancellationToken cancellationToken);
    Task<decimal> GetActiveClaimedQuantityAsync(
        Guid saleItemId,
        CancellationToken cancellationToken);
    Task<decimal> GetTerminallyRemovedQuantityAsync(
        Guid saleItemId,
        CancellationToken cancellationToken);
    Task<ShopStockWarrantyCase?> GetShopStockCaseAsync(
        Guid caseId,
        CancellationToken cancellationToken);
    Task<ShopStockWarrantyCase?> GetShopStockCaseForUpdateAsync(
        Guid caseId,
        CancellationToken cancellationToken);
    void AddClaim(WarrantyClaim claim);
    void AddClaimItem(WarrantyClaimItem item);
    void AddClaimItemUnit(WarrantyClaimItemUnit itemUnit);
    void AddClaimEvent(WarrantyClaimEvent claimEvent);
    void AddOperation(WarrantyOperation operation);
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
    Task<Quotation?> GetQuotationAsync(
        Guid quotationId,
        CancellationToken cancellationToken);
    Task<Quotation?> GetQuotationForUpdateAsync(
        Guid quotationId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotationItem>> GetItemsAsync(
        Guid quotationId,
        CancellationToken cancellationToken);
    Task<QuotationOperation?> GetOperationByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    void AddQuotation(Quotation quotation);
    void AddItem(QuotationItem item);
    void RemoveItem(QuotationItem item);
    void AddOperation(QuotationOperation operation);
}


public interface ISalesRepository
{
    Task<Sale?> GetSaleByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<Sale?> GetSaleForUpdateAsync(
        Guid saleId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SaleItem>> GetSaleItemsAsync(
        Guid saleId,
        CancellationToken cancellationToken);
    Task<SaleItem?> GetSaleItemForUpdateAsync(
        Guid saleItemId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SaleItemUnit>> GetSaleItemUnitsAsync(
        Guid saleItemId,
        CancellationToken cancellationToken);
    Task<SalePayment?> GetSalePaymentAsync(
        Guid saleId,
        CancellationToken cancellationToken);
    Task<decimal> GetReturnedBaseQuantityAsync(
        Guid saleItemId,
        CancellationToken cancellationToken);
    Task<decimal> GetRefundedAmountAsync(
        Guid saleItemId,
        CancellationToken cancellationToken);
    Task<decimal> GetReturnedOriginalCostAmountAsync(
        Guid saleItemId,
        CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> GetReturnedInventoryUnitIdsAsync(
        Guid saleItemId,
        CancellationToken cancellationToken);
    Task<SaleReturn?> GetReturnByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    void AddSale(Sale sale);
    void AddSaleItem(SaleItem item);
    void AddSalePayment(SalePayment payment);
    void AddSaleItemUnit(SaleItemUnit itemUnit);
    void AddReturn(SaleReturn saleReturn);
    void AddReturnItem(SaleReturnItem item);
    void AddReturnItemUnit(SaleReturnItemUnit itemUnit);
}

public interface IPurchasingRepository
{
    Task<Purchase?> GetPurchaseByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<Purchase?> GetPurchaseForUpdateAsync(
        Guid purchaseId,
        CancellationToken cancellationToken);
    Task<Purchase?> GetPurchaseBySupplierInvoiceAsync(
        Guid supplierId,
        string normalizedSupplierInvoiceNumber,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PurchaseItem>> GetPurchaseItemsAsync(
        Guid purchaseId,
        CancellationToken cancellationToken);
    Task<PurchaseItem?> GetPurchaseItemForUpdateAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PurchaseItemUnit>> GetPurchaseItemUnitsAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken);
    Task<PurchaseReturn?> GetReturnByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<decimal> GetReturnedBaseQuantityAsync(
        Guid purchaseItemId,
        CancellationToken cancellationToken);
    Task<bool> HasCompletedReturnAsync(
        Guid purchaseId,
        CancellationToken cancellationToken);
    Task<PurchaseVoid?> GetVoidByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<bool> HasVoidAsync(
        Guid purchaseId,
        CancellationToken cancellationToken);
    void AddPurchase(Purchase purchase);
    void AddPurchaseItem(PurchaseItem item);
    void AddPurchaseItemUnit(PurchaseItemUnit itemUnit);
    void AddReturn(PurchaseReturn purchaseReturn);
    void AddReturnItem(PurchaseReturnItem item);
    void AddReturnItemUnit(PurchaseReturnItemUnit itemUnit);
    void AddVoid(PurchaseVoid purchaseVoid);
}



public interface IPartyRepository
{
    Task<EdgeRetails.Domain.Parties.Customer?> GetCustomerAsync(
        Guid customerId,
        CancellationToken cancellationToken);
    Task<EdgeRetails.Domain.Parties.Customer?> GetCustomerForUpdateAsync(
        Guid customerId,
        CancellationToken cancellationToken);
    Task<EdgeRetails.Domain.Parties.Supplier?> GetSupplierAsync(
        Guid supplierId,
        CancellationToken cancellationToken);
    Task<EdgeRetails.Domain.Parties.Supplier?> GetSupplierForUpdateAsync(
        Guid supplierId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<EdgeRetails.Domain.Parties.Customer>> GetCustomersAsync(
        bool includeInactive,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<EdgeRetails.Domain.Parties.Supplier>> GetSuppliersAsync(
        bool includeInactive,
        CancellationToken cancellationToken);
    void AddCustomer(EdgeRetails.Domain.Parties.Customer customer);
    void AddSupplier(EdgeRetails.Domain.Parties.Supplier supplier);
}

public interface IExpenseRepository
{
    Task<Expense?> GetByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<Expense?> GetForUpdateAsync(
        Guid expenseId,
        CancellationToken cancellationToken);
    Task<ExpenseCategory?> GetCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken);
    Task<ExpenseSubcategory?> GetSubcategoryAsync(
        Guid subcategoryId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ExpenseCategory>> GetCategoriesAsync(
        bool includeInactive,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ExpenseSubcategory>> GetSubcategoriesAsync(
        Guid? categoryId,
        bool includeInactive,
        CancellationToken cancellationToken);
    void AddCategory(ExpenseCategory category);
    void AddSubcategory(ExpenseSubcategory subcategory);
    void AddExpense(Expense expense);
}

public interface IThakaRepository
{
    Task<ThakaProject?> GetProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken);
    Task<ThakaProject?> GetProjectForUpdateAsync(
        Guid projectId,
        CancellationToken cancellationToken);
    Task<ThakaMaterialIssue?> GetIssueByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<ThakaMaterialIssue?> GetIssueForUpdateAsync(
        Guid materialIssueId,
        CancellationToken cancellationToken);
    Task<ThakaPayment?> GetPaymentByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<ThakaPayment?> GetPaymentForUpdateAsync(
        Guid paymentId,
        CancellationToken cancellationToken);
    Task<ThakaSettlement?> GetSettlementByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<ThakaMaterialReversal?> GetMaterialReversalByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<ThakaMaterialReversal?> GetMaterialReversalByIssueAsync(
        Guid materialIssueId,
        CancellationToken cancellationToken);
    Task<ThakaPaymentReversal?> GetPaymentReversalByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken);
    Task<ThakaPaymentReversal?> GetPaymentReversalByPaymentAsync(
        Guid paymentId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ThakaMaterialIssueItem>> GetIssueItemsAsync(
        Guid materialIssueId,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ThakaMaterialIssueUnit>> GetIssueUnitsAsync(
        Guid materialIssueItemId,
        CancellationToken cancellationToken);
    Task<decimal> GetGrossMaterialChargesAsync(
        Guid projectId,
        CancellationToken cancellationToken);
    Task<decimal> GetPaymentsCollectedAsync(
        Guid projectId,
        CancellationToken cancellationToken);
    Task<decimal> GetSettlementDiscountsAsync(
        Guid projectId,
        CancellationToken cancellationToken);
    Task<ThakaSettlement?> GetLatestSettlementAsync(
        Guid projectId,
        CancellationToken cancellationToken);
    void AddProject(ThakaProject project);
    void AddMaterialIssue(ThakaMaterialIssue issue);
    void AddMaterialIssueItem(ThakaMaterialIssueItem item);
    void AddMaterialIssueUnit(ThakaMaterialIssueUnit unit);
    void AddPayment(ThakaPayment payment);
    void AddSettlement(ThakaSettlement settlement);
    void AddReopening(ThakaReopening reopening);
    void AddMaterialReversal(ThakaMaterialReversal reversal);
    void AddPaymentReversal(ThakaPaymentReversal reversal);
}
