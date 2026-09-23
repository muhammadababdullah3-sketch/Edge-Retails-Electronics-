using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeRetails.Desktop.Services;

public sealed class DemoTransactionService : ITransactionService
{
    private static readonly Lazy<DemoTransactionService> s_instance = new(() => new DemoTransactionService());
    public static DemoTransactionService Instance => s_instance.Value;

    private readonly Lock _syncRoot = new();
    private readonly List<SaleTransactionRecord> _transactions = [];
    private readonly List<SaleReturnRecord> _returns = [];
    private int _nextInvoiceSequence = 1293;
    private int _nextReturnSequence = 1;

    public event EventHandler<SaleTransactionRecord>? TransactionRecorded;
    public event EventHandler<SaleReturnRecord>? ReturnRecorded;

    public DemoTransactionService()
    {
        SeedDemoData();
    }

    public decimal TodaySales
    {
        get
        {
            lock (_syncRoot)
            {
                return _transactions
                    .Where(t => t.Timestamp.Date == DateTime.Today)
                    .Sum(t => t.TotalAmount);
            }
        }
    }

    public int TodayCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _transactions.Count(t => t.Timestamp.Date == DateTime.Today);
            }
        }
    }

    public decimal TotalReturns
    {
        get
        {
            lock (_syncRoot)
            {
                return _returns
                    .Where(r => r.Timestamp.Date == DateTime.Today)
                    .Sum(r => r.TotalRefundAmount);
            }
        }
    }

    public decimal NetSales => Math.Max(0m, TodaySales - TotalReturns);

    public SaleTransactionRecord RecordTransaction(RecordSaleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.TotalAmount <= 0m)
        {
            throw new ArgumentException("Total amount must be greater than zero.", nameof(request));
        }

        if (request.Subtotal < request.TotalAmount)
        {
            throw new InvalidOperationException("Subtotal cannot be lower than the final total.");
        }

        if (request.DiscountAmount < 0m)
        {
            throw new InvalidOperationException("Discount amount cannot be negative.");
        }

        if (request.PaymentMethod == PaymentMethod.Cash && request.AmountReceived < request.TotalAmount)
        {
            throw new InvalidOperationException(
                $"Cash payment amount received (Rs. {request.AmountReceived:N0}) is less than total to pay (Rs. {request.TotalAmount:N0}).");
        }

        if ((request.PaymentMethod == PaymentMethod.Bank || request.PaymentMethod == PaymentMethod.Other) &&
            request.AmountReceived != request.TotalAmount)
        {
            throw new InvalidOperationException(
                $"{request.PaymentMethod} payment requires exact amount (Rs. {request.TotalAmount:N0}).");
        }

        SaleTransactionRecord record;
        lock (_syncRoot)
        {
            var invoiceNum = $"#{_nextInvoiceSequence++}";
            var change = Math.Max(0m, request.AmountReceived - request.TotalAmount);

            record = new SaleTransactionRecord
            {
                InvoiceNumber = invoiceNum,
                Timestamp = DateTime.Now,
                CustomerName = string.IsNullOrWhiteSpace(request.CustomerName) ? "Walk-in Customer" : request.CustomerName.Trim(),
                CustomerPhone = request.CustomerPhone,
                PaymentMethod = request.PaymentMethod,
                PaymentState = PaymentState.Paid,
                Subtotal = request.Subtotal > 0m ? request.Subtotal : request.TotalAmount,
                DiscountAmount = request.DiscountAmount,
                TotalAmount = request.TotalAmount,
                AmountReceived = request.AmountReceived,
                ChangeReturned = change,
                PrintReceipt = request.PrintReceipt,
                CashierName = string.IsNullOrWhiteSpace(request.CashierName) ? "Abdullah, Owner" : request.CashierName,
                PaymentReference = request.PaymentReference?.Trim() ?? string.Empty,
                Notes = request.Notes,
                Items = request.Items != null && request.Items.Count > 0
                    ? request.Items
                    :
                    [
                        new SaleTransactionItem
                        {
                            ProductId = "P-CUSTOM",
                            ProductName = "POS Sale Items",
                            Sku = "SALE-ITEMS",
                            UnitPrice = request.TotalAmount,
                            Quantity = 1m,
                            Discount = 0m,
                            LineTotal = request.TotalAmount,
                            UnitCostSnapshot = 0m
                        }
                    ]
            };

            _transactions.Insert(0, record);
        }

        TransactionRecorded?.Invoke(this, record);
        return record;
    }

    public async Task<SaleTransactionRecord> RecordTransactionAsync(
        RecordSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        return RecordTransaction(request);
    }

    public IReadOnlyList<SaleTransactionRecord> GetAllTransactions()
    {
        lock (_syncRoot)
        {
            return _transactions.ToList().AsReadOnly();
        }
    }

    public Task<IReadOnlyList<SaleTransactionRecord>> GetAllTransactionsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetAllTransactions());
    }

    public SaleTransactionRecord? GetByInvoiceNumber(string invoiceNumber)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return null;
        }

        var normalized = NormalizeInvoice(invoiceNumber);
        lock (_syncRoot)
        {
            return _transactions.FirstOrDefault(t =>
                string.Equals(t.InvoiceNumber, normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    public Task<SaleTransactionRecord?> GetByInvoiceNumberAsync(
        string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetByInvoiceNumber(invoiceNumber));
    }

    public SaleReturnRecord RecordReturn(RecordSaleReturnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Items.Count == 0 || request.TotalRefundAmount <= 0m)
        {
            throw new InvalidOperationException("Return must contain at least one item and a positive refund amount.");
        }

        var invoiceNumber = NormalizeInvoice(request.InvoiceNumber);
        SaleReturnRecord record;

        lock (_syncRoot)
        {
            var original = _transactions.FirstOrDefault(t =>
                string.Equals(t.InvoiceNumber, invoiceNumber, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Invoice {invoiceNumber} was not found.");

            foreach (var item in request.Items)
            {
                if (item.Quantity <= 0m || item.RefundAmount < 0m)
                {
                    throw new InvalidOperationException("Return quantity and refund values must be valid.");
                }

                var soldQuantity = original.Items
                    .Where(i => string.Equals(i.ProductId, item.ProductId, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(i.Sku, item.Sku, StringComparison.OrdinalIgnoreCase))
                    .Sum(i => i.Quantity);

                var alreadyReturned = _returns
                    .Where(r => string.Equals(r.InvoiceNumber, invoiceNumber, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(r => r.Items)
                    .Where(i => string.Equals(i.ProductId, item.ProductId, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(i.Sku, item.Sku, StringComparison.OrdinalIgnoreCase))
                    .Sum(i => i.Quantity);

                if (item.Quantity > soldQuantity - alreadyReturned)
                {
                    throw new InvalidOperationException(
                        $"Return quantity for {item.Sku} exceeds the remaining eligible quantity.");
                }
            }

            record = new SaleReturnRecord
            {
                ReturnNumber = $"RET-{_nextReturnSequence++:D4}",
                InvoiceNumber = invoiceNumber,
                Timestamp = DateTime.Now,
                Disposition = request.Disposition,
                RefundMethod = request.RefundMethod,
                Notes = request.Notes,
                TotalRefundAmount = request.TotalRefundAmount,
                Items = [.. request.Items]
            };

            _returns.Insert(0, record);
        }

        ReturnRecorded?.Invoke(this, record);
        return record;
    }

    public async Task<SaleReturnRecord> RecordReturnAsync(
        RecordSaleReturnRequest request,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        return RecordReturn(request);
    }

    public IReadOnlyList<SaleReturnRecord> GetReturnsForInvoice(string invoiceNumber)
    {
        var normalized = NormalizeInvoice(invoiceNumber);
        lock (_syncRoot)
        {
            return _returns
                .Where(r => string.Equals(r.InvoiceNumber, normalized, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Timestamp)
                .ToList()
                .AsReadOnly();
        }
    }

    public IReadOnlyList<SaleReturnRecord> GetAllReturns()
    {
        lock (_syncRoot)
        {
            return _returns
                .OrderByDescending(r => r.Timestamp)
                .ToList()
                .AsReadOnly();
        }
    }

    private static string NormalizeInvoice(string invoiceNumber)
    {
        var normalized = invoiceNumber.Trim();
        return normalized.StartsWith('#') ? normalized : "#" + normalized;
    }

    private void SeedDemoData()
    {
        var today = DateTime.Today;

        _transactions.AddRange(
        [
            new SaleTransactionRecord
            {
                InvoiceNumber = "#1292",
                Timestamp = today.AddHours(16).AddMinutes(20),
                CustomerName = "Walk-in Customer",
                PaymentMethod = PaymentMethod.Cash,
                PaymentState = PaymentState.Paid,
                Subtotal = 3200m,
                TotalAmount = 3200m,
                AmountReceived = 3200m,
                CashierName = "Abdullah, Owner",
                Items =
                [
                    Item("P1", "LED Bulb 12W", "PH-LED-12W", "Philips", 500m, 2m),
                    Item("P2", "Switch 16A", "LG-SW-16A", "Legrand", 300m, 2m),
                    Item("P5", "Socket 16A", "MK-SK-16A", "MK", 400m, 2m),
                    Item("P6", "MCB 20A", "HG-MCB-20A", "Hager", 800m, 1m)
                ]
            },
            new SaleTransactionRecord
            {
                InvoiceNumber = "#1291",
                Timestamp = today.AddHours(14).AddMinutes(15),
                CustomerName = "Haji Ahmed",
                CustomerPhone = "0300-5551234",
                PaymentMethod = PaymentMethod.Bank,
                PaymentState = PaymentState.Paid,
                Subtotal = 15400m,
                TotalAmount = 15400m,
                AmountReceived = 15400m,
                CashierName = "Abdullah, Owner",
                Items =
                [
                    Item("P4", "Wire 2.5mm", "PC-WR-25", "Pak Cables", 6500m, 2m),
                    Item("P2", "Switch 16A", "LG-SW-16A", "Legrand", 300m, 8m)
                ]
            },
            new SaleTransactionRecord
            {
                InvoiceNumber = "#1290",
                Timestamp = today.AddHours(11).AddMinutes(30),
                CustomerName = "Ali Raza",
                CustomerPhone = "0321-9876543",
                PaymentMethod = PaymentMethod.Cash,
                PaymentState = PaymentState.Paid,
                Subtotal = 1450m,
                TotalAmount = 1450m,
                AmountReceived = 1450m,
                CashierName = "Abdullah, Owner",
                Items =
                [
                    Item("P3", "Breaker 32A", "SC-BR-32A", "Schneider", 1450m, 1m)
                ]
            },
            new SaleTransactionRecord
            {
                InvoiceNumber = "#1289",
                Timestamp = today.AddHours(10).AddMinutes(5),
                CustomerName = "Usman Farooq",
                CustomerPhone = "0333-1122334",
                PaymentMethod = PaymentMethod.Cash,
                PaymentState = PaymentState.Paid,
                Subtotal = 8250m,
                TotalAmount = 8250m,
                AmountReceived = 8250m,
                CashierName = "Abdullah, Owner",
                Items =
                [
                    Item("P4", "Wire 2.5mm", "PC-WR-25", "Pak Cables", 6500m, 1m),
                    Item("P6", "MCB 20A", "HG-MCB-20A", "Hager", 850m, 2m),
                    Item("P1", "LED Bulb 12W", "PH-LED-12W", "Philips", 500m, 1m, 450m)
                ]
            },
            new SaleTransactionRecord
            {
                InvoiceNumber = "#1288",
                Timestamp = today.AddHours(9).AddMinutes(40),
                CustomerName = "Tariq Mahmood",
                CustomerPhone = "0345-4455667",
                PaymentMethod = PaymentMethod.Other,
                PaymentState = PaymentState.Partial,
                Subtotal = 6800m,
                TotalAmount = 6800m,
                AmountReceived = 5000m,
                CashierName = "Abdullah, Owner",
                Notes = "Demo partial payment record",
                Items =
                [
                    Item("P8", "CCTV Cable 3+1", "CS-CC-31", "Commscope", 2800m, 2m),
                    Item("P2", "Switch 16A", "LG-SW-16A", "Legrand", 300m, 4m)
                ]
            },
            new SaleTransactionRecord
            {
                InvoiceNumber = "#1287",
                Timestamp = today.AddHours(9).AddMinutes(15),
                CustomerName = "Walk-in Customer",
                PaymentMethod = PaymentMethod.Cash,
                PaymentState = PaymentState.Paid,
                Subtotal = 3400m,
                TotalAmount = 3400m,
                AmountReceived = 3400m,
                CashierName = "Abdullah, Owner",
                Items =
                [
                    Item("P6", "MCB 20A", "HG-MCB-20A", "Hager", 850m, 4m)
                ]
            }
        ]);

        _returns.Add(new SaleReturnRecord
        {
            ReturnNumber = "RET-0001",
            InvoiceNumber = "#1290",
            Timestamp = today.AddHours(12),
            Disposition = SaleReturnDisposition.Defective,
            RefundMethod = "Cash Refund",
            Notes = "Defective breaker return",
            TotalRefundAmount = 1450m,
            Items =
            [
                new SaleReturnItemRecord
                {
                    ProductId = "P3",
                    Sku = "SC-BR-32A",
                    Quantity = 1m,
                    RefundAmount = 1450m
                }
            ]
        });

        _nextReturnSequence = 2;
    }

    private static SaleTransactionItem Item(
        string productId,
        string name,
        string sku,
        string brand,
        decimal unitPrice,
        decimal quantity,
        decimal discount = 0m)
    {
        var unitCost = DemoRetailState.Instance.Products
            .FirstOrDefault(product => string.Equals(
                product.Id,
                productId,
                StringComparison.OrdinalIgnoreCase))?.Cost ?? 0m;

        return new SaleTransactionItem
        {
            ProductId = productId,
            ProductName = name,
            Sku = sku,
            Brand = brand,
            UnitPrice = unitPrice,
            Quantity = quantity,
            Discount = discount,
            LineTotal = Math.Max(0m, Math.Round((unitPrice * quantity) - discount, 2)),
            UnitCostSnapshot = unitCost
        };
    }
}
