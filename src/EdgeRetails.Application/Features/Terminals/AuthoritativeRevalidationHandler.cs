using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Features.Terminals;

public sealed record RevalidatedProductState(
    Guid ProductId,
    string Name,
    string Sku,
    bool IsActive,
    decimal BasePrice,
    decimal SellableStock,
    long Version);

public sealed record RevalidatedSupplierBalance(
    Guid SupplierId,
    string SupplierName,
    decimal CurrentPayableBalance);

public sealed record AuthoritativeRevalidationQuery(
    Guid TerminalId,
    IReadOnlyList<Guid>? MonitoredProductIds = null,
    Guid? CurrentCashSessionId = null);

public sealed record AuthoritativeRevalidationResult(
    Guid TerminalId,
    TerminalStatus TerminalStatus,
    DateTimeOffset ServerTimeUtc,
    string ServerProtocolVersion,
    bool CashSessionIsOpen,
    Guid? ActiveCashSessionId,
    IReadOnlyList<Guid> ActiveStocktakeLockedProductIds,
    IReadOnlyList<RevalidatedProductState> ProductStates,
    IReadOnlyList<RevalidatedSupplierBalance> SupplierBalances);

public sealed class AuthoritativeRevalidationHandler
{
    private readonly ITerminalRepository _terminals;
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly ICashRepository _cash;
    private readonly IPartyRepository _parties;
    private readonly ISupplierAccountRepository _supplierAccounts;
    private readonly IClock _clock;

    public AuthoritativeRevalidationHandler(
        ITerminalRepository terminals,
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        ICashRepository cash,
        IPartyRepository parties,
        ISupplierAccountRepository supplierAccounts,
        IClock clock)
    {
        _terminals = terminals;
        _catalog = catalog;
        _inventory = inventory;
        _cash = cash;
        _parties = parties;
        _supplierAccounts = supplierAccounts;
        _clock = clock;
    }

    public async Task<Result<AuthoritativeRevalidationResult>> HandleAsync(
        AuthoritativeRevalidationQuery query,
        CancellationToken cancellationToken)
    {
        var terminal = await _terminals.GetByIdAsync(query.TerminalId, cancellationToken);
        if (terminal is null)
        {
            return Result<AuthoritativeRevalidationResult>.Failure(
                "terminal.not_found",
                $"Terminal '{query.TerminalId}' does not exist on the server.");
        }

        if (terminal.Status == TerminalStatus.Revoked)
        {
            return Result<AuthoritativeRevalidationResult>.Failure(
                "terminal.revoked",
                "Terminal has been revoked by administration. Access denied.");
        }

        // 1. Cash session status
        CashSession? activeSession = await _cash.GetOpenSessionForUpdateAsync(cancellationToken);
        bool sessionIsOpen = false;
        Guid? activeSessionId = null;

        if (activeSession is not null && activeSession.Status == CashSessionStatus.Open)
        {
            sessionIsOpen = true;
            activeSessionId = activeSession.Id;
        }

        // 2. Active stocktake locks
        var lockedProductIds = new List<Guid>();
        var openStocktake = await _inventory.GetOpenStocktakeForUpdateAsync(cancellationToken);
        if (openStocktake is not null && openStocktake.Status == StocktakeStatus.Counting)
        {
            var items = await _inventory.GetStocktakeItemsAsync(openStocktake.Id, cancellationToken);
            lockedProductIds.AddRange(items.Select(x => x.ProductId));
        }

        // 3. Products and stock levels
        var productStates = new List<RevalidatedProductState>();
        var productsToQuery = new List<Product>();

        if (query.MonitoredProductIds is not null && query.MonitoredProductIds.Count > 0)
        {
            foreach (var pid in query.MonitoredProductIds)
            {
                var p = await _catalog.GetProductAsync(pid, cancellationToken);
                if (p is not null)
                {
                    productsToQuery.Add(p);
                }
            }
        }
        else
        {
            var allProducts = await _catalog.GetProductsAsync(includeInactive: false, cancellationToken);
            productsToQuery.AddRange(allProducts.Take(100)); // Sample/active set
        }

        foreach (var product in productsToQuery)
        {
            var stock = await _inventory.GetStockBalanceForUpdateAsync(product.Id, cancellationToken);

            productStates.Add(new RevalidatedProductState(
                ProductId: product.Id,
                Name: product.Name,
                Sku: product.Sku ?? string.Empty,
                IsActive: product.IsActive,
                BasePrice: product.DefaultSalePrice,
                SellableStock: stock?.SellableQty ?? 0m,
                Version: product.Version));
        }

        // 4. Supplier balances
        var supplierBalances = new List<RevalidatedSupplierBalance>();
        var suppliers = await _parties.GetSuppliersAsync(includeInactive: false, cancellationToken);
        foreach (var supplier in suppliers)
        {
            var balance = await _supplierAccounts.GetCurrentBalanceAsync(supplier.Id, cancellationToken);
            supplierBalances.Add(new RevalidatedSupplierBalance(
                SupplierId: supplier.Id,
                SupplierName: supplier.Name,
                CurrentPayableBalance: balance));
        }

        var result = new AuthoritativeRevalidationResult(
            TerminalId: terminal.Id,
            TerminalStatus: terminal.Status,
            ServerTimeUtc: _clock.UtcNow,
            ServerProtocolVersion: TerminalProtocol.CurrentProtocolVersion,
            CashSessionIsOpen: sessionIsOpen,
            ActiveCashSessionId: activeSessionId,
            ActiveStocktakeLockedProductIds: lockedProductIds,
            ProductStates: productStates,
            SupplierBalances: supplierBalances);

        return Result<AuthoritativeRevalidationResult>.Success(result);
    }
}
