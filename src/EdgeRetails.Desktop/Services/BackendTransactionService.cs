using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Gateways;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed class BackendTransactionService : ITransactionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Guid?> _actorUserId;
    private readonly Lock _syncRoot = new();
    private readonly List<SaleTransactionRecord> _transactions = [];
    private readonly Dictionary<string, IReadOnlyList<SaleReturnRecord>> _returnsByInvoice =
        new(StringComparer.OrdinalIgnoreCase);

    public BackendTransactionService(
        IServiceScopeFactory scopeFactory,
        Func<Guid?> actorUserId)
    {
        _scopeFactory = scopeFactory;
        _actorUserId = actorUserId;
    }

    public event EventHandler<SaleTransactionRecord>? TransactionRecorded;
    public event EventHandler<SaleReturnRecord>? ReturnRecorded;

    public decimal TodaySales
    {
        get
        {
            lock (_syncRoot)
            {
                return _transactions
                    .Where(x => x.Timestamp.Date == DateTime.Today)
                    .Sum(x => x.TotalAmount);
            }
        }
    }
    public int TodayCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _transactions.Count(x => x.Timestamp.Date == DateTime.Today);
            }
        }
    }

    public decimal TotalReturns
    {
        get
        {
            lock (_syncRoot)
            {
                return _returnsByInvoice.Values
                    .SelectMany(x => x)
                    .Where(x => x.Timestamp.Date == DateTime.Today)
                    .Sum(x => x.TotalRefundAmount);
            }
        }
    }

    public decimal NetSales => Math.Max(0m, TodaySales - TotalReturns);

    [Obsolete("Use RecordTransactionAsync instead to avoid dispatcher deadlocks.")]
    public SaleTransactionRecord RecordTransaction(RecordSaleRequest request) =>
        throw new NotSupportedException("Synchronous transaction recording is prohibited to avoid UI dispatcher deadlocks. Use RecordTransactionAsync instead.");

    public async Task<SaleTransactionRecord> RecordTransactionAsync(
        RecordSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actorId = RequireActor();
        var items = request.Items?.ToArray() ?? [];
        if (items.Length == 0)
        {
            throw new InvalidOperationException(
                "Backend sale requires at least one authoritative cart line.");
        }
        var lines = items.Select(item =>
        {
            if (item.BackendProductId is null ||
                item.BackendProductUnitId is null)
            {
                throw new InvalidOperationException(
                    $"Product '{item.ProductName}' is still using demo identifiers.");
            }

            return new CompleteSaleLineInput(
                item.BackendProductId.Value,
                item.BackendProductUnitId.Value,
                item.Quantity,
                item.UnitPrice,
                item.InventoryUnitIds);
        }).ToArray();

        await using var scope = _scopeFactory.CreateAsyncScope();
        if (request.ClientOperationId == Guid.Empty)
        {
            throw new BackendOperationException(
                "sales.operation_id_required",
                "Client operation id is required.");
        }

        // Authoritative mutation routing: IApplicationGateway encapsulates CompleteSaleHandler and CreateSaleReturnHandler
        var gateway = scope.ServiceProvider.GetRequiredService<IApplicationGateway>();
        CompleteSaleResult committed;
        if (request.DraftId is Guid draftId)
        {
            if (request.DraftVersion is null)
            {
                throw new BackendOperationException(
                    "sales.draft_version_required",
                    "Draft version is required when completing a resumed draft.");
            }

            var draftResult = await gateway.CompletePosDraftAsync(
                new CompletePosDraftCommand(
                    draftId,
                    request.DraftVersion.Value,
                    request.ClientOperationId,
                    actorId,
                    null,
                    request.DiscountAmount,
                    MapPaymentMethod(request.PaymentMethod),
                    request.AmountReceived,
                    request.PaymentReference),
                cancellationToken);

            if (!draftResult.IsSuccess || draftResult.Value is null)
            {
                throw new BackendOperationException(
                    draftResult.Error?.Code ?? "sales.draft_complete_failed",
                    draftResult.Error?.Message ?? "Backend draft completion failed.");
            }

            committed = draftResult.Value;
        }
        else
        {
            var result = await gateway.CompleteSaleAsync(
                new CompleteSaleCommand(
                    request.ClientOperationId,
                    request.CustomerId,
                    actorId,
                    null,
                    request.DiscountAmount,
                    MapPaymentMethod(request.PaymentMethod),
                    request.AmountReceived,
                    request.PaymentReference,
                    null,
                    lines),
                cancellationToken);

            if (!result.IsSuccess || result.Value is null)
            {
                throw new BackendOperationException(
                    result.Error?.Code ?? "sales.complete_failed",
                    result.Error?.Message ?? "Backend sale failed.");
            }

            committed = result.Value;
        }

        var reads = scope.ServiceProvider.GetRequiredService<ISalesReadService>();
        var detail = await reads.GetDetailAsync(
            new GetSaleDetailQuery(committed.SaleId),
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Sale was committed but could not be read back.");

        var record = CacheDetail(detail);
        TransactionRecorded?.Invoke(this, record);
        return record;
    }
    public IReadOnlyList<SaleTransactionRecord> GetAllTransactions()
    {
        lock (_syncRoot)
        {
            return _transactions.ToArray();
        }
    }

    public async Task<IReadOnlyList<SaleTransactionRecord>> GetAllTransactionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<ISalesReadService>();
        var rows = await reads.GetHistoryAsync(
            new GetSalesHistoryQuery(PageSize: 200),
            cancellationToken);

        lock (_syncRoot)
        {
            foreach (var row in rows)
            {
                var normalized = NormalizeInvoice(row.InvoiceNumber);
                if (!_transactions.Any(x => string.Equals(NormalizeInvoice(x.InvoiceNumber), normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    _transactions.Add(ProjectSummarySale(row));
                }
            }

            _transactions.Sort((left, right) => right.Timestamp.CompareTo(left.Timestamp));
            return _transactions.ToArray();
        }
    }

    public SaleTransactionRecord? GetByInvoiceNumber(string invoiceNumber)
    {
        var normalized = NormalizeInvoice(invoiceNumber);
        lock (_syncRoot)
        {
            return _transactions.FirstOrDefault(x =>
                string.Equals(
                    NormalizeInvoice(x.InvoiceNumber),
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
        }
    }
    public async Task<SaleTransactionRecord?> GetByInvoiceNumberAsync(
        string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        var cached = GetByInvoiceNumber(invoiceNumber);
        if (cached is not null)
        {
            return cached;
        }

        var normalized = NormalizeInvoice(invoiceNumber);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<ISalesReadService>();
        var rows = await reads.GetHistoryAsync(
            new GetSalesHistoryQuery(Search: normalized, PageSize: 50),
            cancellationToken);

        var row = rows.FirstOrDefault(x =>
            string.Equals(
                NormalizeInvoice(x.InvoiceNumber),
                normalized,
                StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return null;
        }

        var detail = await reads.GetDetailAsync(
            new GetSaleDetailQuery(row.SaleId),
            cancellationToken);
        return detail is null ? null : CacheDetail(detail);
    }

    [Obsolete("Use RecordReturnAsync instead to avoid dispatcher deadlocks.")]
    public SaleReturnRecord RecordReturn(RecordSaleReturnRequest request) =>
        throw new NotSupportedException("Synchronous return recording is prohibited to avoid UI dispatcher deadlocks. Use RecordReturnAsync instead.");

    public async Task<SaleReturnRecord> RecordReturnAsync(
        RecordSaleReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actorId = RequireActor();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<ISalesReadService>();
        var detail = await FindDetailAsync(
            reads,
            request.InvoiceNumber,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Invoice {request.InvoiceNumber} was not found."); var disposition = MapDisposition(request.Disposition);
        var lines = request.Items.Select(item =>
        {
            if (!Guid.TryParse(item.ProductId, out var productId))
            {
                throw new InvalidOperationException(
                    "Return line does not contain an authoritative product identifier.");
            }

            var candidates = detail.Items
                .Where(x =>
                    x.ProductId == productId &&
                    (string.IsNullOrWhiteSpace(item.Sku) ||
                     string.Equals(
                         x.Sku,
                         item.Sku,
                         StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            if (candidates.Length != 1)
            {
                throw new InvalidOperationException(
                    $"Return line '{item.Sku}' could not be matched uniquely.");
            }

            var original = candidates[0];
            var baseQuantity = decimal.Round(
                item.Quantity * original.FactorToBaseSnapshot,
                6,
                MidpointRounding.AwayFromZero);

            return new SaleReturnLineInput(
                original.SaleItemId,
                baseQuantity,
                disposition,
                Array.Empty<Guid>());
        }).ToArray();

        var gateway = scope.ServiceProvider.GetRequiredService<IApplicationGateway>();
        var result = await gateway.CreateSaleReturnAsync(
            new CreateSaleReturnCommand(
                detail.SaleId,
                MapReasonCode(request.Disposition),
                string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                MapRefundMethod(request.RefundMethod),
                actorId,
                Guid.CreateVersion7(),
                lines),
            cancellationToken); if (!result.IsSuccess || result.Value is null)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Backend sale return failed.");
        }

        var updated = await reads.GetDetailAsync(
            new GetSaleDetailQuery(detail.SaleId),
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Return was committed but sale detail could not be reloaded.");

        CacheDetail(updated);

        var record = GetReturnsForInvoice(updated.InvoiceNumber)
            .FirstOrDefault(x =>
                string.Equals(
                    x.ReturnNumber,
                    result.Value.ReturnNumber,
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "Return was committed but could not be read back.");

        ReturnRecorded?.Invoke(this, record);
        return record;
    }

    public IReadOnlyList<SaleReturnRecord> GetReturnsForInvoice(string invoiceNumber)
    {
        var normalized = NormalizeInvoice(invoiceNumber);
        lock (_syncRoot)
        {
            return _returnsByInvoice.TryGetValue(normalized, out var returns)
                ? returns.ToArray()
                : Array.Empty<SaleReturnRecord>();
        }
    }

    public IReadOnlyList<SaleReturnRecord> GetAllReturns()
    {
        lock (_syncRoot)
        {
            return _returnsByInvoice.Values.SelectMany(x => x).ToArray();
        }
    }
    private SaleTransactionRecord CacheDetail(SaleDetailDto detail)
    {
        var returns = ProjectReturns(detail);
        var record = ProjectSale(detail);

        lock (_syncRoot)
        {
            _transactions.RemoveAll(x =>
                string.Equals(
                    NormalizeInvoice(x.InvoiceNumber),
                    NormalizeInvoice(record.InvoiceNumber),
                    StringComparison.OrdinalIgnoreCase));
            _transactions.Add(record);
            _transactions.Sort((left, right) =>
                right.Timestamp.CompareTo(left.Timestamp));

            _returnsByInvoice[NormalizeInvoice(record.InvoiceNumber)] = returns;
        }

        return record;
    }

    private static SaleTransactionRecord ProjectSale(SaleDetailDto detail)
    {
        return new SaleTransactionRecord
        {
            InvoiceNumber = detail.InvoiceNumber,
            Timestamp = detail.CompletedAt.LocalDateTime,
            CustomerName = detail.CustomerName,
            PaymentMethod = detail.PaymentMethod switch
            {
                EdgeRetails.Domain.Sales.SalePaymentMethod.Cash => PaymentMethod.Cash,
                EdgeRetails.Domain.Sales.SalePaymentMethod.Bank => PaymentMethod.Bank,
                _ => PaymentMethod.Other
            },
            PaymentState = PaymentState.Paid,
            Subtotal = detail.Subtotal,
            DiscountAmount = detail.InvoiceDiscount,
            TotalAmount = detail.GrandTotal,
            AmountReceived = detail.AmountTendered,
            ChangeReturned = detail.ChangeGiven,
            PrintReceipt = true,
            CashierName = "Backend User",
            PaymentReference = detail.PaymentReference ?? string.Empty,
            Items = detail.Items.Select(item => new SaleTransactionItem
            {
                ProductId = item.ProductId.ToString("D"),
                BackendProductId = item.ProductId,
                ProductName = item.ProductName,
                Sku = item.Sku ?? string.Empty,
                UnitPrice = item.UnitPrice,
                Quantity = item.EnteredQuantity,
                Discount = 0m,
                LineTotal = item.GrossLineTotal
            }).ToArray()
        };
    }

    private static SaleTransactionRecord ProjectSummarySale(SalesHistoryRowDto row)
    {
        return new SaleTransactionRecord
        {
            InvoiceNumber = row.InvoiceNumber,
            Timestamp = row.CompletedAt.LocalDateTime,
            CustomerName = string.IsNullOrWhiteSpace(row.CustomerName) ? "Walk-in Customer" : row.CustomerName,
            PaymentMethod = row.PaymentMethod switch
            {
                EdgeRetails.Domain.Sales.SalePaymentMethod.Cash => PaymentMethod.Cash,
                EdgeRetails.Domain.Sales.SalePaymentMethod.Bank => PaymentMethod.Bank,
                _ => PaymentMethod.Other
            },
            PaymentState = PaymentState.Paid,
            Subtotal = row.GrandTotal,
            DiscountAmount = 0m,
            TotalAmount = row.GrandTotal,
            AmountReceived = row.GrandTotal,
            ChangeReturned = 0m,
            PrintReceipt = true,
            CashierName = "Backend User",
            PaymentReference = string.Empty,
            Items = []
        };
    }

    private static IReadOnlyList<SaleReturnRecord> ProjectReturns(
        SaleDetailDto detail)
    {
        return detail.Returns.Select(summary => new SaleReturnRecord
        {
            ReturnNumber = summary.ReturnNumber,
            InvoiceNumber = detail.InvoiceNumber,
            Timestamp = summary.CreatedAt.LocalDateTime,
            Disposition = MapDisposition(summary.ReasonCode),
            RefundMethod = summary.RefundMethod switch
            {
                EdgeRetails.Domain.Sales.RefundMethod.Cash => "Cash Refund",
                EdgeRetails.Domain.Sales.RefundMethod.Bank => "Bank Refund",
                _ => "Other"
            },
            Notes = summary.ReasonCode,
            TotalRefundAmount = summary.RefundAmount,
            Items = detail.ReturnItems
                .Where(x => x.SaleReturnId == summary.SaleReturnId)
                .Select(x => new SaleReturnItemRecord
                {
                    ProductId = x.ProductId.ToString("D"),
                    Sku = x.Sku ?? string.Empty,
                    Quantity = x.EnteredQuantity,
                    RefundAmount = x.RefundAmount
                })
                .ToArray()
        }).ToArray();
    }

    private static async Task<SaleDetailDto?> FindDetailAsync(
        ISalesReadService reads,
        string invoiceNumber,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeInvoice(invoiceNumber);
        var rows = await reads.GetHistoryAsync(
            new GetSalesHistoryQuery(Search: normalized, PageSize: 50),
            cancellationToken);

        var row = rows.FirstOrDefault(x =>
            string.Equals(
                NormalizeInvoice(x.InvoiceNumber),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        return row is null
            ? null
            : await reads.GetDetailAsync(
                new GetSaleDetailQuery(row.SaleId),
                cancellationToken);
    }
    private Guid RequireActor() =>
        _actorUserId()
        ?? throw new InvalidOperationException(
            "A persistent backend user session is required for this operation.");

    private static EdgeRetails.Domain.Sales.SalePaymentMethod MapPaymentMethod(
        PaymentMethod method) => method switch
        {
            PaymentMethod.Cash => EdgeRetails.Domain.Sales.SalePaymentMethod.Cash,
            PaymentMethod.Bank => EdgeRetails.Domain.Sales.SalePaymentMethod.Bank,
            _ => EdgeRetails.Domain.Sales.SalePaymentMethod.Other
        };

    private static EdgeRetails.Domain.Sales.SaleReturnDisposition MapDisposition(
        SaleReturnDisposition disposition) => disposition switch
        {
            SaleReturnDisposition.RestockSellable =>
                EdgeRetails.Domain.Sales.SaleReturnDisposition.RestockSellable,
            SaleReturnDisposition.Defective =>
                EdgeRetails.Domain.Sales.SaleReturnDisposition.Defective,
            SaleReturnDisposition.Damaged =>
                EdgeRetails.Domain.Sales.SaleReturnDisposition.Damaged,
            SaleReturnDisposition.Other =>
                throw new InvalidOperationException(
                    "The backend requires a concrete stock disposition for returns."),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition))
        };

    private static SaleReturnDisposition MapDisposition(string reasonCode)
    {
        if (reasonCode.Contains("DEFECT", StringComparison.OrdinalIgnoreCase))
        {
            return SaleReturnDisposition.Defective;
        }

        if (reasonCode.Contains("DAMAGE", StringComparison.OrdinalIgnoreCase))
        {
            return SaleReturnDisposition.Damaged;
        }

        return SaleReturnDisposition.RestockSellable;
    }
    private static string MapReasonCode(SaleReturnDisposition disposition) =>
        disposition switch
        {
            SaleReturnDisposition.RestockSellable => "CUSTOMER_CHANGED_MIND",
            SaleReturnDisposition.Defective => "DEFECTIVE",
            SaleReturnDisposition.Damaged => "DAMAGED",
            _ => "OTHER"
        };

    private static EdgeRetails.Domain.Sales.RefundMethod MapRefundMethod(
        string refundMethod)
    {
        if (refundMethod.Contains("Cash", StringComparison.OrdinalIgnoreCase))
        {
            return EdgeRetails.Domain.Sales.RefundMethod.Cash;
        }

        if (refundMethod.Contains("Bank", StringComparison.OrdinalIgnoreCase))
        {
            return EdgeRetails.Domain.Sales.RefundMethod.Bank;
        }

        return EdgeRetails.Domain.Sales.RefundMethod.Other;
    }

    private static string NormalizeInvoice(string value) =>
        value.Trim().TrimStart('#');
}
