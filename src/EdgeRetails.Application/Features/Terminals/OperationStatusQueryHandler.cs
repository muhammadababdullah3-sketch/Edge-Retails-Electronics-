using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Gateways;

namespace EdgeRetails.Application.Features.Terminals;

public sealed record OperationStatusQuery(
    Guid ClientOperationId,
    Guid? ActorId = null,
    Guid? TerminalId = null,
    string? PayloadFingerprint = null,
    bool RequireIdentityScope = false,
    bool RequireCanonicalOutcome = false);

public sealed class OperationStatusQueryHandler
{
    private readonly IOperationOutcomeLedger? _outcomeLedger;
    private readonly ISalesRepository _sales;
    private readonly IPurchasingRepository _purchasing;
    private readonly ISupplierAccountRepository _supplierAccounts;
    private readonly IInventoryRepository? _inventory;
    private readonly IExpenseRepository? _expenses;
    private readonly IWarrantyRepository? _warranty;
    private readonly IQuotationRepository? _quotations;
    private readonly IThakaRepository? _thaka;

    public OperationStatusQueryHandler(
        ISalesRepository sales,
        IPurchasingRepository purchasing,
        ISupplierAccountRepository supplierAccounts,
        IInventoryRepository? inventory = null,
        IExpenseRepository? expenses = null,
        IWarrantyRepository? warranty = null,
        IQuotationRepository? quotations = null,
        IThakaRepository? thaka = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _sales = sales;
        _purchasing = purchasing;
        _supplierAccounts = supplierAccounts;
        _inventory = inventory;
        _expenses = expenses;
        _warranty = warranty;
        _quotations = quotations;
        _thaka = thaka;
        _outcomeLedger = outcomeLedger;
    }

    public OperationStatusQueryHandler(
        IOperationOutcomeLedger outcomeLedger,
        ISalesRepository sales,
        IPurchasingRepository purchasing,
        ISupplierAccountRepository supplierAccounts)
        : this(sales, purchasing, supplierAccounts, outcomeLedger: outcomeLedger)
    {
    }

