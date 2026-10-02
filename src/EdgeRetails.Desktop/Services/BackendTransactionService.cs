using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Gateways;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace EdgeRetails.Desktop.Services;

public sealed class BackendTransactionService : ITransactionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Guid?> _actorUserId;
    private readonly DesktopApiClient _apiClient;
    private readonly IClientOperationIntentStore _operationIntents;
    private readonly Lock _syncRoot = new();
    private readonly List<SaleTransactionRecord> _transactions = [];
    private readonly HashSet<Guid> _uncertainSaleOperations = [];
    private readonly Dictionary<string, IReadOnlyList<SaleReturnRecord>> _returnsByInvoice =
        new(StringComparer.OrdinalIgnoreCase);
    public const string SaleCheckoutIntentKey = "sale:checkout";

    public BackendTransactionService(
        IServiceScopeFactory scopeFactory,
        Func<Guid?> actorUserId,
        DesktopApiClient apiClient,
        IClientOperationIntentStore? operationIntents = null)
    {
        _scopeFactory = scopeFactory;
        _actorUserId = actorUserId;
        _apiClient = apiClient;
        _operationIntents = operationIntents ?? new FileClientOperationIntentStore();
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
                item.PriceOverrideUnitPrice is null ? item.UnitPrice : item.ListUnitPrice,
                item.InventoryUnitIds,
                item.PriceOverrideUnitPrice,
                item.PriceOverrideReason);
        }).ToArray();

        if (request.ClientOperationId == Guid.Empty)
        {
            throw new BackendOperationException(
                "sales.operation_id_required",
                "Client operation id is required.");
        }
        if (request.DraftId is not null && request.DraftVersion is null)
        {
            throw new BackendOperationException(
                "sales.draft_version_required",
                "Draft version is required when completing a resumed draft.");
        }

        var operationId = await _operationIntents.GetOrCreateAsync(
            SaleCheckoutIntentKey,
            SerializeSaleIntentPayload(request),
            request.ClientOperationId,
            cancellationToken);
        if (operationId != request.ClientOperationId)
        {
            throw new InvalidOperationException(
                "The unresolved sale must be resumed with its original operation identity.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        // Authoritative mutation routing: IApplicationGateway encapsulates CompleteSaleHandler and CreateSaleReturnHandler
        var gateway = scope.ServiceProvider.GetRequiredService<IApplicationGateway>();
        Guid committedSaleId;
        if (IsUncertainSale(request.ClientOperationId))
        {
            committedSaleId = await RecoverSaleOutcomeAsync(
                gateway, request.ClientOperationId, cancellationToken);
            goto ReadCommittedSale;
        }

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
                if (IsTransportFailure(draftResult.Error?.Code))
                {
                    committedSaleId = await RecoverSaleOutcomeAsync(
                        gateway, request.ClientOperationId, cancellationToken);
                    goto ReadCommittedSale;
                }

                await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, request.ClientOperationId);
                throw new BackendOperationException(
                    draftResult.Error?.Code ?? "sales.draft_complete_failed",
                    draftResult.Error?.Message ?? "Backend draft completion failed.");
            }

            committedSaleId = draftResult.Value.SaleId;
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
                if (IsTransportFailure(result.Error?.Code))
                {
                    committedSaleId = await RecoverSaleOutcomeAsync(
                        gateway, request.ClientOperationId, cancellationToken);
                    goto ReadCommittedSale;
                }

                await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, request.ClientOperationId);
                throw new BackendOperationException(
                    result.Error?.Code ?? "sales.complete_failed",
                    result.Error?.Message ?? "Backend sale failed.");
            }

            committedSaleId = result.Value.SaleId;
        }

    ReadCommittedSale:
        SaleTransactionRecord record;
        try
        {
            var detail = await _apiClient.GetAsync<SaleDetailDto>(
                $"/api/sales/{committedSaleId:D}", cancellationToken);
            record = CacheDetail(detail);
        }
        catch (Exception)
        {
            // The mutation already returned a committed sale id. Any read-back or
            // mapping failure must keep the operation locked to recovery.
            MarkSaleUncertain(request.ClientOperationId);
            throw new BackendOperationException(
                "sales.outcome_unknown",
                "Sale may be committed, but its detail could not be loaded. Recheck using the same operation id.");
        }

        lock (_syncRoot)
        {
            _uncertainSaleOperations.Remove(request.ClientOperationId);
        }
        await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, request.ClientOperationId);
        TransactionRecorded?.Invoke(this, record);
        return record;
    }

    private static string SerializeSaleIntentPayload(RecordSaleRequest request) =>
        JsonSerializer.Serialize(new
        {
            request.DraftId,
            request.DraftVersion,
            request.CustomerId,
            request.CustomerName,
            request.CustomerPhone,
            request.CashierName,
            request.TotalAmount,
            request.Subtotal,
            request.DiscountAmount,
            request.PaymentMethod,
            request.AmountReceived,
            request.ChangeReturned,
            request.PaymentReference,
            request.Notes,
            Items = request.Items?.Select(item => new
            {
                item.BackendProductId,
                item.BackendProductUnitId,
                item.Quantity,
                item.UnitPrice,
                item.ListUnitPrice,
                item.PriceOverrideUnitPrice,
                item.PriceOverrideReason,
                item.InventoryUnitIds
            }).ToArray()
        });

    private bool IsUncertainSale(Guid clientOperationId)
    {
        lock (_syncRoot)
        {
            return _uncertainSaleOperations.Contains(clientOperationId);
        }
    }

    private void MarkSaleUncertain(Guid clientOperationId)
    {
        lock (_syncRoot)
        {
            _uncertainSaleOperations.Add(clientOperationId);
        }
    }

    private static bool IsTransportFailure(string? code) =>
        code is not null &&
        (code.StartsWith("network.", StringComparison.OrdinalIgnoreCase) ||
         code.StartsWith("gateway.", StringComparison.OrdinalIgnoreCase) ||
         code.StartsWith("http.5", StringComparison.OrdinalIgnoreCase));

    private async Task<Guid> RecoverSaleOutcomeAsync(
        IApplicationGateway gateway,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        var outcome = await gateway.QueryOperationStatusAsync(clientOperationId, cancellationToken);
        if (!outcome.IsSuccess || outcome.Value is null)
        {
            MarkSaleUncertain(clientOperationId);
            throw new BackendOperationException(
                "sales.outcome_unknown",
                "Sale response was lost and its committed status could not be verified. Recheck using the same operation id.");
        }

        var status = outcome.Value;
        if (status.WasCommitted &&
            string.Equals(status.OperationType, "Sale", StringComparison.OrdinalIgnoreCase) &&
            status.EntityId is Guid saleId)
        {
            return saleId;
        }

        if (string.Equals(status.EffectiveStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, clientOperationId);
            throw new BackendOperationException(
                status.ErrorCode ?? "sales.failed",
                status.ErrorMessage ?? "Backend rejected the sale.");
        }

        MarkSaleUncertain(clientOperationId);
        throw new BackendOperationException(
            "sales.outcome_unknown",
            "Sale status is not confirmed. Recheck using the same operation id before starting another sale.");
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
        var rows = await _apiClient.GetAsync<SalesHistoryRowDto[]>(
            "/api/sales?pageSize=200", cancellationToken);

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
        var rows = await _apiClient.GetAsync<SalesHistoryRowDto[]>(
            $"/api/sales?search={Uri.EscapeDataString(normalized)}&pageSize=50",
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

        var detail = await _apiClient.GetAsync<SaleDetailDto>(
            $"/api/sales/{row.SaleId:D}", cancellationToken);
        return CacheDetail(detail);
    }

    [Obsolete("Use RecordReturnAsync instead to avoid dispatcher deadlocks.")]
    public SaleReturnRecord RecordReturn(RecordSaleReturnRequest request) =>
        throw new NotSupportedException("Synchronous return recording is prohibited to avoid UI dispatcher deadlocks. Use RecordReturnAsync instead.");

    public async Task<SaleReturnRecord> RecordReturnAsync(
        RecordSaleReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ClientOperationId == Guid.Empty)
        {
            throw new BackendOperationException(
                "sales.return_operation_id_required",
                "Client operation id is required for a sale return.");
        }
        var actorId = RequireActor();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var detail = await FindDetailAsync(
            request.InvoiceNumber,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Invoice {request.InvoiceNumber} was not found.");
        var disposition = MapDisposition(request.Disposition);
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
                request.ClientOperationId,
                lines),
            cancellationToken);
        string returnNumber;
        if (!result.IsSuccess || result.Value is null)
        {
            if (!IsTransportFailure(result.Error?.Code))
            {
                throw new BackendOperationException(
                    result.Error?.Code ?? "sales.return_failed",
                    result.Error?.Message ?? "Backend sale return failed.");
            }

            var outcome = await gateway.QueryOperationStatusAsync(
                request.ClientOperationId, cancellationToken);
            if (!outcome.IsSuccess || outcome.Value is null ||
                !outcome.Value.WasCommitted ||
                !string.Equals(outcome.Value.OperationType, "SaleReturn", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(outcome.Value.DocumentNumber))
            {
                throw new BackendOperationException(
                    "sales.return_outcome_unknown",
                    "Return status is not confirmed. Retry this return with the same operation id.");
            }
            returnNumber = outcome.Value.DocumentNumber;
        }
        else
        {
            returnNumber = result.Value.ReturnNumber;
        }

        SaleReturnRecord record;
        try
        {
            var updated = await _apiClient.GetAsync<SaleDetailDto>(
                $"/api/sales/{detail.SaleId:D}", cancellationToken);
            CacheDetail(updated);

            record = GetReturnsForInvoice(updated.InvoiceNumber)
                .FirstOrDefault(x =>
                    string.Equals(
                        x.ReturnNumber,
                        returnNumber,
                        StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Committed return was not included in sale detail.");
        }
        catch (Exception)
        {
            throw new BackendOperationException(
                "sales.return_outcome_unknown",
                "Return may be committed, but its detail could not be loaded. Retry with the same operation id.");
        }

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
            BackendSaleId = detail.SaleId,
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

    private async Task<SaleDetailDto?> FindDetailAsync(
        string invoiceNumber,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeInvoice(invoiceNumber);
        var rows = await _apiClient.GetAsync<SalesHistoryRowDto[]>(
            $"/api/sales?search={Uri.EscapeDataString(normalized)}&pageSize=50",
            cancellationToken);

        var row = rows.FirstOrDefault(x =>
            string.Equals(
                NormalizeInvoice(x.InvoiceNumber),
                normalized,
                StringComparison.OrdinalIgnoreCase));

        return row is null
            ? null
            : await _apiClient.GetAsync<SaleDetailDto>(
                $"/api/sales/{row.SaleId:D}", cancellationToken);
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
