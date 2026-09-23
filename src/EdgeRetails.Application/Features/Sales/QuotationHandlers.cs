using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Sales;

namespace EdgeRetails.Application.Features.Sales;

public sealed record QuotationItemInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal Quantity,
    decimal QuotedUnitPrice);

public sealed record CreateQuotationCommand(
    Guid? CustomerId,
    string? CustomerName,
    DateOnly? ValidUntil,
    decimal Discount,
    string? Notes,
    Guid ActorId,
    IReadOnlyList<QuotationItemInput> Items);

public sealed class CreateQuotationHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IQuotationRepository _quotations;
    private readonly IPartyRepository _parties;
    private readonly IBusinessAuditWriter _audit;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public CreateQuotationHandler(
        ICatalogRepository catalog,
        IQuotationRepository quotations,
        IPartyRepository parties,
        IBusinessAuditWriter audit,
        IDocumentNumberService numbers,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _quotations = quotations;
        _parties = parties;
        _audit = audit;
        _numbers = numbers;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        CreateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            if (command.Items.Count == 0)
            {
                return Result<Guid>.Failure(
                    "sales.quotation_items_required",
                    "Quotation requires at least one item.");
            }

            if (command.Items
                .GroupBy(x => new { x.ProductId, x.ProductUnitId })
                .Any(g => g.Count() > 1))
            {
                return Result<Guid>.Failure(
                    "sales.quotation_duplicate_line",
                    "The same product and unit may appear only once on a quotation.");
            }

            string? customerNameSnapshot = Normalize(command.CustomerName);
            if (command.CustomerId is not null)
            {
                var customer = await _parties.GetCustomerAsync(
                    command.CustomerId.Value,
                    ct);
                if (customer is null || !customer.IsActive)
                {
                    return Result<Guid>.Failure(
                        "sales.quotation_customer_not_active",
                        "Quotation customer was not found or is inactive.");
                }

                customerNameSnapshot = customer.Name;
            }

            if (command.ValidUntil is not null && command.ValidUntil < _clock.ShopDate)
            {
                return Result<Guid>.Failure(
                    "sales.quotation_validity_past",
                    "Quotation validity date cannot be in the past.");
            }

            var quotation = new Quotation
            {
                QuotationNumber = await _numbers.NextAsync("QTN", ct),
                CustomerId = command.CustomerId,
                CustomerNameSnapshot = customerNameSnapshot,
                QuotationDate = _clock.ShopDate,
                ValidUntil = command.ValidUntil,
                Notes = Normalize(command.Notes),
                CreatedBy = command.ActorId,
                CreatedAt = _clock.UtcNow
            };

            decimal subtotal = 0m;
            foreach (var input in command.Items)
            {
                var product = await _catalog.GetProductAsync(input.ProductId, ct);
                if (product is null || !product.IsActive)
                {
                    return Result<Guid>.Failure(
                        "sales.quotation_product_unavailable",
                        "One of the quotation products is unavailable.");
                }

                var productUnit = await _catalog.GetProductUnitAsync(input.ProductUnitId, ct);
                if (productUnit is null ||
                    productUnit.ProductId != product.Id ||
                    !productUnit.IsActive ||
                    !productUnit.CanSell)
                {
                    return Result<Guid>.Failure(
                        "sales.quotation_unit_unavailable",
                        "One of the quotation units is unavailable for sale.");
                }

                QuotationItem item;
                try
                {
                    item = QuotationItem.Create(
                        quotation.Id,
                        product,
                        productUnit,
                        input.Quantity,
                        input.QuotedUnitPrice);
                }
                catch (BusinessRuleException ex)
                {
                    return Result<Guid>.Failure(ex.Code, ex.Message);
                }

                subtotal += item.LineTotal;
                _quotations.AddItem(item);
            }

            subtotal = decimal.Round(subtotal, 2, MidpointRounding.AwayFromZero);
            var discount = decimal.Round(command.Discount, 2, MidpointRounding.AwayFromZero);
            if (discount < 0 || discount > subtotal)
            {
                return Result<Guid>.Failure(
                    "sales.quotation_discount_invalid",
                    "Quotation discount must be between zero and subtotal.");
            }

            quotation.Subtotal = subtotal;
            quotation.Discount = discount;
            quotation.GrandTotal = decimal.Round(
                subtotal - discount,
                2,
                MidpointRounding.AwayFromZero);

            _quotations.AddQuotation(quotation);

            _audit.Record(
                "QUOTATION_CREATED",
                "QUOTATION",
                quotation.Id,
                command.ActorId,
                quotation.Id,
                $"Quotation {quotation.QuotationNumber}; total {quotation.GrandTotal:0.00}.");

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(quotation.Id);
        }, cancellationToken);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed record UpdateQuotationCommand(
    Guid QuotationId,
    Guid? CustomerId,
    string? CustomerName,
    DateOnly? ValidUntil,
    decimal Discount,
    string? Notes,
    Guid ActorId,
    IReadOnlyList<QuotationItemInput> Items);

