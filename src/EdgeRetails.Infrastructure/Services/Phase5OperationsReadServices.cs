using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class SupplierAccountReadService : ISupplierAccountReadService
{
    private readonly EdgeRetailsDbContext _db;

    public SupplierAccountReadService(EdgeRetailsDbContext db) => _db = db;

    public async Task<SupplierAccountWorkspaceDto> GetWorkspaceAsync(
        Guid supplierId,
        int pageSize = 200,
        DateTimeOffset? beforeOccurredAt = null,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeEntryId = null,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(pageSize, 1, 200);

        var statementQuery = _db.SupplierAccountEntries.AsNoTracking()
            .Where(x => x.SupplierId == supplierId);

        if (beforeOccurredAt is DateTimeOffset occurredAt &&
            beforeCreatedAt is DateTimeOffset createdAt &&
            beforeEntryId is Guid entryId)
        {
            statementQuery = statementQuery.Where(x =>
                x.OccurredAt < occurredAt ||
                (x.OccurredAt == occurredAt && x.CreatedAt < createdAt) ||
                (x.OccurredAt == occurredAt && x.CreatedAt == createdAt && x.Id < entryId));
        }

        var pageEntries = await statementQuery
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        var openingBalance = 0m;
        if (pageEntries.Count > 0)
        {
            var oldest = pageEntries[^1];
            openingBalance = await _db.SupplierAccountEntries.AsNoTracking()
                .Where(x => x.SupplierId == supplierId &&
                    (x.OccurredAt < oldest.OccurredAt ||
                     (x.OccurredAt == oldest.OccurredAt && x.CreatedAt < oldest.CreatedAt) ||
                     (x.OccurredAt == oldest.OccurredAt && x.CreatedAt == oldest.CreatedAt && x.Id < oldest.Id)))
                .Select(x => (decimal?)x.SignedAmount)
                .SumAsync(cancellationToken) ?? 0m;
        }

        var running = openingBalance;
        var statement = pageEntries
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(entry =>
            {
                running += entry.SignedAmount;
                return new SupplierKhataEntryDto(
                    entry.Id,
                    entry.EntryNumber,
                    entry.EntryType,
                    entry.Direction,
                    entry.Amount,
                    entry.SignedAmount,
                    decimal.Round(running, 2),
                    entry.ReferenceType,
                    entry.ReferenceId,
                    entry.OccurredAt,
                    entry.ActorId,
                    entry.ClientOperationId,
                    entry.Note);
            })
            .ToArray();

        var aggregate = await _db.SupplierAccountEntries.AsNoTracking()
            .Where(x => x.SupplierId == supplierId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Balance = group.Sum(x => x.SignedAmount),
                GrossPurchased = group.Where(x => x.EntryType == SupplierAccountEntryType.Purchase).Sum(x => x.Amount),
                PurchaseReturnCredits = group.Where(x => x.EntryType == SupplierAccountEntryType.PurchaseReturnCredit).Sum(x => x.Amount),
                PurchaseVoidReversals = group.Where(x => x.EntryType == SupplierAccountEntryType.PurchaseVoidReversal).Sum(x => x.Amount),
                WarrantyCredits = group.Where(x => x.EntryType == SupplierAccountEntryType.WarrantyCredit).Sum(x => x.Amount),
                NetPaid = group.Where(x => x.EntryType == SupplierAccountEntryType.SupplierPayment).Sum(x => x.Amount) -
                          group.Where(x => x.EntryType == SupplierAccountEntryType.SupplierPaymentReversal).Sum(x => x.Amount),
                NetRefunds = group.Where(x => x.EntryType == SupplierAccountEntryType.SupplierRefundReceived).Sum(x => x.Amount) -
                             group.Where(x => x.EntryType == SupplierAccountEntryType.SupplierRefundReversal).Sum(x => x.Amount)
            })
            .SingleOrDefaultAsync(cancellationToken);

        var balance = decimal.Round(aggregate?.Balance ?? 0m, 2);
        var grossPurchased = decimal.Round(aggregate?.GrossPurchased ?? 0m, 2);
        var returnCredits = decimal.Round(aggregate?.PurchaseReturnCredits ?? 0m, 2);
        var voidReversals = decimal.Round(aggregate?.PurchaseVoidReversals ?? 0m, 2);
        var warrantyCredits = decimal.Round(aggregate?.WarrantyCredits ?? 0m, 2);
        var netPurchased = decimal.Round(grossPurchased - returnCredits - voidReversals, 2);
        var netPaid = decimal.Round(aggregate?.NetPaid ?? 0m, 2);
        var netRefunds = decimal.Round(aggregate?.NetRefunds ?? 0m, 2);

        var payments = await _db.SupplierPayments.AsNoTracking()
            .Where(x => x.SupplierId == supplierId)
            .OrderByDescending(x => x.PaidAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new SupplierPaymentReadDto(
                x.Id,
                x.PaymentNumber,
                x.Amount,
                x.Purpose,
                x.Method,
                x.PaidAt,
                x.Status,
                x.ExternalReference,
                x.Note))
            .ToListAsync(cancellationToken);

        var refunds = await _db.SupplierRefunds.AsNoTracking()
            .Where(x => x.SupplierId == supplierId)
            .OrderByDescending(x => x.ReceivedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new SupplierRefundReadDto(
                x.Id,
                x.RefundNumber,
                x.Amount,
                x.Method,
                x.ReceivedAt,
                x.Status,
                x.ExternalReference,
                x.ReferenceType,
                x.ReferenceId,
                x.Note))
            .ToListAsync(cancellationToken);

        var products = await (
            from supplierProduct in _db.SupplierProducts.AsNoTracking()
            join product in _db.Products.AsNoTracking()
                on supplierProduct.ProductId equals product.Id
            where supplierProduct.SupplierId == supplierId
            orderby product.Name, product.Id
            select new SupplierProductContextDto(
                supplierProduct.Id,
                product.Id,
                product.Name,
                product.Sku,
                supplierProduct.IsActive))
            .Take(take)
            .ToListAsync(cancellationToken);

        var customerClaims = _db.WarrantyClaims.AsNoTracking()
            .Where(x => x.SupplierId == supplierId);
        var shopCases = _db.ShopStockWarrantyCases.AsNoTracking()
            .Where(x => x.SupplierId == supplierId);

        var customerClaimCount = await customerClaims.CountAsync(cancellationToken);
        var openCustomerClaims = await customerClaims.CountAsync(
            x => x.Status != WarrantyClaimStatus.Closed && x.Status != WarrantyClaimStatus.Cancelled,
            cancellationToken);
        var withSupplierClaims = await customerClaims.CountAsync(
            x => x.CurrentCustody == WarrantyCustody.WithSupplier,
            cancellationToken);
        var readyClaims = await customerClaims.CountAsync(
            x => x.Status == WarrantyClaimStatus.ReadyForCustomer,
            cancellationToken);
        var closedClaims = await customerClaims.CountAsync(
            x => x.Status == WarrantyClaimStatus.Closed || x.Status == WarrantyClaimStatus.Cancelled,
            cancellationToken);

        var shopCaseCount = await shopCases.CountAsync(cancellationToken);
        var openShopCases = await shopCases.CountAsync(
            x => x.Status != ShopWarrantyCaseStatus.Closed && x.Status != ShopWarrantyCaseStatus.WrittenOff,
            cancellationToken);
        var withSupplierCases = await shopCases.CountAsync(
            x => x.Status == ShopWarrantyCaseStatus.WithSupplier,
            cancellationToken);
        var readyCases = await shopCases.CountAsync(
            x => x.Status == ShopWarrantyCaseStatus.ReadyForStock,
            cancellationToken);
        var closedCases = await shopCases.CountAsync(
            x => x.Status == ShopWarrantyCaseStatus.Closed || x.Status == ShopWarrantyCaseStatus.WrittenOff,
            cancellationToken);

        var trackedCustomerUnits = await (
            from link in _db.WarrantyClaimItemUnits.AsNoTracking()
            join item in _db.WarrantyClaimItems.AsNoTracking() on link.ClaimItemId equals item.Id
            join claim in _db.WarrantyClaims.AsNoTracking() on item.ClaimId equals claim.Id
            where claim.SupplierId == supplierId && link.OriginalInventoryUnitId != null
            select link.OriginalInventoryUnitId!.Value)
            .Distinct()
            .CountAsync(cancellationToken);

        var trackedShopUnits = await (
            from unit in _db.InventoryUnits.AsNoTracking()
            join warrantyCase in _db.ShopStockWarrantyCases.AsNoTracking()
                on unit.SourceWarrantyCaseId equals warrantyCase.Id
            where warrantyCase.SupplierId == supplierId
            select unit.Id)
            .Distinct()
            .CountAsync(cancellationToken);

        return new SupplierAccountWorkspaceDto(
            new SupplierAccountSummaryDto(
                supplierId,
                balance,
                grossPurchased,
                returnCredits,
                voidReversals,
                warrantyCredits,
                netPurchased,
                netPaid,
                Math.Max(balance, 0m),
                Math.Max(-balance, 0m),
                netRefunds),
            statement,
            payments,
            refunds,
            products,
            new SupplierWarrantySummaryDto(
                customerClaimCount,
                shopCaseCount,
                trackedCustomerUnits + trackedShopUnits,
                withSupplierClaims + withSupplierCases,
                readyClaims + readyCases,
                closedClaims + closedCases));
    }
}