    public async Task<Result<OperationStatusResult>> HandleAsync(
        OperationStatusQuery query,
        CancellationToken cancellationToken)
    {
        if (query.ClientOperationId == Guid.Empty)
        {
            return Result<OperationStatusResult>.Failure(
                "validation.client_operation_id_required",
                "ClientOperationId is required.");
        }

        // 1. Canonical Outcome Authority check (Highest precedence)
        if (_outcomeLedger is not null)
        {
            var canonicalOutcome = await _outcomeLedger.GetOutcomeAsync(query.ClientOperationId, cancellationToken);
            if (canonicalOutcome is not null)
            {
                // Security Scoping: Verify trusted identity (actor/terminal)
                if (query.ActorId.HasValue && query.ActorId.Value != canonicalOutcome.ActorId)
                {
                    return Result<OperationStatusResult>.Failure(
                        "authorization.forbidden",
                        "Not authorized to access operation status for another actor.");
                }

                if (query.TerminalId.HasValue && query.TerminalId.Value != canonicalOutcome.TerminalId)
                {
                    return Result<OperationStatusResult>.Failure(
                        "authorization.forbidden",
                        "Not authorized to access operation status for another terminal.");
                }

                if (query.RequireIdentityScope && (!query.ActorId.HasValue || !canonicalOutcome.ActorId.HasValue || query.ActorId.Value != canonicalOutcome.ActorId.Value))
                {
                    return Result<OperationStatusResult>.Failure(
                        "authorization.forbidden",
                        "Identity scoping required to access operation status.");
                }

                // Idempotency & Payload Mismatch: Fail closed on mismatch
                if (!string.IsNullOrEmpty(query.PayloadFingerprint) &&
                    !string.IsNullOrEmpty(canonicalOutcome.PayloadFingerprint) &&
                    !string.Equals(query.PayloadFingerprint, canonicalOutcome.PayloadFingerprint, StringComparison.Ordinal))
                {
                    return Result<OperationStatusResult>.Failure(
                        "idempotency.payload_mismatch",
                        "Operation was previously submitted with a different payload.");
                }

                // Canonical states: NotFound, Pending, Succeeded, Failed, OutcomeUnknown
                return canonicalOutcome.State switch
                {
                    OperationOutcomeState.Succeeded => Result<OperationStatusResult>.Success(new OperationStatusResult(
                        ClientOperationId: canonicalOutcome.ClientOperationId,
                        Found: true,
                        OperationType: canonicalOutcome.OperationType,
                        EntityId: canonicalOutcome.EntityId,
                        DocumentNumber: canonicalOutcome.DocumentNumber,
                        WasCommitted: true,
                        PayloadFingerprint: canonicalOutcome.PayloadFingerprint,
                        Timestamp: canonicalOutcome.CompletedAt ?? canonicalOutcome.CreatedAt,
                        Status: "Succeeded")),

                    OperationOutcomeState.Pending => Result<OperationStatusResult>.Success(new OperationStatusResult(
                        ClientOperationId: canonicalOutcome.ClientOperationId,
                        Found: true,
                        OperationType: canonicalOutcome.OperationType,
                        EntityId: canonicalOutcome.EntityId,
                        DocumentNumber: canonicalOutcome.DocumentNumber,
                        WasCommitted: false,
                        PayloadFingerprint: canonicalOutcome.PayloadFingerprint,
                        Timestamp: canonicalOutcome.CreatedAt,
                        Status: "Pending")),

                    OperationOutcomeState.Failed => Result<OperationStatusResult>.Success(new OperationStatusResult(
                        ClientOperationId: canonicalOutcome.ClientOperationId,
                        Found: true,
                        OperationType: canonicalOutcome.OperationType,
                        EntityId: canonicalOutcome.EntityId,
                        DocumentNumber: canonicalOutcome.DocumentNumber,
                        WasCommitted: false,
                        PayloadFingerprint: canonicalOutcome.PayloadFingerprint,
                        Timestamp: canonicalOutcome.CompletedAt ?? canonicalOutcome.CreatedAt,
                        Status: "Failed",
                        ErrorCode: canonicalOutcome.ErrorCode,
                        ErrorMessage: canonicalOutcome.ErrorMessage)),

                    OperationOutcomeState.OutcomeUnknown => Result<OperationStatusResult>.Success(new OperationStatusResult(
                        ClientOperationId: canonicalOutcome.ClientOperationId,
                        Found: true,
                        OperationType: canonicalOutcome.OperationType,
                        EntityId: null,
                        DocumentNumber: null,
                        WasCommitted: false,
                        PayloadFingerprint: canonicalOutcome.PayloadFingerprint,
                        Timestamp: canonicalOutcome.CreatedAt,
                        Status: "OutcomeUnknown")),

                    OperationOutcomeState.NotFound => Result<OperationStatusResult>.Success(new OperationStatusResult(
                        ClientOperationId: query.ClientOperationId,
                        Found: false,
                        OperationType: null,
                        EntityId: null,
                        DocumentNumber: null,
                        WasCommitted: false,
                        Status: "NotFound")),

                    _ => Result<OperationStatusResult>.Success(new OperationStatusResult(
                        ClientOperationId: query.ClientOperationId,
                        Found: false,
                        OperationType: null,
                        EntityId: null,
                        DocumentNumber: null,
                        WasCommitted: false,
                        Status: "NotFound"))
                };
            }

            if (query.RequireCanonicalOutcome)
            {
                // Legacy business rows do not reliably retain both the authenticated actor and terminal.
                // Do not disclose or claim ownership of an unscoped legacy row from an API lookup.
                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: false,
                    OperationType: null,
                    EntityId: null,
                    DocumentNumber: null,
                    WasCommitted: false,
                    Status: "OutcomeUnknown"));
            }
        }

        // 2. Legacy Business Tables Fallback (consulted only when no canonical outcome exists)

        // 2.1 Sale
        var sale = await _sales.GetSaleByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (sale is not null)
        {
            var res = new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "Sale",
                EntityId: sale.Id,
                DocumentNumber: sale.InvoiceNumber,
                WasCommitted: true,
                Status: "Succeeded");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    query.ClientOperationId,
                    "Sale",
                    sale.Id,
                    sale.InvoiceNumber,
                    actorId: query.ActorId,
                    terminalId: query.TerminalId,
                    payloadFingerprint: query.PayloadFingerprint,
                    cancellationToken: cancellationToken);
            }

            return Result<OperationStatusResult>.Success(res);
        }

        // 2.2 Sale Return
        var saleReturn = await _sales.GetReturnByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (saleReturn is not null)
        {
            var res = new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "SaleReturn",
                EntityId: saleReturn.Id,
                DocumentNumber: saleReturn.ReturnNumber,
                WasCommitted: true,
                Status: "Succeeded");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    query.ClientOperationId,
                    "SaleReturn",
                    saleReturn.Id,
                    saleReturn.ReturnNumber,
                    actorId: query.ActorId,
                    terminalId: query.TerminalId,
                    payloadFingerprint: query.PayloadFingerprint,
                    cancellationToken: cancellationToken);
            }

            return Result<OperationStatusResult>.Success(res);
        }

        // 2.3 Purchase
        var purchase = await _purchasing.GetPurchaseByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (purchase is not null)
        {
            var res = new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "Purchase",
                EntityId: purchase.Id,
                DocumentNumber: purchase.PurchaseNumber,
                WasCommitted: true,
                Status: "Succeeded");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    query.ClientOperationId,
                    "Purchase",
                    purchase.Id,
                    purchase.PurchaseNumber,
                    actorId: query.ActorId,
                    terminalId: query.TerminalId,
                    payloadFingerprint: query.PayloadFingerprint,
                    cancellationToken: cancellationToken);
            }

            return Result<OperationStatusResult>.Success(res);
        }

        // 2.4 Purchase Return
        var purchaseReturn = await _purchasing.GetReturnByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (purchaseReturn is not null)
        {
            var res = new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "PurchaseReturn",
                EntityId: purchaseReturn.Id,
                DocumentNumber: purchaseReturn.ReturnNumber,
                WasCommitted: true,
                Status: "Succeeded");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    query.ClientOperationId,
                    "PurchaseReturn",
                    purchaseReturn.Id,
                    purchaseReturn.ReturnNumber,
                    actorId: query.ActorId,
                    terminalId: query.TerminalId,
                    payloadFingerprint: query.PayloadFingerprint,
                    cancellationToken: cancellationToken);
            }

            return Result<OperationStatusResult>.Success(res);
        }

        // 2.5 Purchase Void
        var purchaseVoid = await _purchasing.GetVoidByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (purchaseVoid is not null)
        {
            var res = new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "PurchaseVoid",
                EntityId: purchaseVoid.Id,
                DocumentNumber: purchaseVoid.Id.ToString("D"),
                WasCommitted: true,
                Status: "Succeeded");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    query.ClientOperationId,
                    "PurchaseVoid",
                    purchaseVoid.Id,
                    purchaseVoid.Id.ToString("D"),
                    actorId: query.ActorId,
                    terminalId: query.TerminalId,
                    payloadFingerprint: query.PayloadFingerprint,
                    cancellationToken: cancellationToken);
            }

            return Result<OperationStatusResult>.Success(res);
        }

        // 2.6 Supplier Payment
        var payment = await _supplierAccounts.GetPaymentByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (payment is not null)
        {
            var res = new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "SupplierPayment",
                EntityId: payment.Id,
                DocumentNumber: payment.PaymentNumber,
                WasCommitted: true,
                Status: "Succeeded");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    query.ClientOperationId,
                    "SupplierPayment",
                    payment.Id,
                    payment.PaymentNumber,
                    actorId: query.ActorId,
                    terminalId: query.TerminalId,
                    payloadFingerprint: query.PayloadFingerprint,
                    cancellationToken: cancellationToken);
            }

            return Result<OperationStatusResult>.Success(res);
        }

        // 2.7 Supplier Refund
        var refund = await _supplierAccounts.GetRefundByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (refund is not null)
        {
            var res = new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "SupplierRefund",
                EntityId: refund.Id,
                DocumentNumber: refund.RefundNumber,
                WasCommitted: true,
                Status: "Succeeded");

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    query.ClientOperationId,
                    "SupplierRefund",
                    refund.Id,
                    refund.RefundNumber,
                    actorId: query.ActorId,
                    terminalId: query.TerminalId,
                    payloadFingerprint: query.PayloadFingerprint,
                    cancellationToken: cancellationToken);
            }

            return Result<OperationStatusResult>.Success(res);
        }

        // 2.8 Inventory Movement
        if (_inventory is not null)
        {
            var movement = await _inventory.GetMovementByCorrelationIdAsync(query.ClientOperationId, cancellationToken);
            if (movement is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "InventoryMovement",
                        movement.Id,
                        movement.ReferenceId?.ToString("D"),
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "InventoryMovement",
                    EntityId: movement.Id,
                    DocumentNumber: movement.ReferenceId?.ToString("D"),
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }
        }

        // 2.9 Expense
        if (_expenses is not null)
        {
            var expense = await _expenses.GetByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
            if (expense is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "Expense",
                        expense.Id,
                        expense.ExpenseNumber,
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "Expense",
                    EntityId: expense.Id,
                    DocumentNumber: expense.ExpenseNumber,
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }
        }

        // 2.10 Warranty Claim & Operation
        if (_warranty is not null)
        {
            var claim = await _warranty.GetClaimByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
            if (claim is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "WarrantyClaim",
                        claim.Id,
                        claim.ClaimNumber,
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "WarrantyClaim",
                    EntityId: claim.Id,
                    DocumentNumber: claim.ClaimNumber,
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }

            var warrantyOp = await _warranty.GetOperationByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
            if (warrantyOp is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "WarrantyOperation",
                        warrantyOp.Id,
                        warrantyOp.Id.ToString("D"),
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "WarrantyOperation",
                    EntityId: warrantyOp.Id,
                    DocumentNumber: warrantyOp.Id.ToString("D"),
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }
        }

        // 2.11 Quotation Operation
        if (_quotations is not null)
        {
            var quoteOp = await _quotations.GetOperationByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
            if (quoteOp is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "QuotationOperation",
                        quoteOp.Id,
                        quoteOp.Id.ToString("D"),
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "QuotationOperation",
                    EntityId: quoteOp.Id,
                    DocumentNumber: quoteOp.Id.ToString("D"),
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }
        }

        // 2.12 Thaka Operations
        if (_thaka is not null)
        {
            var issue = await _thaka.GetIssueByOperationIdAsync(query.ClientOperationId, cancellationToken);
            if (issue is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "ThakaMaterialIssue",
                        issue.Id,
                        issue.ChallanNumber,
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "ThakaMaterialIssue",
                    EntityId: issue.Id,
                    DocumentNumber: issue.ChallanNumber,
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }

            var thakaPayment = await _thaka.GetPaymentByOperationIdAsync(query.ClientOperationId, cancellationToken);
            if (thakaPayment is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "ThakaPayment",
                        thakaPayment.Id,
                        thakaPayment.ReceiptNumber,
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "ThakaPayment",
                    EntityId: thakaPayment.Id,
                    DocumentNumber: thakaPayment.ReceiptNumber,
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }

            var settlement = await _thaka.GetSettlementByOperationIdAsync(query.ClientOperationId, cancellationToken);
            if (settlement is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "ThakaSettlement",
                        settlement.Id,
                        settlement.SettlementNumber,
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "ThakaSettlement",
                    EntityId: settlement.Id,
                    DocumentNumber: settlement.SettlementNumber,
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }

            var matRev = await _thaka.GetMaterialReversalByOperationIdAsync(query.ClientOperationId, cancellationToken);
            if (matRev is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "ThakaMaterialReversal",
                        matRev.Id,
                        matRev.ReversalNumber,
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "ThakaMaterialReversal",
                    EntityId: matRev.Id,
                    DocumentNumber: matRev.ReversalNumber,
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }

            var payRev = await _thaka.GetPaymentReversalByOperationIdAsync(query.ClientOperationId, cancellationToken);
            if (payRev is not null)
            {
                if (_outcomeLedger is not null)
                {
                    await _outcomeLedger.RecordSuccessAsync(
                        query.ClientOperationId,
                        "ThakaPaymentReversal",
                        payRev.Id,
                        payRev.ReversalNumber,
                        actorId: query.ActorId,
                        terminalId: query.TerminalId,
                        payloadFingerprint: query.PayloadFingerprint,
                        cancellationToken: cancellationToken);
                }

                return Result<OperationStatusResult>.Success(new OperationStatusResult(
                    ClientOperationId: query.ClientOperationId,
                    Found: true,
                    OperationType: "ThakaPaymentReversal",
                    EntityId: payRev.Id,
                    DocumentNumber: payRev.ReversalNumber,
                    WasCommitted: true,
                    PayloadFingerprint: query.PayloadFingerprint,
                    Status: "Succeeded"));
            }
        }

        return Result<OperationStatusResult>.Success(new OperationStatusResult(
            ClientOperationId: query.ClientOperationId,
            Found: false,
            OperationType: null,
            EntityId: null,
            DocumentNumber: null,
            WasCommitted: false,
            Status: "NotFound"));
    }
}
