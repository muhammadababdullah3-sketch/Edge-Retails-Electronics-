using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Gateways;

namespace EdgeRetails.Application.Features.Terminals;

public sealed record OperationStatusQuery(Guid ClientOperationId);

public sealed class OperationStatusQueryHandler
{
    private readonly ISalesRepository _sales;
    private readonly IPurchasingRepository _purchasing;
    private readonly ISupplierAccountRepository _supplierAccounts;

    public OperationStatusQueryHandler(
        ISalesRepository sales,
        IPurchasingRepository purchasing,
        ISupplierAccountRepository supplierAccounts)
    {
        _sales = sales;
        _purchasing = purchasing;
        _supplierAccounts = supplierAccounts;
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

        // 1. Sale
        var sale = await _sales.GetSaleByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (sale is not null)
        {
            return Result<OperationStatusResult>.Success(new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "Sale",
                EntityId: sale.Id,
                DocumentNumber: sale.InvoiceNumber,
                WasCommitted: true));
        }

        // 2. Sale Return
        var saleReturn = await _sales.GetReturnByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (saleReturn is not null)
        {
            return Result<OperationStatusResult>.Success(new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "SaleReturn",
                EntityId: saleReturn.Id,
                DocumentNumber: saleReturn.ReturnNumber,
                WasCommitted: true));
        }

        // 3. Purchase
        var purchase = await _purchasing.GetPurchaseByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (purchase is not null)
        {
            return Result<OperationStatusResult>.Success(new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "Purchase",
                EntityId: purchase.Id,
                DocumentNumber: purchase.PurchaseNumber,
                WasCommitted: true));
        }

        // 4. Purchase Return
        var purchaseReturn = await _purchasing.GetReturnByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (purchaseReturn is not null)
        {
            return Result<OperationStatusResult>.Success(new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "PurchaseReturn",
                EntityId: purchaseReturn.Id,
                DocumentNumber: purchaseReturn.ReturnNumber,
                WasCommitted: true));
        }

        // 5. Purchase Void
        var purchaseVoid = await _purchasing.GetVoidByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (purchaseVoid is not null)
        {
            return Result<OperationStatusResult>.Success(new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "PurchaseVoid",
                EntityId: purchaseVoid.Id,
                DocumentNumber: purchaseVoid.Id.ToString("D"),
                WasCommitted: true));
        }

        // 6. Supplier Payment
        var payment = await _supplierAccounts.GetPaymentByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (payment is not null)
        {
            return Result<OperationStatusResult>.Success(new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "SupplierPayment",
                EntityId: payment.Id,
                DocumentNumber: payment.PaymentNumber,
                WasCommitted: true));
        }

        // 7. Supplier Refund
        var refund = await _supplierAccounts.GetRefundByClientOperationIdAsync(query.ClientOperationId, cancellationToken);
        if (refund is not null)
        {
            return Result<OperationStatusResult>.Success(new OperationStatusResult(
                ClientOperationId: query.ClientOperationId,
                Found: true,
                OperationType: "SupplierRefund",
                EntityId: refund.Id,
                DocumentNumber: refund.RefundNumber,
                WasCommitted: true));
        }

        return Result<OperationStatusResult>.Success(new OperationStatusResult(
            ClientOperationId: query.ClientOperationId,
            Found: false,
            OperationType: null,
            EntityId: null,
            DocumentNumber: null,
            WasCommitted: false));
    }
}