public sealed class WarrantyReadService : IWarrantyReadService
{
    private readonly EdgeRetailsDbContext _db;
    private readonly IClock? _clock;

    public WarrantyReadService(EdgeRetailsDbContext db, IClock? clock = null)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<WarrantyDashboardDto> GetDashboardAsync(
        string? search,
        int pageSize = 100,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeWorkId = null,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(pageSize, 1, 200);
        var term = search?.Trim();

        var claimsBase = _db.WarrantyClaims.AsNoTracking();
        if (beforeCreatedAt is DateTimeOffset cursorAt && beforeWorkId is Guid cursorId)
        {
            claimsBase = claimsBase.Where(x =>
                x.ReceivedAt < cursorAt ||
                (x.ReceivedAt == cursorAt && x.Id < cursorId));
        }

        if (!string.IsNullOrWhiteSpace(term))
        {
            claimsBase = claimsBase.Where(c =>
                c.ClaimNumber.Contains(term) ||
                _db.Customers.Any(customer =>
                    customer.Id == c.CustomerId &&
                    (customer.Name.Contains(term) ||
                     (customer.Phone != null && customer.Phone.Contains(term)))) ||
                (c.SupplierId != null && _db.Suppliers.Any(supplier =>
                    supplier.Id == c.SupplierId.Value && supplier.Name.Contains(term))) ||
                _db.WarrantyClaimItems.Any(item =>
                    item.ClaimId == c.Id &&
                    _db.Products.Any(product =>
                        product.Id == item.ProductId &&
                        (product.Name.Contains(term) || (product.Sku != null && product.Sku.Contains(term))))) ||
                _db.WarrantyClaimItemUnits.Any(link =>
                    _db.WarrantyClaimItems.Any(item => item.Id == link.ClaimItemId && item.ClaimId == c.Id) &&
                    link.OriginalInventoryUnitId != null &&
                    _db.InventoryUnits.Any(unit =>
                        unit.Id == link.OriginalInventoryUnitId.Value &&
                        ((unit.TrackingCode != null && unit.TrackingCode.Contains(term)) ||
                         (unit.SerialNumber != null && unit.SerialNumber.Contains(term)) ||
                         (unit.Imei1 != null && unit.Imei1.Contains(term)) ||
                         (unit.Imei2 != null && unit.Imei2.Contains(term))))));
        }

        var claimRows = await claimsBase
            .OrderByDescending(x => x.ReceivedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .Select(claim => new
            {
                claim.Id,
                claim.ClaimNumber,
                claim.CustomerId,
                claim.SupplierId,
                claim.ReceivedAt,
                claim.Status,
                claim.CurrentCustody,
                ProductName = _db.WarrantyClaimItems
                    .Where(item => item.ClaimId == claim.Id)
                    .OrderBy(item => item.Id)
                    .Join(_db.Products, item => item.ProductId, product => product.Id, (_, product) => product.Name)
                    .FirstOrDefault(),
                Resolution = _db.WarrantyClaimItems
                    .Where(item => item.ClaimId == claim.Id && item.ResolutionType != null)
                    .OrderBy(item => item.Id)
                    .Select(item => item.ResolutionType!.Value)
                    .FirstOrDefault(),
                CustomerName = _db.Customers
                    .Where(customer => customer.Id == claim.CustomerId)
                    .Select(customer => customer.Name)
                    .FirstOrDefault(),
                SupplierName = claim.SupplierId == null
                    ? null
                    : _db.Suppliers.Where(supplier => supplier.Id == claim.SupplierId.Value)
                        .Select(supplier => supplier.Name)
                        .FirstOrDefault(),
                TrackingCode = (from link in _db.WarrantyClaimItemUnits
                                join item in _db.WarrantyClaimItems on link.ClaimItemId equals item.Id
                                where item.ClaimId == claim.Id && link.OriginalInventoryUnitId != null
                                orderby link.Id
                                select _db.InventoryUnits
                                    .Where(unit => unit.Id == link.OriginalInventoryUnitId!.Value)
                                    .Select(unit => unit.TrackingCode)
                                    .FirstOrDefault()).FirstOrDefault(),
                SerialNumber = (from link in _db.WarrantyClaimItemUnits
                                join item in _db.WarrantyClaimItems on link.ClaimItemId equals item.Id
                                where item.ClaimId == claim.Id && link.OriginalInventoryUnitId != null
                                orderby link.Id
                                select _db.InventoryUnits
                                    .Where(unit => unit.Id == link.OriginalInventoryUnitId!.Value)
                                    .Select(unit => unit.SerialNumber)
                                    .FirstOrDefault()).FirstOrDefault(),
                Imei1 = (from link in _db.WarrantyClaimItemUnits
                         join item in _db.WarrantyClaimItems on link.ClaimItemId equals item.Id
                         where item.ClaimId == claim.Id && link.OriginalInventoryUnitId != null
                         orderby link.Id
                         select _db.InventoryUnits
                             .Where(unit => unit.Id == link.OriginalInventoryUnitId!.Value)
                             .Select(unit => unit.Imei1)
                             .FirstOrDefault()).FirstOrDefault(),
                Imei2 = (from link in _db.WarrantyClaimItemUnits
                         join item in _db.WarrantyClaimItems on link.ClaimItemId equals item.Id
                         where item.ClaimId == claim.Id && link.OriginalInventoryUnitId != null
                         orderby link.Id
                         select _db.InventoryUnits
                             .Where(unit => unit.Id == link.OriginalInventoryUnitId!.Value)
                             .Select(unit => unit.Imei2)
                             .FirstOrDefault()).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var caseBase = _db.ShopStockWarrantyCases.AsNoTracking();
        if (beforeCreatedAt is DateTimeOffset caseCursorAt && beforeWorkId is Guid caseCursorId)
        {
            caseBase = caseBase.Where(x =>
                x.CreatedAt < caseCursorAt ||
                (x.CreatedAt == caseCursorAt && x.Id < caseCursorId));
        }

        if (!string.IsNullOrWhiteSpace(term))
        {
            caseBase = caseBase.Where(c =>
                c.CaseNumber.Contains(term) ||
                c.FaultDescription.Contains(term) ||
                c.SupplierReference != null && c.SupplierReference.Contains(term) ||
                _db.Suppliers.Any(supplier => supplier.Id == c.SupplierId && supplier.Name.Contains(term)) ||
                _db.Products.Any(product => product.Id == c.ProductId &&
                    (product.Name.Contains(term) || (product.Sku != null && product.Sku.Contains(term)))) ||
                _db.InventoryUnits.Any(unit =>
                    unit.SourceWarrantyCaseId == c.Id &&
                    ((unit.TrackingCode != null && unit.TrackingCode.Contains(term)) ||
                     (unit.SerialNumber != null && unit.SerialNumber.Contains(term)) ||
                     (unit.Imei1 != null && unit.Imei1.Contains(term)) ||
                     (unit.Imei2 != null && unit.Imei2.Contains(term)))));
        }

        var caseRows = await caseBase
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .Select(warrantyCase => new
            {
                warrantyCase.Id,
                warrantyCase.CaseNumber,
                warrantyCase.ProductId,
                warrantyCase.SupplierId,
                warrantyCase.CreatedAt,
                warrantyCase.Status,
                warrantyCase.ResolutionType,
                ProductName = _db.Products
                    .Where(product => product.Id == warrantyCase.ProductId)
                    .Select(product => product.Name)
                    .FirstOrDefault(),
                SupplierName = _db.Suppliers
                    .Where(supplier => supplier.Id == warrantyCase.SupplierId)
                    .Select(supplier => supplier.Name)
                    .FirstOrDefault(),
                TrackingCode = _db.InventoryUnits
                    .Where(unit => unit.SourceWarrantyCaseId == warrantyCase.Id)
                    .OrderBy(unit => unit.Id)
                    .Select(unit => unit.TrackingCode)
                    .FirstOrDefault(),
                SerialNumber = _db.InventoryUnits
                    .Where(unit => unit.SourceWarrantyCaseId == warrantyCase.Id)
                    .OrderBy(unit => unit.Id)
                    .Select(unit => unit.SerialNumber)
                    .FirstOrDefault(),
                Imei1 = _db.InventoryUnits
                    .Where(unit => unit.SourceWarrantyCaseId == warrantyCase.Id)
                    .OrderBy(unit => unit.Id)
                    .Select(unit => unit.Imei1)
                    .FirstOrDefault(),
                Imei2 = _db.InventoryUnits
                    .Where(unit => unit.SourceWarrantyCaseId == warrantyCase.Id)
                    .OrderBy(unit => unit.Id)
                    .Select(unit => unit.Imei2)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var rows = claimRows.Select(x => new WarrantyQueueRowDto(
                WarrantyWorkKind.CustomerClaim,
                x.Id,
                x.ClaimNumber,
                x.ProductName ?? "Unknown product",
                x.CustomerName ?? "Unknown customer",
                x.SupplierName ?? "-",
                x.ReceivedAt,
                x.Status.ToString(),
                x.CurrentCustody.ToString(),
                x.Resolution == default ? "Pending" : x.Resolution.ToString(),
                x.TrackingCode,
                x.SerialNumber,
                x.Imei1,
                x.Imei2))
            .Concat(caseRows.Select(x => new WarrantyQueueRowDto(
                WarrantyWorkKind.ShopStock,
                x.Id,
                x.CaseNumber,
                x.ProductName ?? "Unknown product",
                "Shop Stock",
                x.SupplierName ?? "Unknown supplier",
                x.CreatedAt,
                x.Status.ToString(),
                x.Status == ShopWarrantyCaseStatus.WithSupplier ? "WithSupplier" : "WithShop",
                x.ResolutionType?.ToString() ?? "Pending",
                x.TrackingCode,
                x.SerialNumber,
                x.Imei1,
                x.Imei2)))
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Kind)
            .ThenByDescending(x => x.WorkId)
            .Take(take)
            .ToArray();

        var claimCount = await _db.WarrantyClaims.CountAsync(cancellationToken);
        var openClaims = await _db.WarrantyClaims.CountAsync(
            x => x.Status != WarrantyClaimStatus.Closed && x.Status != WarrantyClaimStatus.Cancelled,
            cancellationToken);
        var supplierClaims = await _db.WarrantyClaims.CountAsync(
            x => x.CurrentCustody == WarrantyCustody.WithSupplier,
            cancellationToken);
        var readyClaims = await _db.WarrantyClaims.CountAsync(
            x => x.Status == WarrantyClaimStatus.ReadyForCustomer,
            cancellationToken);
        var closedClaims = await _db.WarrantyClaims.CountAsync(
            x => x.Status == WarrantyClaimStatus.Closed || x.Status == WarrantyClaimStatus.Cancelled,
            cancellationToken);

        var caseCount = await _db.ShopStockWarrantyCases.CountAsync(cancellationToken);
        var openCases = await _db.ShopStockWarrantyCases.CountAsync(
            x => x.Status != ShopWarrantyCaseStatus.Closed && x.Status != ShopWarrantyCaseStatus.WrittenOff,
            cancellationToken);
        var supplierCases = await _db.ShopStockWarrantyCases.CountAsync(
            x => x.Status == ShopWarrantyCaseStatus.WithSupplier,
            cancellationToken);
        var readyCases = await _db.ShopStockWarrantyCases.CountAsync(
            x => x.Status == ShopWarrantyCaseStatus.ReadyForStock,
            cancellationToken);
        var closedCases = await _db.ShopStockWarrantyCases.CountAsync(
            x => x.Status == ShopWarrantyCaseStatus.Closed || x.Status == ShopWarrantyCaseStatus.WrittenOff,
            cancellationToken);

        var trackedCustomerOriginal = _db.WarrantyClaimItemUnits.AsNoTracking()
            .Where(x => x.OriginalInventoryUnitId != null)
            .Select(x => x.OriginalInventoryUnitId!.Value);
        var trackedCustomerReplacement = _db.WarrantyClaimItemUnits.AsNoTracking()
            .Where(x => x.ReplacementInventoryUnitId != null)
            .Select(x => x.ReplacementInventoryUnitId!.Value);
        var trackedShop = _db.InventoryUnits.AsNoTracking()
            .Where(x => x.SourceWarrantyCaseId != null)
            .Select(x => x.Id);
        var trackedUnitCount = await trackedCustomerOriginal
            .Union(trackedCustomerReplacement)
            .Union(trackedShop)
            .CountAsync(cancellationToken);

        return new WarrantyDashboardDto(
            new WarrantyDashboardSummaryDto(
                openClaims + openCases,
                supplierClaims + supplierCases,
                readyClaims + readyCases,
                closedClaims + closedCases,
                claimCount,
                caseCount,
                trackedUnitCount),
            rows);
    }

    public async Task<IReadOnlyList<WarrantyEventDto>> GetClaimTimelineAsync(
        Guid claimId,
        CancellationToken cancellationToken) =>
        await _db.WarrantyClaimEvents.AsNoTracking()
            .Where(x => x.ClaimId == claimId)
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .Select(x => new WarrantyEventDto(
                x.Id,
                x.ClaimId,
                x.Status,
                x.Custody,
                x.EventType,
                x.Note,
                x.ActorId,
                x.OccurredAt))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WarrantyClaimIntakeRowDto>> SearchClaimIntakeAsync(
        string search,
        CancellationToken cancellationToken)
    {
        var term = search.Trim();
        if (term.Length < 2)
        {
            return Array.Empty<WarrantyClaimIntakeRowDto>();
        }

        var saleIds = new HashSet<Guid>(
            await _db.Sales.AsNoTracking()
                .Where(x => x.Status == EdgeRetails.Domain.Sales.SaleStatus.Completed &&
                    x.InvoiceNumber.Contains(term))
                .Select(x => x.Id)
                .Take(100)
                .ToListAsync(cancellationToken));

        var customerIds = await _db.Customers.AsNoTracking()
            .Where(x => x.Name.Contains(term) || (x.Phone != null && x.Phone.Contains(term)))
            .Select(x => x.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (customerIds.Count > 0)
        {
            saleIds.UnionWith(await _db.Sales.AsNoTracking()
                .Where(x => x.Status == EdgeRetails.Domain.Sales.SaleStatus.Completed &&
                            x.CustomerId != null && customerIds.Contains(x.CustomerId.Value))
                .Select(x => x.Id)
                .Take(100)
                .ToListAsync(cancellationToken));
        }

        var productIds = await _db.Products.AsNoTracking()
            .Where(x => x.Name.Contains(term) || (x.Sku != null && x.Sku.Contains(term)))
            .Select(x => x.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (productIds.Count > 0)
        {
            saleIds.UnionWith(await _db.SaleItems.AsNoTracking()
                .Where(x => productIds.Contains(x.ProductId))
                .Select(x => x.SaleId)
                .Distinct()
                .Take(100)
                .ToListAsync(cancellationToken));
        }

        var matchingUnitIds = await _db.InventoryUnits.AsNoTracking()
            .Where(x => (x.TrackingCode != null && x.TrackingCode.Contains(term)) ||
                        (x.SerialNumber != null && x.SerialNumber.Contains(term)) ||
                        (x.Imei1 != null && x.Imei1.Contains(term)) ||
                        (x.Imei2 != null && x.Imei2.Contains(term)))
            .Select(x => x.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (matchingUnitIds.Count > 0)
        {
            saleIds.UnionWith(await (
                from link in _db.SaleItemUnits.AsNoTracking()
                join item in _db.SaleItems.AsNoTracking() on link.SaleItemId equals item.Id
                where matchingUnitIds.Contains(link.InventoryUnitId)
                select item.SaleId)
                .Distinct()
                .Take(100)
                .ToListAsync(cancellationToken));
        }

        if (saleIds.Count == 0)
        {
            return Array.Empty<WarrantyClaimIntakeRowDto>();
        }

        var sales = await _db.Sales.AsNoTracking()
            .Where(x => saleIds.Contains(x.Id) && x.Status == EdgeRetails.Domain.Sales.SaleStatus.Completed)
            .OrderByDescending(x => x.CompletedAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        var matchingSaleIds = sales.Select(s => s.Id).ToArray();
        var items = await _db.SaleItems.AsNoTracking()
            .Where(x => matchingSaleIds.Contains(x.SaleId))
            .ToListAsync(cancellationToken);
        var saleItemIds = items.Select(i => i.Id).Distinct().ToArray();
        var links = await _db.SaleItemUnits.AsNoTracking()
            .Where(x => saleItemIds.Contains(x.SaleItemId))
            .ToListAsync(cancellationToken);
        var unitIds = links.Select(x => x.InventoryUnitId).Distinct().ToArray();
        var units = await _db.InventoryUnits.AsNoTracking()
            .Where(x => unitIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var products = await _db.Products.AsNoTracking()
            .Where(x => items.Select(i => i.ProductId).Distinct().Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var customers = await _db.Customers.AsNoTracking()
            .Where(x => sales.Where(s => s.CustomerId != null).Select(s => s.CustomerId!.Value).Distinct().Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var activeClaimItems = saleItemIds.Length == 0
            ? new List<WarrantyClaimItem>()
            : await (
                from item in _db.WarrantyClaimItems.AsNoTracking()
                join claim in _db.WarrantyClaims.AsNoTracking() on item.ClaimId equals claim.Id
                where claim.Status != WarrantyClaimStatus.Closed &&
                      claim.Status != WarrantyClaimStatus.Cancelled &&
                      item.OriginalSaleItemId != null &&
                      saleItemIds.Contains(item.OriginalSaleItemId!.Value)
                select item)
                .ToListAsync(cancellationToken);
        var activeClaimedBySaleItem = activeClaimItems
            .Where(x => x.OriginalSaleItemId != null)
            .GroupBy(x => x.OriginalSaleItemId!.Value)
            .ToDictionary(x => x.Key, x => QuantityMath.RoundQuantity(x.Sum(i => i.Quantity)));
        var activeClaimUnitIds = unitIds.Length == 0
            ? new HashSet<Guid>()
            : (await (
                from link in _db.WarrantyClaimItemUnits.AsNoTracking()
                join item in _db.WarrantyClaimItems.AsNoTracking() on link.ClaimItemId equals item.Id
                join claim in _db.WarrantyClaims.AsNoTracking() on item.ClaimId equals claim.Id
                where claim.Status != WarrantyClaimStatus.Closed &&
                      claim.Status != WarrantyClaimStatus.Cancelled &&
                      link.OriginalInventoryUnitId != null &&
                      unitIds.Contains(link.OriginalInventoryUnitId!.Value)
                select link.OriginalInventoryUnitId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();
        var terminalClaimItems = saleItemIds.Length == 0
            ? new List<WarrantyClaimItem>()
            : await (
                from item in _db.WarrantyClaimItems.AsNoTracking()
                join claim in _db.WarrantyClaims.AsNoTracking() on item.ClaimId equals claim.Id
                where claim.Status == WarrantyClaimStatus.Closed &&
                      item.OriginalSaleItemId != null &&
                      saleItemIds.Contains(item.OriginalSaleItemId!.Value) &&
                      (item.ResolutionType == WarrantyResolutionType.Replaced ||
                       item.ResolutionType == WarrantyResolutionType.Refunded)
                select item)
                .ToListAsync(cancellationToken);
        var terminalBySaleItem = terminalClaimItems
            .Where(x => x.OriginalSaleItemId != null)
            .GroupBy(x => x.OriginalSaleItemId!.Value)
            .ToDictionary(x => x.Key, x => QuantityMath.RoundQuantity(x.Sum(i => i.Quantity)));

        var today = _clock?.ShopDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var rows = new List<WarrantyClaimIntakeRowDto>();
        foreach (var sale in sales)
        {
            var customerName = sale.CustomerId is Guid customerId &&
                               customers.TryGetValue(customerId, out var customer)
                ? customer.Name
                : "No customer";
            foreach (var item in items.Where(x => x.SaleId == sale.Id))
            {
                var product = products.GetValueOrDefault(item.ProductId);
                var baseEligibility = sale.CustomerId != null
                    ? item.WarrantyValidUntil is not null
                        ? today <= item.WarrantyValidUntil.Value
                            ? (true, (string?)null)
                            : (false, "warranty.expired")
                        : (false, "warranty.not_covered")
                    : (false, "warranty.customer_required");

                var active = activeClaimedBySaleItem.GetValueOrDefault(item.Id);
                var terminal = terminalBySaleItem.GetValueOrDefault(item.Id);
                var remaining = QuantityMath.RoundQuantity(item.BaseQuantity - active - terminal);
                var unitsForItem = links.Where(x => x.SaleItemId == item.Id)
                    .Select(x => x.InventoryUnitId)
                    .Distinct()
                    .Where(units.ContainsKey)
                    .Select(id => units[id])
                    .Select(unit => new WarrantyClaimIntakeUnitDto(
                        unit.Id,
                        unit.TrackingCode,
                        unit.SerialNumber,
                        unit.Imei1,
                        unit.Imei2,
                        baseEligibility.Item1 &&
                        unit.Status == InventoryUnitStatus.Sold &&
                        !activeClaimUnitIds.Contains(unit.Id),
                        unit.Status != InventoryUnitStatus.Sold
                            ? "warranty.unit_no_longer_customer_owned"
                            : activeClaimUnitIds.Contains(unit.Id)
                                ? "warranty.active_claim_exists"
                                : null))
                    .ToArray();

                var eligible = baseEligibility.Item1 && remaining > 0;
                var code = baseEligibility.Item2;
                if (eligible && product?.TrackingMode == TrackingMode.Serialized && unitsForItem.Length == 0)
                {
                    eligible = false;
                    code = "warranty.serialized_units_missing";
                }
                rows.Add(new WarrantyClaimIntakeRowDto(
                    sale.Id,
                    sale.InvoiceNumber,
                    sale.CustomerId,
                    customerName,
                    item.Id,
                    item.ProductId,
                    product?.Name ?? item.ProductNameSnapshot,
                    item.BaseQuantity,
                    item.WarrantyValidUntil,
                    eligible,
                    code,
                    unitsForItem));
            }
        }

        return rows
            .OrderByDescending(x => x.Eligible)
            .ThenByDescending(x => x.WarrantyValidUntil)
            .Take(100)
            .ToArray();
    }

    private static bool Contains(string? source, string value) =>
        source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
}
