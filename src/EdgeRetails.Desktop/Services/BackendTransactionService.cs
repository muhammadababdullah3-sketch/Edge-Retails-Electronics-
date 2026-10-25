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
        DesktopApiClient apiClient,
        IClientOperationIntentStore? operationIntents = null)
        : this(null!, () => null, apiClient, operationIntents)
    {
    }

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

    public DesktopApiClient? ApiClient => _apiClient;

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
        var allPhysicalUnitIds = items.SelectMany(x => x.InventoryUnitIds).ToList();
        if (allPhysicalUnitIds.Count != allPhysicalUnitIds.Distinct().Count())
        {
            throw new BackendOperationException(
                "sales.duplicate_physical_unit",
                "The same physical unit appears more than once in the sale lines.");
        }

        var lines = items
            .GroupBy(item =>
            {
                if (item.BackendProductId is null ||
                    item.BackendProductUnitId is null)
                {
                    throw new InvalidOperationException(
                        $"Product '{item.ProductName}' is still using demo identifiers.");
                }

                var price = item.PriceOverrideUnitPrice ?? (item.ListUnitPrice > 0m ? item.ListUnitPrice : item.UnitPrice);
                var overrideReason = item.PriceOverrideReason?.Trim() ?? string.Empty;

                return (
                    ProductId: item.BackendProductId.Value,
                    ProductUnitId: item.BackendProductUnitId.Value,
                    UnitPrice: price,
                    OverridePrice: item.PriceOverrideUnitPrice,
                    OverrideReason: overrideReason);
            })
            .Select(group =>
            {
                var groupItems = group.ToList();
                var first = groupItems[0];
                var baseUnitPrice = first.PriceOverrideUnitPrice is null ? first.UnitPrice : first.ListUnitPrice;
                var totalQuantity = groupItems.Sum(x => x.Quantity);
                var combinedUnits = groupItems.SelectMany(x => x.InventoryUnitIds).Distinct().ToArray();

                return new CompleteSaleLineInput(
                    group.Key.ProductId,
                    group.Key.ProductUnitId,
                    totalQuantity,
                    baseUnitPrice,
                    combinedUnits,
                    first.PriceOverrideUnitPrice,
                    first.PriceOverrideReason);
            })
            .ToArray();

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

        var replayEnvelopeObj = new SafeSaleReplayEnvelope
        {
            ClientOperationId = request.ClientOperationId,
            DraftId = request.DraftId,
            DraftVersion = request.DraftVersion,
            CustomerId = request.CustomerId,
            CustomerName = request.CustomerName,
            CustomerPhone = request.CustomerPhone,
            CashierName = request.CashierName,
            TotalAmount = request.TotalAmount,
            Subtotal = request.Subtotal,
            DiscountAmount = request.DiscountAmount,
            PaymentMethod = request.PaymentMethod.ToString(),
            AmountReceived = request.AmountReceived,
            ChangeReturned = request.ChangeReturned,
            PaymentReference = request.PaymentReference,
            Notes = request.Notes,
            PostingDate = request.PostingDate,
            Lines = items.Select(item => new SafeSaleReplayLine
            {
                ProductId = item.BackendProductId!.Value,
                ProductUnitId = item.BackendProductUnitId!.Value,
                SkuOrCode = item.Sku,
                ProductName = item.ProductName,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                ListUnitPrice = item.ListUnitPrice > 0m ? item.ListUnitPrice : item.UnitPrice,
                PriceOverrideUnitPrice = item.PriceOverrideUnitPrice,
                PriceOverrideReason = item.PriceOverrideReason,
                InventoryUnitIds = item.InventoryUnitIds.ToList()
            }).ToList()
        };
        var safeReplayEnvelope = JsonSerializer.Serialize(replayEnvelopeObj);

        var operationId = await _operationIntents.GetOrCreateAsync(
            SaleCheckoutIntentKey,
            SerializeSaleIntentPayload(request),
            request.ClientOperationId,
            operationType: "Sale",
            safeDisplayContext: $"Sale for customer {request.CustomerName ?? "Walk-in"}",
            safeReplayEnvelope: safeReplayEnvelope,
            cancellationToken: cancellationToken);
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
            var recovery = await RecoverSaleOutcomeAsync(
                gateway, request.ClientOperationId, cancellationToken);
            if (recovery.WasCommitted && recovery.CommittedSaleId is Guid recoveredSaleId)
            {
                committedSaleId = recoveredSaleId;
                goto ReadCommittedSale;
            }

            if (recovery.IsNotFoundConfirmed)
            {
                // Bounded recheck confirmed NOT_FOUND on the server.
                // Clear uncertain state and safely proceed to send with the SAME ClientOperationId.
                ClearSaleUncertain(request.ClientOperationId);
            }
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
                    request.PaymentReference,
                    request.PostingDate,
                    request.SupervisorApproval),
                cancellationToken);

            if (!draftResult.IsSuccess || draftResult.Value is null)
            {
                var errorCode = draftResult.Error?.Code;
                if (IsNonFinalOrRetryableFailure(errorCode))
                {
                    MarkSaleUncertain(request.ClientOperationId);
                    throw new BackendOperationException(
                        errorCode ?? "sales.outcome_unknown",
                        draftResult.Error?.Message ?? "Backend draft completion outcome is in-flight or uncertain. Retry using the same operation id.");
                }

                await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, request.ClientOperationId);
                throw new BackendOperationException(
                    errorCode ?? "sales.draft_complete_failed",
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
                    lines,
                    request.PostingDate,
                    request.SupervisorApproval),
                cancellationToken);

            if (!result.IsSuccess || result.Value is null)
            {
                var errorCode = result.Error?.Code;
                if (IsNonFinalOrRetryableFailure(errorCode))
                {
                    MarkSaleUncertain(request.ClientOperationId);
                    throw new BackendOperationException(
                        errorCode ?? "sales.outcome_unknown",
                        result.Error?.Message ?? "Backend sale outcome is in-flight or uncertain. Retry using the same operation id.");
                }

                await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, request.ClientOperationId);
                throw new BackendOperationException(
                    errorCode ?? "sales.complete_failed",
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

    private void ClearSaleUncertain(Guid clientOperationId)
    {
        lock (_syncRoot)
        {
            _uncertainSaleOperations.Remove(clientOperationId);
        }
    }

    private static bool IsNonFinalOrRetryableFailure(string? code) =>
        code is not null &&
        (code.StartsWith("network.", StringComparison.OrdinalIgnoreCase) ||
         code.StartsWith("gateway.", StringComparison.OrdinalIgnoreCase) ||
         code.StartsWith("http.5", StringComparison.OrdinalIgnoreCase) ||
         code.StartsWith("transport.", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(code, "idempotency.operation_in_flight", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(code, "idempotency.outcome_unknown", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(code, "operation.in_flight", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(code, "operation.unknown", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(code, "sales.outcome_unknown", StringComparison.OrdinalIgnoreCase));

    private sealed record SaleRecoveryOutcome(bool WasCommitted, Guid? CommittedSaleId, bool IsNotFoundConfirmed);

    private async Task<SaleRecoveryOutcome> RecoverSaleOutcomeAsync(
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
            return new SaleRecoveryOutcome(true, saleId, false);
        }

        if (string.Equals(status.EffectiveStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, clientOperationId);
            throw new BackendOperationException(
                status.ErrorCode ?? "sales.failed",
                status.ErrorMessage ?? "Backend rejected the sale.");
        }

        if (string.Equals(status.EffectiveStatus, "NotFound", StringComparison.OrdinalIgnoreCase))
        {
            // Bounded recheck to confirm absence on the server before permitting same-ID resend
            var isStillNotFound = true;
            for (int i = 0; i < 2; i++)
            {
                await Task.Delay(50, cancellationToken);
                var recheck = await gateway.QueryOperationStatusAsync(clientOperationId, cancellationToken);
                if (recheck.IsSuccess && recheck.Value is not null)
                {
                    if (recheck.Value.WasCommitted && recheck.Value.EntityId is Guid recheckedSaleId)
                    {
                        return new SaleRecoveryOutcome(true, recheckedSaleId, false);
                    }
                    if (string.Equals(recheck.Value.EffectiveStatus, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, clientOperationId);
                        throw new BackendOperationException(
                            recheck.Value.ErrorCode ?? "sales.failed",
                            recheck.Value.ErrorMessage ?? "Backend rejected the sale.");
                    }
                    if (!string.Equals(recheck.Value.EffectiveStatus, "NotFound", StringComparison.OrdinalIgnoreCase))
                    {
                        isStillNotFound = false;
                        break;
                    }
                }
                else
                {
                    isStillNotFound = false;
                    break;
                }
            }

            if (isStillNotFound)
            {
                return new SaleRecoveryOutcome(false, null, true);
            }
        }

        MarkSaleUncertain(clientOperationId);
        throw new BackendOperationException(
            "sales.outcome_unknown",
            "Sale status is in-flight or not confirmed. Recheck using the same operation id before starting another sale.");
    }

    public async Task<SaleTransactionRecord?> TryRecoverPendingSaleAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _operationIntents.FindPendingByPrefixAsync(SaleCheckoutIntentKey, cancellationToken);
        var entry = pending.SingleOrDefault();
        if (entry is null)
        {
            return null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IApplicationGateway>();

        var outcome = await gateway.QueryOperationStatusAsync(entry.OperationId, cancellationToken);
        if (!outcome.IsSuccess || outcome.Value is null)
        {
            return null;
        }

        var status = outcome.Value;
        if (status.WasCommitted &&
            string.Equals(status.OperationType, "Sale", StringComparison.OrdinalIgnoreCase) &&
            status.EntityId is Guid saleId)
        {
            SaleTransactionRecord record;
            try
            {
                var detail = await _apiClient.GetAsync<SaleDetailDto>(
                    $"/api/sales/{saleId:D}", cancellationToken);
                record = CacheDetail(detail);
            }
            catch
            {
                return null;
            }

            await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, entry.OperationId, cancellationToken);
            TransactionRecorded?.Invoke(this, record);
            return record;
        }

        if (string.Equals(status.EffectiveStatus, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, entry.OperationId, cancellationToken);
            throw new BackendOperationException(
                status.ErrorCode ?? "sales.failed",
                status.ErrorMessage ?? "Backend rejected the sale.");
        }

        if (string.Equals(status.EffectiveStatus, "NotFound", StringComparison.OrdinalIgnoreCase))
        {
            // Bounded recheck to confirm Point-in-time absence on the server before resending
            var isStillNotFound = true;
            for (int i = 0; i < 2; i++)
            {
                await Task.Delay(50, cancellationToken);
                var recheck = await gateway.QueryOperationStatusAsync(entry.OperationId, cancellationToken);
                if (recheck.IsSuccess && recheck.Value is not null)
                {
                    if (recheck.Value.WasCommitted && recheck.Value.EntityId is Guid recheckedSaleId)
                    {
                        var detail = await _apiClient.GetAsync<SaleDetailDto>(
                            $"/api/sales/{recheckedSaleId:D}", cancellationToken);
                        var record = CacheDetail(detail);
                        await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, entry.OperationId, cancellationToken);
                        TransactionRecorded?.Invoke(this, record);
                        return record;
                    }

                    if (string.Equals(recheck.Value.EffectiveStatus, "Failed", StringComparison.OrdinalIgnoreCase))
                    {
                        await _operationIntents.CompleteAsync(SaleCheckoutIntentKey, entry.OperationId, cancellationToken);
                        throw new BackendOperationException(
                            recheck.Value.ErrorCode ?? "sales.failed",
                            recheck.Value.ErrorMessage ?? "Backend rejected the sale.");
                    }

                    if (!string.Equals(recheck.Value.EffectiveStatus, "NotFound", StringComparison.OrdinalIgnoreCase))
                    {
                        isStillNotFound = false;
                        break;
                    }
                }
                else
                {
                    isStillNotFound = false;
                    break;
                }
            }

            if (isStillNotFound && !string.IsNullOrWhiteSpace(entry.SafeReplayEnvelope))
            {
                SafeSaleReplayEnvelope? envelope = null;
                try
                {
                    envelope = JsonSerializer.Deserialize<SafeSaleReplayEnvelope>(entry.SafeReplayEnvelope);
                }
                catch
                {
                    // Invalid envelope format, retain pending intent
                }

                if (envelope is not null && envelope.Lines.Count > 0)
                {
                    var restoredRequest = ReconstituteSaleRequest(envelope, entry.OperationId);
                    ClearSaleUncertain(entry.OperationId);
                    return await RecordTransactionAsync(restoredRequest, cancellationToken);
                }
            }
        }

        return null;
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
                item.InventoryUnitIds ?? Array.Empty<Guid>(),
                item.ReturnSourceContainerId,
                item.ConditionInspectionResult,
                item.IsRestockAsRemnant);
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
                lines,
                request.PostingDate,
                request.SupervisorApproval),
            cancellationToken);
        string returnNumber;
        if (!result.IsSuccess || result.Value is null)
        {
            if (!IsNonFinalOrRetryableFailure(result.Error?.Code))
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

        if (refundMethod.Contains("Store Credit", StringComparison.OrdinalIgnoreCase) ||
            refundMethod.Contains("Other", StringComparison.OrdinalIgnoreCase))
        {
            throw new DesktopApiException(
                "sales.unsupported_refund_method",
                "Store credit is not a supported refund method in sales returns; use cash or bank refund.");
        }

        throw new DesktopApiException(
            "sales.unsupported_refund_method",
            $"Refund method '{refundMethod}' is not supported.");
    }

    private static string NormalizeInvoice(string value) =>
        value.Trim().TrimStart('#');

    public static RecordSaleRequest ReconstituteSaleRequest(SafeSaleReplayEnvelope envelope, Guid operationId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var paymentMethod = Enum.TryParse<PaymentMethod>(envelope.PaymentMethod, true, out var pm)
            ? pm
            : PaymentMethod.Cash;

        var items = envelope.Lines.Select(line =>
        {
            var listPrice = line.ListUnitPrice > 0m ? line.ListUnitPrice : line.UnitPrice;
            var lineTotal = Math.Round(line.UnitPrice * line.Quantity, 2);
            return new SaleTransactionItem
            {
                ProductId = line.ProductId.ToString("D"),
                BackendProductId = line.ProductId,
                BackendProductUnitId = line.ProductUnitId,
                ProductName = string.IsNullOrWhiteSpace(line.ProductName) ? "Item" : line.ProductName,
                Sku = line.SkuOrCode,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                ListUnitPrice = listPrice,
                PriceOverrideUnitPrice = line.PriceOverrideUnitPrice,
                PriceOverrideReason = line.PriceOverrideReason,
                InventoryUnitIds = line.InventoryUnitIds ?? [],
                LineTotal = lineTotal
            };
        }).ToList();

        var subtotal = envelope.Subtotal > 0m ? envelope.Subtotal : items.Sum(i => i.LineTotal);
        var totalAmount = envelope.TotalAmount > 0m ? envelope.TotalAmount : Math.Max(0m, subtotal - envelope.DiscountAmount);

        return new RecordSaleRequest
        {
            ClientOperationId = operationId,
            DraftId = envelope.DraftId,
            DraftVersion = envelope.DraftVersion,
            CustomerId = envelope.CustomerId,
            CustomerName = envelope.CustomerName,
            CustomerPhone = envelope.CustomerPhone,
            CashierName = envelope.CashierName,
            PaymentMethod = paymentMethod,
            AmountReceived = envelope.AmountReceived,
            ChangeReturned = envelope.ChangeReturned,
            PaymentReference = envelope.PaymentReference,
            Notes = envelope.Notes,
            PostingDate = envelope.PostingDate,
            Subtotal = subtotal,
            DiscountAmount = envelope.DiscountAmount,
            TotalAmount = totalAmount,
            Items = items
        };
    }

    public async Task<CommercialExchangeResult> ExecuteCommercialExchangeAsync(
        CommercialExchangeCommand command,
        CancellationToken cancellationToken = default)
    {
        if (command.ClientOperationId == Guid.Empty)
        {
            throw new BackendOperationException(
                "sales.exchange_operation_id_required",
                "Client operation id is required for a commercial exchange.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IApplicationGateway>();
        var result = await gateway.ExecuteCommercialExchangeAsync(command, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new BackendOperationException(
                result.Error?.Code ?? "sales.exchange_failed",
                result.Error?.Message ?? "Commercial exchange could not be completed.");
        }

        return result.Value!;
    }
}

public sealed class SafeSaleReplayEnvelope
{
    public Guid ClientOperationId { get; set; }
    public Guid? DraftId { get; set; }
    public long? DraftVersion { get; set; }
    public Guid? CustomerId { get; set; }
    public string CustomerName { get; set; } = "Walk-in Customer";
    public string CustomerPhone { get; set; } = string.Empty;
    public string CashierName { get; set; } = "Abdullah, Owner";
    public decimal TotalAmount { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public decimal AmountReceived { get; set; }
    public decimal ChangeReturned { get; set; }
    public string PaymentReference { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public DateOnly? PostingDate { get; set; }
    public List<SafeSaleReplayLine> Lines { get; set; } = [];
}

public sealed class SafeSaleReplayLine
{
    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public string SkuOrCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ListUnitPrice { get; set; }
    public decimal? PriceOverrideUnitPrice { get; set; }
    public string? PriceOverrideReason { get; set; }
    public List<Guid> InventoryUnitIds { get; set; } = [];
}