public sealed class UpdateQuotationHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IQuotationRepository _quotations;
    private readonly IPartyRepository _parties;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateQuotationHandler(
        ICatalogRepository catalog,
        IQuotationRepository quotations,
        IPartyRepository parties,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _quotations = quotations;
        _parties = parties;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        UpdateQuotationCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            if (command.Items.Count == 0)
            {
                return Result.Failure(
                    "sales.quotation_items_required",
                    "Quotation requires at least one item.");
            }

            if (command.Items
                .GroupBy(x => new { x.ProductId, x.ProductUnitId })
                .Any(g => g.Count() > 1))
            {
                return Result.Failure(
                    "sales.quotation_duplicate_line",
                    "The same product and unit may appear only once on a quotation.");
            }

            if (command.ValidUntil is not null &&
                command.ValidUntil < _clock.ShopDate)
            {
                return Result.Failure(
                    "sales.quotation_validity_past",
                    "Quotation validity date cannot be in the past.");
            }

            var quotation = await _quotations.GetQuotationForUpdateAsync(
                command.QuotationId,
                ct);
            if (quotation is null)
            {
                return Result.Failure(
                    "sales.quotation_not_found",
                    "Quotation was not found.");
            }

            try
            {
                quotation.PrepareForUpdate();
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            string? customerNameSnapshot = NormalizeUpdate(command.CustomerName);
            if (command.CustomerId is not null)
            {
                var customer = await _parties.GetCustomerAsync(
                    command.CustomerId.Value,
                    ct);
                if (customer is null || !customer.IsActive)
                {
                    return Result.Failure(
                        "sales.quotation_customer_not_active",
                        "Quotation customer was not found or is inactive.");
                }

                customerNameSnapshot = customer.Name;
            }

            var existingItems = await _quotations.GetItemsAsync(
                quotation.Id,
                ct);
            foreach (var existingItem in existingItems)
            {
                _quotations.RemoveItem(existingItem);
            }

            decimal subtotal = 0m;
            foreach (var input in command.Items)
            {
                var product = await _catalog.GetProductAsync(
                    input.ProductId,
                    ct);
                if (product is null || !product.IsActive)
                {
                    return Result.Failure(
                        "sales.quotation_product_unavailable",
                        "One of the quotation products is unavailable.");
                }

                var productUnit = await _catalog.GetProductUnitAsync(
                    input.ProductUnitId,
                    ct);
                if (productUnit is null ||
                    productUnit.ProductId != product.Id ||
                    !productUnit.IsActive ||
                    !productUnit.CanSell)
                {
                    return Result.Failure(
                        "sales.quotation_unit_unavailable",
                        "One of the quotation units is unavailable for sale.");
                }

                QuotationItem item;
                try
                {
                    item = QuotationItem.Create(
                        quotation.Id,
                        product,
                        productUnit,
                        input.Quantity,
                        input.QuotedUnitPrice);
                }
                catch (BusinessRuleException ex)
                {
                    return Result.Failure(ex.Code, ex.Message);
                }

                subtotal += item.LineTotal;
                _quotations.AddItem(item);
            }

            subtotal = decimal.Round(
                subtotal,
                2,
                MidpointRounding.AwayFromZero);
            var discount = decimal.Round(
                command.Discount,
                2,
                MidpointRounding.AwayFromZero);

            if (discount < 0 || discount > subtotal)
            {
                return Result.Failure(
                    "sales.quotation_discount_invalid",
                    "Quotation discount must be between zero and subtotal.");
            }

            quotation.CustomerId = command.CustomerId;
            quotation.CustomerNameSnapshot = customerNameSnapshot;
            quotation.QuotationDate = _clock.ShopDate;
            quotation.ValidUntil = command.ValidUntil;
            quotation.Notes = NormalizeUpdate(command.Notes);
            quotation.Subtotal = subtotal;
            quotation.Discount = discount;
            quotation.GrandTotal = decimal.Round(
                subtotal - discount,
                2,
                MidpointRounding.AwayFromZero);
            quotation.Version++;

            _audit.Record(
                "QUOTATION_UPDATED",
                "QUOTATION",
                quotation.Id,
                command.ActorId,
                quotation.Id,
                $"Quotation {quotation.QuotationNumber} updated; total {quotation.GrandTotal:0.00}.");

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }

    private static string? NormalizeUpdate(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed record IssueQuotationCommand(Guid QuotationId, Guid ActorId);

public sealed class IssueQuotationHandler
{
    private readonly IQuotationRepository _quotations;
    private readonly IBusinessAuditWriter _audit;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public IssueQuotationHandler(
        IQuotationRepository quotations,
        IBusinessAuditWriter audit,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _quotations = quotations;
        _audit = audit;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        IssueQuotationCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var quotation = await _quotations.GetQuotationForUpdateAsync(
                command.QuotationId,
                ct);

            if (quotation is null)
            {
                return Result.Failure(
                    "sales.quotation_not_found",
                    "Quotation was not found.");
            }

            try
            {
                quotation.Issue();
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            _audit.Record(
                "QUOTATION_ISSUED",
                "QUOTATION",
                quotation.Id,
                command.ActorId,
                quotation.Id,
                $"Quotation {quotation.QuotationNumber} issued.");

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}

public sealed record CancelQuotationCommand(Guid QuotationId, Guid ActorId);

public sealed class CancelQuotationHandler
{
    private readonly IQuotationRepository _quotations;
    private readonly IBusinessAuditWriter _audit;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public CancelQuotationHandler(
        IQuotationRepository quotations,
        IBusinessAuditWriter audit,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _quotations = quotations;
        _audit = audit;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        CancelQuotationCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var quotation = await _quotations.GetQuotationForUpdateAsync(
                command.QuotationId,
                ct);

            if (quotation is null)
            {
                return Result.Failure(
                    "sales.quotation_not_found",
                    "Quotation was not found.");
            }

            try
            {
                quotation.Cancel();
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            _audit.Record(
                "QUOTATION_CANCELLED",
                "QUOTATION",
                quotation.Id,
                command.ActorId,
                quotation.Id,
                $"Quotation {quotation.QuotationNumber} cancelled.");

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}

public sealed record PreparedQuotationSaleLine(
    Guid ProductId,
    Guid ProductUnitId,
    decimal EnteredQuantity,
    decimal ExpectedUnitPrice);

public sealed record PreparedQuotationSale(
    Guid QuotationId,
    Guid? CustomerId,
    decimal InvoiceDiscount,
    IReadOnlyList<PreparedQuotationSaleLine> Lines);

public sealed record PrepareQuotationForSaleQuery(Guid QuotationId);

public sealed class PrepareQuotationForSaleHandler
{
    private readonly IQuotationRepository _quotations;
    private readonly ICatalogRepository _catalog;
    private readonly IClock _clock;

    public PrepareQuotationForSaleHandler(
        IQuotationRepository quotations,
        ICatalogRepository catalog,
        IClock clock)
    {
        _quotations = quotations;
        _catalog = catalog;
        _clock = clock;
    }

    public async Task<Result<PreparedQuotationSale>> HandleAsync(
        PrepareQuotationForSaleQuery query,
        CancellationToken cancellationToken)
    {
        var quotation = await _quotations.GetQuotationAsync(
            query.QuotationId,
            cancellationToken);

        if (quotation is null)
        {
            return Result<PreparedQuotationSale>.Failure(
                "sales.quotation_not_found",
                "Quotation was not found.");
        }

        if (quotation.Status != QuotationStatus.Issued)
        {
            return Result<PreparedQuotationSale>.Failure(
                "sales.quotation_not_issued",
                "Only an issued quotation can be prepared for sale.");
        }

        if (quotation.ValidUntil is not null &&
            _clock.ShopDate > quotation.ValidUntil.Value)
        {
            return Result<PreparedQuotationSale>.Failure(
                "sales.quotation_expired",
                "Expired quotation cannot be converted.");
        }

        var items = await _quotations.GetItemsAsync(
            quotation.Id,
            cancellationToken);
        var prepared = new List<PreparedQuotationSaleLine>(items.Count);

        foreach (var item in items.OrderBy(x => x.ProductId).ThenBy(x => x.SelectedUnitId))
        {
            var product = await _catalog.GetProductAsync(
                item.ProductId,
                cancellationToken);
            if (product is null || !product.IsActive)
            {
                return Result<PreparedQuotationSale>.Failure(
                    "sales.quotation_product_unavailable",
                    "One or more quotation products are unavailable.");
            }

            var productUnit = await _catalog.GetProductUnitAsync(
                item.ProductId,
                item.SelectedUnitId,
                cancellationToken);
            if (productUnit is null ||
                !productUnit.IsActive ||
                !productUnit.CanSell ||
                productUnit.FactorToBaseUnit != item.FactorToBaseSnapshot)
            {
                return Result<PreparedQuotationSale>.Failure(
                    "sales.quotation_unit_changed",
                    "A quotation unit is unavailable or its conversion changed.");
            }

            prepared.Add(new PreparedQuotationSaleLine(
                item.ProductId,
                productUnit.Id,
                item.EnteredQuantity,
                item.QuotedUnitPrice));
        }

        return Result<PreparedQuotationSale>.Success(
            new PreparedQuotationSale(
                quotation.Id,
                quotation.CustomerId,
                quotation.Discount,
                prepared));
    }
}
