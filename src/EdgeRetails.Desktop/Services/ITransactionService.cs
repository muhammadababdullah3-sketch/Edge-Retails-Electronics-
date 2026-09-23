using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeRetails.Desktop.Services;

public enum PaymentMethod
{
    Cash,
    Bank,
    Other
}

public enum PaymentState
{
    Paid,
    Partial
}

public enum SaleReturnState
{
    None,
    Partial,
    Refunded
}

public enum SaleReturnDisposition
{
    RestockSellable,
    Defective,
    Damaged,
    Other
}

public sealed class SaleTransactionItem
{
    public string ProductId { get; init; } = string.Empty;
    public Guid? BackendProductId { get; init; }
    public Guid? BackendProductUnitId { get; init; }
    public IReadOnlyList<Guid> InventoryUnitIds { get; init; } = Array.Empty<Guid>();
    public string ProductName { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public string Brand { get; init; } = string.Empty;
    public decimal UnitPrice { get; init; }
    public decimal Quantity { get; init; }
    public decimal Discount { get; init; }
    public decimal LineTotal { get; init; }
    public decimal UnitCostSnapshot { get; init; }
    public decimal TotalCostSnapshot => Math.Round(UnitCostSnapshot * Quantity, 2);
    public decimal GrossProfitSnapshot => Math.Round(LineTotal - TotalCostSnapshot, 2);

    public decimal NetUnitPrice => Quantity > 0m ? Math.Round(LineTotal / Quantity, 2) : UnitPrice;
    public string UnitPriceDisplay => $"Rs. {UnitPrice:N0}";
    public string LineTotalDisplay => $"Rs. {LineTotal:N0}";
    public string QuantityDisplay => Quantity % 1 == 0 ? $"{Quantity:0}" : $"{Quantity:0.##}";
}

public sealed class SaleTransactionRecord
{
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string CustomerName { get; init; } = "Walk-in Customer";
    public string CustomerPhone { get; init; } = string.Empty;
    public PaymentMethod PaymentMethod { get; init; } = PaymentMethod.Cash;
    public PaymentState PaymentState { get; init; } = PaymentState.Paid;
    public decimal Subtotal { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal AmountReceived { get; init; }
    public decimal ChangeReturned { get; init; }
    public bool PrintReceipt { get; init; } = true;
    public string CashierName { get; init; } = "Abdullah, Owner";
    public string PaymentReference { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public IReadOnlyList<SaleTransactionItem> Items { get; init; } = [];

    public int ItemsCount => Items.Count > 0 ? Items.Count : 1;
    public string TotalDisplay => $"Rs. {TotalAmount:N0}";
    public string AmountReceivedDisplay => $"Rs. {AmountReceived:N0}";
    public string ChangeReturnedDisplay => $"Rs. {ChangeReturned:N0}";
    public string DateFormatted => Timestamp.ToString("dd MMM yyyy");
    public string TimeFormatted => Timestamp.ToString("hh:mm tt");
    public string PaymentMethodDisplay => PaymentMethod switch
    {
        PaymentMethod.Cash => "Cash",
        PaymentMethod.Bank => "Bank",
        PaymentMethod.Other => "Other",
        _ => PaymentMethod.ToString()
    };
    public string PaymentStateDisplay => PaymentState == PaymentState.Partial ? "PARTIAL PAYMENT" : "PAID";
}

public sealed class RecordSaleRequest
{
    public Guid ClientOperationId { get; init; }
    public Guid? DraftId { get; init; }
    public long? DraftVersion { get; init; }
    public Guid? CustomerId { get; init; }
    public decimal TotalAmount { get; init; }
    public PaymentMethod PaymentMethod { get; init; } = PaymentMethod.Cash;
    public decimal AmountReceived { get; init; }
    public decimal ChangeReturned { get; init; }
    public bool PrintReceipt { get; init; } = true;
    public string CustomerName { get; init; } = "Walk-in Customer";
    public string CustomerPhone { get; init; } = string.Empty;
    public string CashierName { get; init; } = "Abdullah, Owner";
    public string PaymentReference { get; init; } = string.Empty;
    public string? Notes { get; init; }
    public decimal Subtotal { get; init; }
    public decimal DiscountAmount { get; init; }
    public IReadOnlyList<SaleTransactionItem>? Items { get; init; }
}

public sealed class SaleReturnItemRecord
{
    public string ProductId { get; init; } = string.Empty;
    public string Sku { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal RefundAmount { get; init; }
}

public sealed class SaleReturnRecord
{
    public string ReturnNumber { get; init; } = string.Empty;
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public SaleReturnDisposition Disposition { get; init; }
    public string RefundMethod { get; init; } = "Cash Refund";
    public string Notes { get; init; } = string.Empty;
    public decimal TotalRefundAmount { get; init; }
    public IReadOnlyList<SaleReturnItemRecord> Items { get; init; } = [];
}

public sealed class RecordSaleReturnRequest
{
    public string InvoiceNumber { get; init; } = string.Empty;
    public SaleReturnDisposition Disposition { get; init; }
    public string RefundMethod { get; init; } = "Cash Refund";
    public string Notes { get; init; } = string.Empty;
    public decimal TotalRefundAmount { get; init; }
    public IReadOnlyList<SaleReturnItemRecord> Items { get; init; } = [];
}

public interface ITransactionService
{
    Task<SaleTransactionRecord> RecordTransactionAsync(RecordSaleRequest request, CancellationToken cancellationToken = default);
    SaleTransactionRecord RecordTransaction(RecordSaleRequest request);
    Task<IReadOnlyList<SaleTransactionRecord>> GetAllTransactionsAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<SaleTransactionRecord> GetAllTransactions();
    Task<SaleTransactionRecord?> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default);
    SaleTransactionRecord? GetByInvoiceNumber(string invoiceNumber);

    Task<SaleReturnRecord> RecordReturnAsync(RecordSaleReturnRequest request, CancellationToken cancellationToken = default);
    SaleReturnRecord RecordReturn(RecordSaleReturnRequest request);
    IReadOnlyList<SaleReturnRecord> GetReturnsForInvoice(string invoiceNumber);
    IReadOnlyList<SaleReturnRecord> GetAllReturns();

    event EventHandler<SaleTransactionRecord>? TransactionRecorded;
    event EventHandler<SaleReturnRecord>? ReturnRecorded;

    decimal TodaySales { get; }
    int TodayCount { get; }
    decimal TotalReturns { get; }
    decimal NetSales { get; }
}
