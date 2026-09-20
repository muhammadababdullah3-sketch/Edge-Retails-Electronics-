using System.Collections.ObjectModel;
using EdgeRetails.Desktop.Controls;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Services;

public enum DemoStockMutationKind
{
    SaleOut,
    SaleReturnIn,
    ThakaOut,
    Damage
}

public sealed class DemoStockMutationRecord : EventArgs
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public required string ProductId { get; init; }
    public required string ProductName { get; init; }
    public DemoStockMutationKind Kind { get; init; }
    public decimal QuantityDelta { get; init; }
    public decimal BeforeQuantity { get; init; }
    public decimal AfterQuantity { get; init; }
    public string Reference { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class DemoRetailState
{
    private static readonly Lazy<DemoRetailState> s_instance = new(() => new DemoRetailState());
    private readonly Dictionary<string, ObservableCollection<MaterialLedgerEntry>> _materialLedgers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ObservableCollection<PaymentLedgerEntry>> _paymentLedgers = new(StringComparer.OrdinalIgnoreCase);
    private int _challanSequence = 100;

    public static DemoRetailState Instance => s_instance.Value;

    public event EventHandler? StateChanged;
    public event EventHandler<DemoStockMutationRecord>? StockMutationRecorded;

    public List<DemoStockMutationRecord> StockMutations { get; } = [];
    public List<PosProductItemViewModel> Products { get; }

    public ObservableCollection<ThakaProjectListItemViewModel> ThakaProjects { get; }

    private DemoRetailState()
    {
        Products = CreateProducts();
        ThakaProjects = CreateProjects();
        SeedLedgers();
    }
    public IReadOnlyList<ThakaProjectListItemViewModel> GetActiveProjects() =>
        [.. ThakaProjects.Where(project => project.IsActive)];

    public ObservableCollection<MaterialLedgerEntry> GetMaterialLedger(ThakaProjectListItemViewModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return GetOrCreateMaterialLedger(project.Id);
    }

    public ObservableCollection<PaymentLedgerEntry> GetPaymentLedger(ThakaProjectListItemViewModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return GetOrCreatePaymentLedger(project.Id);
    }

    public void AddProject(ThakaProjectListItemViewModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (ThakaProjects.Any(existing => string.Equals(existing.Id, project.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A Thaka project with id {project.Id} already exists.");
        }

        GetOrCreateMaterialLedger(project.Id);
        GetOrCreatePaymentLedger(project.Id);
        ThakaProjects.Insert(0, project);
        RaiseStateChanged();
    }

    public void AddProduct(PosProductItemViewModel product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (Products.Any(existing =>
            string.Equals(existing.Id, product.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(existing.Sku, product.Sku, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("A product with the same ID or SKU already exists.");
        }

        Products.Add(product);
        RaiseStateChanged();
    }

    public void NotifyProductChanged() => RaiseStateChanged();

    public void ApplyLocalSaleStock(
        IEnumerable<(PosProductItemViewModel Product, decimal Quantity)> items,
        string reference = "SALE")
    {
        ArgumentNullException.ThrowIfNull(items);
        var lines = items.ToArray();

        foreach (var (product, quantityValue) in lines)
        {
            var quantity = Math.Round(quantityValue, 2);
            if (quantity <= 0m || quantity > product.Stock)
            {
                throw new InvalidOperationException(
                    $"Insufficient stock for {product.Name}. Available: {product.Stock:0.##} {product.Unit}.");
            }
        }

        foreach (var (product, quantityValue) in lines)
        {
            var quantity = Math.Round(quantityValue, 2);
            var before = product.Stock;
            product.Stock = Math.Round(before - quantity, 2);
            RecordStockMutation(new DemoStockMutationRecord
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Kind = DemoStockMutationKind.SaleOut,
                QuantityDelta = -quantity,
                BeforeQuantity = before,
                AfterQuantity = product.Stock,
                Reference = reference,
                Reason = "Retail sale"
            });
        }

        RaiseStateChanged();
    }

    public void ApplySaleReturnStock(SaleReturnRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        foreach (var item in record.Items)
        {
            var product = Products.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, item.ProductId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Sku, item.Sku, StringComparison.OrdinalIgnoreCase));
            if (product is null || item.Quantity <= 0m)
            {
                continue;
            }

            var quantity = Math.Round(item.Quantity, 2);
            var before = product.Stock;

            if (record.Disposition == SaleReturnDisposition.RestockSellable)
            {
                product.Stock = Math.Round(before + quantity, 2);
                RecordStockMutation(new DemoStockMutationRecord
                {
                    Timestamp = record.Timestamp,
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Kind = DemoStockMutationKind.SaleReturnIn,
                    QuantityDelta = quantity,
                    BeforeQuantity = before,
                    AfterQuantity = product.Stock,
                    Reference = record.ReturnNumber,
                    Reason = $"Restocked from {record.InvoiceNumber}"
                });
            }
            else
            {
                RecordStockMutation(new DemoStockMutationRecord
                {
                    Timestamp = record.Timestamp,
                    ProductId = product.Id,
                    ProductName = product.Name,
                    Kind = DemoStockMutationKind.Damage,
                    QuantityDelta = 0m,
                    BeforeQuantity = before,
                    AfterQuantity = before,
                    Reference = record.ReturnNumber,
                    Reason = $"{record.Disposition} return from {record.InvoiceNumber}; not added to sellable stock"
                });
            }
        }

        RaiseStateChanged();
    }

    public MaterialLedgerEntry IssueMaterial(
        ThakaProjectListItemViewModel project,
        PosProductItemViewModel product,
        decimal quantity)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(product);
        ValidateMaterialIssue(project, product, quantity);

        var roundedQuantity = Math.Round(quantity, 2);
        var value = Math.Round(product.Price * roundedQuantity, 2);
        var before = product.Stock;
        var challanNumber = $"CH-{++_challanSequence:D4}";
        product.Stock = Math.Round(before - roundedQuantity, 2);

        var entry = new MaterialLedgerEntry
        {
            Date = DateTime.Now,
            ChallanNumber = challanNumber,
            ProductName = product.Name,
            Quantity = roundedQuantity,
            Unit = product.Unit,
            Rate = product.Price,
            TotalValue = value
        };

        GetOrCreateMaterialLedger(project.Id).Insert(0, entry);
        project.MaterialValue = Math.Round(project.MaterialValue + value, 2);
        RecordStockMutation(new DemoStockMutationRecord
        {
            ProductId = product.Id,
            ProductName = product.Name,
            Kind = DemoStockMutationKind.ThakaOut,
            QuantityDelta = -roundedQuantity,
            BeforeQuantity = before,
            AfterQuantity = product.Stock,
            Reference = project.Id,
            Reason = $"Material issue {challanNumber}"
        });
        RaiseStateChanged();
        return entry;
    }
    public IReadOnlyList<MaterialLedgerEntry> IssueMaterialBatch(
        ThakaProjectListItemViewModel project,
        IEnumerable<(PosProductItemViewModel Product, decimal Quantity)> items)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(items);

        var lines = items.ToArray();
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("Material issue requires at least one item.");
        }

        foreach (var (Product, Quantity) in lines)
        {
            ValidateMaterialIssue(project, Product, Quantity);
        }

        var created = new List<MaterialLedgerEntry>(lines.Length);
        foreach (var (product, quantityValue) in lines)
        {
            var roundedQuantity = Math.Round(quantityValue, 2);
            var value = Math.Round(product.Price * roundedQuantity, 2);
            var before = product.Stock;
            var challanNumber = $"CH-{++_challanSequence:D4}";
            product.Stock = Math.Round(before - roundedQuantity, 2);

            var entry = new MaterialLedgerEntry
            {
                Date = DateTime.Now,
                ChallanNumber = challanNumber,
                ProductName = product.Name,
                Quantity = roundedQuantity,
                Unit = product.Unit,
                Rate = product.Price,
                TotalValue = value
            };

            GetOrCreateMaterialLedger(project.Id).Insert(0, entry);
            project.MaterialValue = Math.Round(project.MaterialValue + value, 2);
            created.Add(entry);
            RecordStockMutation(new DemoStockMutationRecord
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Kind = DemoStockMutationKind.ThakaOut,
                QuantityDelta = -roundedQuantity,
                BeforeQuantity = before,
                AfterQuantity = product.Stock,
                Reference = project.Id,
                Reason = $"Material issue {challanNumber}"
            });
        }

        RaiseStateChanged();
        return created;
    }

    public PaymentLedgerEntry RecordPayment(
        ThakaProjectListItemViewModel project,
        decimal amount,
        string paymentMethod,
        string reference,
        string recordedBy)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.IsActive)
        {
            throw new InvalidOperationException("Payments cannot be recorded against a settled Thaka project.");
        }
        var roundedAmount = Math.Round(amount, 2);
        if (roundedAmount <= 0m)
        {
            throw new InvalidOperationException("Payment amount must be greater than zero.");
        }

        if (roundedAmount > project.Balance)
        {
            throw new InvalidOperationException("Payment exceeds the current outstanding balance.");
        }

        var ledger = GetOrCreatePaymentLedger(project.Id);
        var entry = new PaymentLedgerEntry
        {
            ReceiptNumber = $"RCP-{ledger.Count + 1:D3}",
            Date = DateTime.Now,
            PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? "Cash" : paymentMethod.Trim(),
            Amount = roundedAmount,
            RecordedBy = string.IsNullOrWhiteSpace(recordedBy) ? "Abdullah" : recordedBy.Trim(),
            Reference = reference?.Trim() ?? string.Empty
        };

        ledger.Insert(0, entry);
        project.Paid = Math.Round(project.Paid + roundedAmount, 2);
        RaiseStateChanged();
        return entry;
    }
    public PaymentLedgerEntry SettleProject(
        ThakaProjectListItemViewModel project,
        decimal amount,
        string recordedBy)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!project.IsActive)
        {
            throw new InvalidOperationException("This Thaka project is already settled.");
        }

        var expected = Math.Round(project.Balance, 2);
        var roundedAmount = Math.Round(amount, 2);
        if (expected <= 0m || roundedAmount != expected)
        {
            throw new InvalidOperationException($"Final settlement must equal the exact remaining balance of Rs. {expected:N0}.");
        }

        var entry = RecordPayment(
            project,
            roundedAmount,
            "Final Settlement",
            "Final account settlement",
            recordedBy);

        project.Status = "SETTLED";
        RaiseStateChanged();
        return entry;
    }
    private static void ValidateMaterialIssue(
        ThakaProjectListItemViewModel project,
        PosProductItemViewModel product,
        decimal quantity)
    {
        if (!project.IsActive)
        {
            throw new InvalidOperationException("Material cannot be issued to a settled Thaka project.");
        }

        var roundedQuantity = Math.Round(quantity, 2);
        if (roundedQuantity <= 0m)
        {
            throw new InvalidOperationException("Material quantity must be greater than zero.");
        }

        if (roundedQuantity > product.Stock)
        {
            throw new InvalidOperationException(
                $"Only {product.Stock:0.##} {product.Unit} of {product.Name} are available.");
        }
    }

    private ObservableCollection<MaterialLedgerEntry> GetOrCreateMaterialLedger(string projectId)
    {
        if (!_materialLedgers.TryGetValue(projectId, out var ledger))
        {
            ledger = [];
            _materialLedgers[projectId] = ledger;
        }

        return ledger;
    }
    private ObservableCollection<PaymentLedgerEntry> GetOrCreatePaymentLedger(string projectId)
    {
        if (!_paymentLedgers.TryGetValue(projectId, out var ledger))
        {
            ledger = [];
            _paymentLedgers[projectId] = ledger;
        }

        return ledger;
    }

    private void RecordStockMutation(DemoStockMutationRecord mutation)
    {
        StockMutations.Insert(0, mutation);
        StockMutationRecorded?.Invoke(this, mutation);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static List<PosProductItemViewModel> CreateProducts() =>
    [
        new("P1", "LED Bulb 12W", "PH-LED-12W", "Philips", "Lighting", 82m, 500m, "82 in stock", BadgeTone.Success, "Pcs", 350m, 20m, "12W"),
        new("P2", "Switch 16A", "LG-SW-16A", "Legrand", "Switches", 124m, 300m, "124 in stock", BadgeTone.Success, "Pcs", 120m, 25m, "16A"),
        new("P3", "Breaker 32A", "SC-BR-32A", "Schneider", "Breakers", 4m, 1450m, "4 left", BadgeTone.Warning, "Pcs", 1100m, 10m, "32A"),
        new("P4", "Wire 2.5mm", "PC-WR-25", "Pak Cables", "Cables", 12m, 6500m, "12 in stock", BadgeTone.Success, "Roll", 5000m, 5m, "2.5mm"),
        new("P5", "Socket 16A", "MK-SK-16A", "MK", "Switches", 0m, 450m, "Out of Stock", BadgeTone.Danger, "Pcs", 300m, 10m, "16A"),
        new("P6", "MCB 20A", "HG-MCB-20A", "Hager", "Breakers", 30m, 850m, "30 in stock", BadgeTone.Success, "Pcs", 650m, 8m, "20A"),
        new("P7", "Fan Regulator", "HV-FR-01", "Havells", "Accessories", 18m, 750m, "18 in stock", BadgeTone.Success, "Pcs", 550m, 5m, "Standard"),
        new("P8", "CCTV Cable 3+1", "CS-CC-31", "Commscope", "Cables", 8m, 2800m, "8 left", BadgeTone.Warning, "Roll", 2200m, 5m, "3+1")
    ];

    private static ObservableCollection<ThakaProjectListItemViewModel> CreateProjects() =>
    [
        new("THK-001", "Ahmed House", "Haji Ahmed", "0300-1234567", "F-10/2 Islamabad",
            new DateTime(2026, 9, 5), 185000m, 50000m, "ACTIVE", "Main villa electrical conduit & panel wiring."),
        new("THK-002", "Ali Plaza", "Ali Raza", "0321-9876543", "Blue Area Islamabad",
            new DateTime(2026, 8, 12), 420000m, 40000m, "ACTIVE", "Commercial plaza 3rd floor wiring & switches."),
        new("THK-003", "Usman House", "Usman Farooq", "0333-5551234", "G-11/3 Islamabad",
            new DateTime(2026, 8, 28), 82400m, 10000m, "ACTIVE", "Residential renovation lighting and DB installation."),
        new("THK-004", "Tariq Complex", "Tariq Mehmood", "0301-4448899", "I-8 Markaz Islamabad",
            new DateTime(2026, 7, 15), 245000m, 245000m, "SETTLED", "All supplies reconciled and final invoice cleared.")
    ];

    private void SeedLedgers()
    {
        SeedAhmedHouse();
        SeedSimpleProject("THK-002", 420000m, 40000m, "Commercial electrical material");
        SeedSimpleProject("THK-003", 82400m, 10000m, "Residential electrical material");
        SeedSimpleProject("THK-004", 245000m, 245000m, "Settled project material");
    }
    private void SeedAhmedHouse()
    {
        var materials = GetOrCreateMaterialLedger("THK-001");
        materials.Add(new MaterialLedgerEntry { Date = new DateTime(2026, 9, 12), ChallanNumber = "CH-005", ProductName = "DB Box 12-Way", Quantity = 5m, Unit = "Pcs", Rate = 23000m, TotalValue = 115000m });
        materials.Add(new MaterialLedgerEntry { Date = new DateTime(2026, 9, 12), ChallanNumber = "CH-004", ProductName = "Switch Socket Plate", Quantity = 40m, Unit = "Pcs", Rate = 300m, TotalValue = 12000m });
        materials.Add(new MaterialLedgerEntry { Date = new DateTime(2026, 9, 10), ChallanNumber = "CH-003", ProductName = "MCB 32A Schneider", Quantity = 20m, Unit = "Pcs", Rate = 1450m, TotalValue = 29000m });
        materials.Add(new MaterialLedgerEntry { Date = new DateTime(2026, 9, 7), ChallanNumber = "CH-002", ProductName = "LED Bulb 12W", Quantity = 50m, Unit = "Pcs", Rate = 450m, TotalValue = 22500m });
        materials.Add(new MaterialLedgerEntry { Date = new DateTime(2026, 9, 5), ChallanNumber = "CH-001", ProductName = "Wire 2.5mm PVC", Quantity = 100m, Unit = "Meters", Rate = 65m, TotalValue = 6500m });

        var payments = GetOrCreatePaymentLedger("THK-001");
        payments.Add(new PaymentLedgerEntry { ReceiptNumber = "RCP-002", Date = new DateTime(2026, 9, 14), PaymentMethod = "Bank", Amount = 20000m, RecordedBy = "Abdullah", Reference = "JazzCash transfer" });
        payments.Add(new PaymentLedgerEntry { ReceiptNumber = "RCP-001", Date = new DateTime(2026, 9, 8), PaymentMethod = "Cash", Amount = 30000m, RecordedBy = "Abdullah", Reference = "Advance payment" });
    }
    private void SeedSimpleProject(string projectId, decimal materialValue, decimal paid, string materialName)
    {
        var materials = GetOrCreateMaterialLedger(projectId);
        materials.Add(new MaterialLedgerEntry
        {
            Date = DateTime.Today.AddDays(-7),
            ChallanNumber = $"CH-{projectId[^3..]}",
            ProductName = materialName,
            Quantity = 1m,
            Unit = "Lot",
            Rate = materialValue,
            TotalValue = materialValue
        });

        if (paid > 0m)
        {
            var payments = GetOrCreatePaymentLedger(projectId);
            payments.Add(new PaymentLedgerEntry
            {
                ReceiptNumber = "RCP-001",
                Date = DateTime.Today.AddDays(-5),
                PaymentMethod = "Cash",
                Amount = paid,
                RecordedBy = "Abdullah",
                Reference = "Opening project payment"
            });
        }
    }
}
