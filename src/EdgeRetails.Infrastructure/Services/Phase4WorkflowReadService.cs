using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class Phase4WorkflowReadService : IPhase4WorkflowReadService
{
    private readonly EdgeRetailsDbContext _db;

    public Phase4WorkflowReadService(EdgeRetailsDbContext db) => _db = db;

    public Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status,
        Guid? sourcePurchaseItemId,
        CancellationToken cancellationToken) =>
        GetExactUnitsAsync(productId, status, sourcePurchaseItemId, 100, cancellationToken);

    public async Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status,
        Guid? sourcePurchaseItemId,
        int pageSize,
        CancellationToken cancellationToken)
        => await GetExactUnitsAsync(productId, status, sourcePurchaseItemId, pageSize, null, cancellationToken);

    public async Task<IReadOnlyList<ExactInventoryUnitDto>> GetExactUnitsAsync(
        Guid productId,
        InventoryUnitStatus? status,
        Guid? sourcePurchaseItemId,
        int pageSize,
        Guid? beforeUnitId,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize <= 0 ? 100 : pageSize, 1, 500);
        var query = _db.InventoryUnits
            .AsNoTracking()
            .Where(x => x.ProductId == productId);

        if (status is not null)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        if (sourcePurchaseItemId is not null)
        {
            query = query.Where(x => x.SourcePurchaseItemId == sourcePurchaseItemId.Value);
        }

        if (beforeUnitId is Guid cursorId)
        {
            query = query.Where(x => x.Id.CompareTo(cursorId) > 0);
        }

        var units = await query
            .OrderBy(x => x.Id)
            .Take(take)
            .ToArrayAsync(cancellationToken);

        return await ProjectUnitsAsync(units, cancellationToken);
    }

    public async Task<IReadOnlyList<ScannerProductMatchDto>> ResolveScannerAsync(
        string input,
        CancellationToken cancellationToken)
    {
        var term = input?.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            return Array.Empty<ScannerProductMatchDto>();
        }

        var upper = term.ToUpperInvariant();
        var digits = new string(term.Where(char.IsDigit).ToArray());

        // 1. TrackingCode / Physical SKU
        var trackingIds = await _db.InventoryUnits
            .AsNoTracking()
            .Where(x => x.TrackingCode != null && x.TrackingCode.ToUpper() == upper)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        if (trackingIds.Length > 0)
        {
            return await BuildInventoryUnitMatchesAsync(
                trackingIds,
                ScannerResolutionNamespace.TrackingCode,
                cancellationToken);
        }

        // 2. Serial Number
        var serialIds = await _db.InventoryUnits
            .AsNoTracking()
            .Where(x => x.SerialNumber != null && x.SerialNumber.ToUpper() == upper)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        if (serialIds.Length > 0)
        {
            return await BuildInventoryUnitMatchesAsync(
                serialIds,
                ScannerResolutionNamespace.SerialNumber,
                cancellationToken);
        }

        // 3. IMEI (authoritative ScannerResolutionNamespace.ManufacturerSerialOrImei tier)
        var imeiIds = digits.Length > 0
            ? await _db.InventoryUnits
                .AsNoTracking()
                .Where(x => x.Imei1 == digits || x.Imei2 == digits)
                .Select(x => x.Id)
                .ToArrayAsync(cancellationToken)
            : Array.Empty<Guid>();
        if (imeiIds.Length > 0)
        {
            return await BuildInventoryUnitMatchesAsync(
                imeiIds,
                ScannerResolutionNamespace.Imei,
                cancellationToken);
        }

        var barcodeMatches = await (
            from barcode in _db.ProductUnitBarcodes.AsNoTracking()
            join productUnit in _db.ProductUnits.AsNoTracking()
                on barcode.ProductUnitId equals productUnit.Id
            join product in _db.Products.AsNoTracking()
                on productUnit.ProductId equals product.Id
            join unit in _db.Units.AsNoTracking()
                on productUnit.UnitId equals unit.Id
            join stockJoin in _db.StockBalances.AsNoTracking()
                on product.Id equals stockJoin.ProductId into stockGroup
            from stock in stockGroup.DefaultIfEmpty()
            join categoryJoin in _db.Categories.AsNoTracking()
                on product.CategoryId equals categoryJoin.Id into categoryGroup
            from category in categoryGroup.DefaultIfEmpty()
            where barcode.IsActive &&
                  barcode.Barcode.ToUpper() == upper &&
                  product.IsActive &&
                  productUnit.IsActive &&
                  productUnit.CanSell
            orderby product.Name, productUnit.Id
            select new ScannerProductMatchDto(
                ScannerResolutionNamespace.ProductUnitBarcode,
                product.Id,
                productUnit.Id,
                product.Name,
                product.Sku,
                product.Brand,
                category == null ? "Uncategorized" : category.Name,
                unit.Symbol,
                stock == null ? 0m : stock.SellableQty,
                decimal.Round(
                    product.DefaultSalePrice * productUnit.FactorToBaseUnit,
                    2,
                    MidpointRounding.AwayFromZero),
                product.TrackingMode == TrackingMode.Serialized,
                null, null, null, null, null, null))
            .Take(20)
            .ToArrayAsync(cancellationToken);
        if (barcodeMatches.Length > 0)
        {
            return barcodeMatches;
        }

        var skuMatches = await BuildProductMatchesAsync(
            ScannerResolutionNamespace.ProductBarcode,
            p => p.Sku != null && p.Sku.ToUpper() == upper,
            cancellationToken);
        if (skuMatches.Count > 0)
        {
            return skuMatches;
        }

        return await BuildProductMatchesAsync(
            ScannerResolutionNamespace.BroaderSearch,
            p => p.Name.ToUpper().Contains(upper) ||
                 (p.Sku != null && p.Sku.ToUpper().Contains(upper)) ||
                 (p.Brand != null && p.Brand.ToUpper().Contains(upper)) ||
                 (p.Model != null && p.Model.ToUpper().Contains(upper)),
            cancellationToken);
    }

    public async Task<IReadOnlyList<PosDraftSummaryDto>> GetOpenDraftsAsync(
        CancellationToken cancellationToken)
    {
        return await _db.PosDrafts
            .AsNoTracking()
            .Where(x => x.Status == PosDraftStatus.Open)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => new PosDraftSummaryDto(
                x.Id,
                x.DraftNumber,
                x.CustomerId,
                x.CustomerId == null
                    ? "Walk-in Customer"
                    : _db.Customers
                        .Where(c => c.Id == x.CustomerId.Value)
                        .Select(c => c.Name)
                        .FirstOrDefault() ?? "Unknown Customer",
                x.CreatedBy,
                x.TerminalId,
                x.Note,
                x.UpdatedAt,
                x.Version,
                _db.PosDraftItems.Count(i => i.DraftId == x.Id)))
            .Take(100)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<PosDraftDetailDto?> GetDraftAsync(
        Guid draftId,
        CancellationToken cancellationToken)
    {
        var summary = await _db.PosDrafts
            .AsNoTracking()
            .Where(x => x.Id == draftId && x.Status == PosDraftStatus.Open)
            .Select(x => new PosDraftSummaryDto(
                x.Id,
                x.DraftNumber,
                x.CustomerId,
                x.CustomerId == null
                    ? "Walk-in Customer"
                    : _db.Customers
                        .Where(c => c.Id == x.CustomerId.Value)
                        .Select(c => c.Name)
                        .FirstOrDefault() ?? "Unknown Customer",
                x.CreatedBy,
                x.TerminalId,
                x.Note,
                x.UpdatedAt,
                x.Version,
                _db.PosDraftItems.Count(i => i.DraftId == x.Id)))
            .SingleOrDefaultAsync(cancellationToken);
        if (summary is null)
        {
            return null;
        }

        var rows = await (
            from item in _db.PosDraftItems.AsNoTracking()
            join product in _db.Products.AsNoTracking()
                on item.ProductId equals product.Id
            join productUnit in _db.ProductUnits.AsNoTracking()
                on item.ProductUnitId equals productUnit.Id
            join unit in _db.Units.AsNoTracking()
                on productUnit.UnitId equals unit.Id
            where item.DraftId == draftId
            orderby item.Id
            select new
            {
                Item = item,
                ProductName = product.Name,
                product.Sku,
                UnitSymbol = unit.Symbol
            })
            .ToArrayAsync(cancellationToken);

        var selectedIds = rows
            .Where(x => x.Item.SelectedInventoryUnitId is not null)
            .Select(x => x.Item.SelectedInventoryUnitId!.Value)
            .Distinct()
            .ToArray();

        var selectedUnits = selectedIds.Length == 0
            ? Array.Empty<ExactInventoryUnitDto>()
            : await ProjectUnitsAsync(
                await _db.InventoryUnits
                    .AsNoTracking()
                    .Where(x => selectedIds.Contains(x.Id))
                    .ToArrayAsync(cancellationToken),
                cancellationToken);
        var unitById = selectedUnits.ToDictionary(x => x.InventoryUnitId);

        var items = rows.Select(x =>
        {
            ExactInventoryUnitDto? exact = null;
            if (x.Item.SelectedInventoryUnitId is Guid selectedId)
            {
                unitById.TryGetValue(selectedId, out exact);
            }

            return new PosDraftLineDto(
                x.Item.Id,
                x.Item.ProductId,
                x.Item.ProductUnitId,
                x.ProductName,
                x.Sku,
                x.UnitSymbol,
                x.Item.EnteredQuantity,
                x.Item.DisplayedUnitPriceSnapshot,
                x.Item.SelectedInventoryUnitId,
                exact);
        }).ToArray();

        return new PosDraftDetailDto(summary, items);
    }

    public async Task<StocktakeSnapshotDto?> GetOpenStocktakeAsync(
        CancellationToken cancellationToken)
    {
        var stocktake = await _db.Stocktakes
            .AsNoTracking()
            .Where(x => x.Status == StocktakeStatus.Draft ||
                        x.Status == StocktakeStatus.Counting ||
                        x.Status == StocktakeStatus.Review)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (stocktake is null)
        {
            return null;
        }

        var items = await (
            from item in _db.StocktakeItems.AsNoTracking()
            join product in _db.Products.AsNoTracking()
                on item.ProductId equals product.Id
            where item.StocktakeId == stocktake.Id
            orderby product.Name, product.Id
            select new StocktakeLineDto(
                item.Id,
                product.Id,
                product.Name,
                product.Sku,
                product.TrackingMode == TrackingMode.Serialized,
                item.ExpectedSellableQty,
                item.CountedSellableQty,
                item.CountedSellableQty == null
                    ? 0m
                    : item.CountedSellableQty.Value - item.ExpectedSellableQty,
                item.ReviewNote))
            .ToArrayAsync(cancellationToken);

        return new StocktakeSnapshotDto(
            stocktake.Id,
            stocktake.Scope,
            stocktake.Status,
            stocktake.CreatedAt,
            stocktake.StartedAt,
            stocktake.ReviewAt,
            stocktake.Note,
            stocktake.Version,
            items);
    }

    private async Task<IReadOnlyList<ScannerProductMatchDto>> BuildInventoryUnitMatchesAsync(
        IReadOnlyCollection<Guid> inventoryUnitIds,
        ScannerResolutionNamespace resolutionNamespace,
        CancellationToken cancellationToken)
    {
        var units = await (
            from inventoryUnit in _db.InventoryUnits.AsNoTracking()
            join product in _db.Products.AsNoTracking()
                on inventoryUnit.ProductId equals product.Id
            join productUnit in _db.ProductUnits.AsNoTracking()
                on product.Id equals productUnit.ProductId
            join unit in _db.Units.AsNoTracking()
                on productUnit.UnitId equals unit.Id
            join stockJoin in _db.StockBalances.AsNoTracking()
                on product.Id equals stockJoin.ProductId into stockGroup
            from stock in stockGroup.DefaultIfEmpty()
            join categoryJoin in _db.Categories.AsNoTracking()
                on product.CategoryId equals categoryJoin.Id into categoryGroup
            from category in categoryGroup.DefaultIfEmpty()
            where inventoryUnitIds.Contains(inventoryUnit.Id) &&
                  product.IsActive &&
                  productUnit.IsActive &&
                  productUnit.CanSell &&
                  productUnit.IsDefaultSaleUnit
            orderby inventoryUnit.Id
            select new
            {
                InventoryUnit = inventoryUnit,
                Product = product,
                ProductUnit = productUnit,
                CategoryName = category == null ? "Uncategorized" : category.Name,
                UnitSymbol = unit.Symbol,
                SellableStock = stock == null ? 0m : stock.SellableQty
            })
            .ToArrayAsync(cancellationToken);

        if (units.Length == 0)
        {
            return Array.Empty<ScannerProductMatchDto>();
        }

        var sourceItemIds = units
            .Where(x => x.InventoryUnit.SourcePurchaseItemId != null)
            .Select(x => x.InventoryUnit.SourcePurchaseItemId!.Value)
            .Distinct()
            .ToArray();

        var provenance = sourceItemIds.Length == 0
            ? new Dictionary<Guid, (string PurchaseNumber, string SupplierName, Guid SupplierId)>()
            : await (
                from item in _db.PurchaseItems.AsNoTracking()
                join purchase in _db.Purchases.AsNoTracking()
                    on item.PurchaseId equals purchase.Id
                join supplier in _db.Suppliers.AsNoTracking()
                    on purchase.SupplierId equals supplier.Id
                where sourceItemIds.Contains(item.Id)
                select new
                {
                    item.Id,
                    purchase.PurchaseNumber,
                    SupplierName = supplier.Name,
                    purchase.SupplierId
                })
                .ToDictionaryAsync(
                    x => x.Id,
                    x => (x.PurchaseNumber, x.SupplierName, x.SupplierId),
                    cancellationToken);

        return units.Select(x =>
        {
            var u = x.InventoryUnit;
            var prov = u.SourcePurchaseItemId is Guid sourceId && provenance.TryGetValue(sourceId, out var p)
                ? (p.PurchaseNumber, p.SupplierName, (Guid?)p.SupplierId)
                : ((string?)null, (string?)null, (Guid?)null);

            var isSellable = u.Status == InventoryUnitStatus.InStock;

            return new ScannerProductMatchDto(
                resolutionNamespace,
                x.Product.Id,
                x.ProductUnit.Id,
                x.Product.Name,
                x.Product.Sku,
                x.Product.Brand,
                x.CategoryName,
                x.UnitSymbol,
                x.SellableStock,
                decimal.Round(
                    x.Product.DefaultSalePrice * x.ProductUnit.FactorToBaseUnit,
                    2,
                    MidpointRounding.AwayFromZero),
                x.Product.TrackingMode == TrackingMode.Serialized,
                u.Id,
                u.TrackingCode,
                u.SerialNumber,
                u.Imei1,
                u.Imei2,
                u.Status,
                IsSellable: isSellable,
                SupplierName: prov.Item2,
                PurchaseNumber: prov.Item1,
                SourcePurchaseItemId: u.SourcePurchaseItemId,
                SupplierId: prov.Item3);
        }).ToArray();
    }

    private async Task<IReadOnlyList<ScannerProductMatchDto>> BuildProductMatchesAsync(
        ScannerResolutionNamespace resolutionNamespace,
        System.Linq.Expressions.Expression<Func<Product, bool>> predicate,
        CancellationToken cancellationToken)
    {
        return await (
            from product in _db.Products.AsNoTracking().Where(predicate)
            join productUnit in _db.ProductUnits.AsNoTracking()
                on product.Id equals productUnit.ProductId
            join unit in _db.Units.AsNoTracking()
                on productUnit.UnitId equals unit.Id
            join stockJoin in _db.StockBalances.AsNoTracking()
                on product.Id equals stockJoin.ProductId into stockGroup
            from stock in stockGroup.DefaultIfEmpty()
            join categoryJoin in _db.Categories.AsNoTracking()
                on product.CategoryId equals categoryJoin.Id into categoryGroup
            from category in categoryGroup.DefaultIfEmpty()
            where product.IsActive &&
                  productUnit.IsActive &&
                  productUnit.CanSell &&
                  productUnit.IsDefaultSaleUnit
            orderby product.Name, product.Id
            select new ScannerProductMatchDto(
                resolutionNamespace,
                product.Id,
                productUnit.Id,
                product.Name,
                product.Sku,
                product.Brand,
                category == null ? "Uncategorized" : category.Name,
                unit.Symbol,
                stock == null ? 0m : stock.SellableQty,
                decimal.Round(
                    product.DefaultSalePrice * productUnit.FactorToBaseUnit,
                    2,
                    MidpointRounding.AwayFromZero),
                product.TrackingMode == TrackingMode.Serialized,
                null, null, null, null, null, null))
            .Take(20)
            .ToArrayAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ExactInventoryUnitDto>> ProjectUnitsAsync(
        IReadOnlyCollection<InventoryUnit> units,
        CancellationToken cancellationToken)
    {
        if (units.Count == 0)
        {
            return Array.Empty<ExactInventoryUnitDto>();
        }

        var productIds = units.Select(x => x.ProductId).Distinct().ToArray();
        var products = await _db.Products
            .AsNoTracking()
            .Where(x => productIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var purchaseItemIds = units
            .Where(x => x.SourcePurchaseItemId is not null)
            .Select(x => x.SourcePurchaseItemId!.Value)
            .Distinct()
            .ToArray();

        var provenance = purchaseItemIds.Length == 0
            ? new Dictionary<Guid, (string PurchaseNumber, string SupplierName)>()
            : await (
                from item in _db.PurchaseItems.AsNoTracking()
                join purchase in _db.Purchases.AsNoTracking()
                    on item.PurchaseId equals purchase.Id
                join supplier in _db.Suppliers.AsNoTracking()
                    on purchase.SupplierId equals supplier.Id
                where purchaseItemIds.Contains(item.Id)
                select new { item.Id, purchase.PurchaseNumber, SupplierName = supplier.Name })
                .ToDictionaryAsync(
                    x => x.Id,
                    x => (x.PurchaseNumber, x.SupplierName),
                    cancellationToken);

        return units
            .Select(x =>
            {
                products.TryGetValue(x.ProductId, out var product);
                var source = x.SourcePurchaseItemId is Guid sourceId &&
                             provenance.TryGetValue(sourceId, out var found)
                    ? ((string?)found.PurchaseNumber, (string?)found.SupplierName)
                    : ((string?)null, (string?)null);

                return new ExactInventoryUnitDto(
                    x.Id,
                    x.ProductId,
                    product?.Name ?? "Unknown Product",
                    product?.Sku,
                    x.TrackingCode,
                    x.SerialNumber,
                    x.Imei1,
                    x.Imei2,
                    x.Status,
                    x.AcquisitionCost,
                    x.SourcePurchaseItemId,
                    source.Item1,
                    source.Item2,
                    x.CreatedAt,
                    x.Version);
            })
            .ToArray();
    }
}
