using System.Collections.ObjectModel;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Services;

public enum InventoryMovementKind
{
    OpeningBalance,
    PurchaseIn,
    PurchaseReturnOut,
    SaleOut,
    SaleReturnIn,
    ThakaOut,
    AdjustmentIn,
    AdjustmentOut,
    Damage
}

public sealed class InventoryMovementRecord
{
    public DateTime Timestamp { get; init; }
    public string ProductId { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public InventoryMovementKind Kind { get; init; }
    public decimal QuantityDelta { get; init; }
    public decimal BeforeQuantity { get; init; }
    public decimal AfterQuantity { get; init; }
    public string Reference { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string TypeDisplay => Kind switch
    {
        InventoryMovementKind.OpeningBalance => "Opening Balance",
        InventoryMovementKind.PurchaseIn => "Purchase",
        InventoryMovementKind.PurchaseReturnOut => "Purchase Return",
        InventoryMovementKind.SaleOut => "Sale",
        InventoryMovementKind.SaleReturnIn => "Sale Return",
        InventoryMovementKind.ThakaOut => "Thaka",
        InventoryMovementKind.AdjustmentIn => "Adjustment +",
        InventoryMovementKind.AdjustmentOut => "Adjustment -",
        _ => "Damage"
    };

    public string QuantityDisplay =>
        $"{(QuantityDelta > 0m ? "+" : string.Empty)}{QuantityDelta:0.##}";

    public string TimeDisplay => Timestamp.ToString("dd MMM · hh:mm tt");
}

public sealed class PurchaseItemRecord
{
    public required PosProductItemViewModel Product { get; init; }
    public decimal PurchasedQuantity { get; init; }
    public decimal UsedQuantity { get; set; }
    public decimal ReturnedQuantity { get; set; }
    public decimal Cost { get; init; }
    public decimal EffectiveUnitCost { get; init; }
    public decimal LandedUnitCost => EffectiveUnitCost > 0m ? EffectiveUnitCost : Cost;
    public decimal SalePrice { get; init; }
    public decimal EligibleReturnQuantity =>
        Math.Max(0m, Math.Min(
            PurchasedQuantity - UsedQuantity - ReturnedQuantity,
            Product.Stock));

    public decimal LineTotal => Math.Round(PurchasedQuantity * Cost, 2);
    public string ProductName => Product.Name;
    public string ProductMeta => $"{Product.Brand} · {Product.Sku}";
}

public sealed class PurchaseRecord
{
    public required string PurchaseNumber { get; init; }
    public required string Supplier { get; init; }
    public required string InvoiceNumber { get; init; }
    public DateTime Date { get; init; }
    public string Note { get; init; } = string.Empty;
    public decimal OtherCharges { get; init; }
    public required IReadOnlyList<PurchaseItemRecord> Items { get; init; }

    public decimal Subtotal => Items.Sum(item => item.LineTotal);
    public decimal Total => Subtotal + OtherCharges;
    public int ItemCount => Items.Count;
    public string DateDisplay => Date.ToString("dd MMM yyyy");
    public string TotalDisplay => $"Rs. {Total:N0}";
}

public sealed class PurchaseDraftLine
{
    public required PosProductItemViewModel Product { get; init; }
    public decimal Quantity { get; init; }
    public decimal Cost { get; init; }
    public decimal SalePrice { get; init; }
}

public sealed class PurchaseReturnLineRecord
{
    public required string ProductId { get; init; }
    public required string ProductName { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitCost { get; init; }
    public decimal ReturnValue => Math.Round(Quantity * UnitCost, 2);
}

public sealed class PurchaseReturnRecord
{
    public required string ReturnNumber { get; init; }
    public required string PurchaseNumber { get; init; }
    public DateTime Timestamp { get; init; }
    public required string Reason { get; init; }
    public string Note { get; init; } = string.Empty;
    public required IReadOnlyList<PurchaseReturnLineRecord> Items { get; init; }
    public decimal TotalValue => Items.Sum(item => item.ReturnValue);
}

public sealed class InventoryStockTruthResult
{
    public required string ProductId { get; init; }
    public required string ProductName { get; init; }
    public bool HasOpeningBalance { get; init; }
    public decimal BaselineQuantity { get; init; }
    public decimal MovementDelta { get; init; }
    public decimal ProjectedQuantity { get; init; }
    public decimal ActualQuantity { get; init; }
    public decimal Difference => Math.Round(ActualQuantity - ProjectedQuantity, 2);
    public bool IsReconciled => HasOpeningBalance && Difference == 0m;
}

public sealed class DemoPurchaseInventoryService
{
    private static readonly Lazy<DemoPurchaseInventoryService> s_instance =
        new(() => new DemoPurchaseInventoryService());

    private readonly DemoRetailState _retailState = DemoRetailState.Instance;
    private int _purchaseSequence = 253;
    private int _purchaseReturnSequence = 2;

    public static DemoPurchaseInventoryService Instance => s_instance.Value;

    public event EventHandler? StateChanged;

    public ObservableCollection<PurchaseRecord> Purchases { get; } = [];
    public ObservableCollection<PurchaseReturnRecord> PurchaseReturns { get; } = [];
    public ObservableCollection<InventoryMovementRecord> Movements { get; } = [];

    public ObservableCollection<string> Suppliers { get; } =
        ["ABC Electrical", "XYZ Traders", "City Electric Store", "Pak Wholesale"];

    private DemoPurchaseInventoryService()
    {
        SeedPurchases();
        SeedPurchaseReturns();
        SeedMovements();

        foreach (var mutation in _retailState.StockMutations.OrderBy(m => m.Timestamp))
        {
            AddRetailMutation(mutation);
        }

        SeedOpeningBalances();
        _retailState.StockMutationRecorded += OnRetailStockMutationRecorded;
        _retailState.StateChanged += OnRetailStateChanged;
    }
    public void RegisterSupplier(string supplierName)
    {
        var normalized = supplierName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Supplier name is required.");
        }

        if (Suppliers.Any(existing =>
            string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Suppliers.Add(normalized);
        RaiseStateChanged();
    }

    public PurchaseRecord SavePurchase(
        string supplier,
        string invoiceNumber,
        DateTime date,
        string note,
        decimal otherCharges,
        IEnumerable<PurchaseDraftLine> lines)
    {
        var prepared = lines.ToArray();
        if (string.IsNullOrWhiteSpace(supplier))
        {
            throw new InvalidOperationException("Supplier is required.");
        }

        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            throw new InvalidOperationException("Supplier invoice number is required.");
        }

        if (prepared.Length == 0)
        {
            throw new InvalidOperationException("Purchase requires at least one product.");
        }

        if (otherCharges < 0m)
        {
            throw new InvalidOperationException("Other charges cannot be negative.");
        }

        if (prepared.Any(line => line.Quantity <= 0m || line.Cost < 0m || line.SalePrice < 0m))
        {
            throw new InvalidOperationException("Purchase quantities and prices must be valid.");
        }

        var purchaseNumber = $"P-{_purchaseSequence++:D4}";
        var items = new List<PurchaseItemRecord>(prepared.Length);
        var baseSubtotal = prepared.Sum(line =>
            Math.Round(line.Quantity, 2) * Math.Round(line.Cost, 2));
        var roundedOtherCharges = Math.Round(otherCharges, 2);

        if (roundedOtherCharges > 0m && baseSubtotal <= 0m)
        {
            throw new InvalidOperationException(
                "Other charges require a positive purchase subtotal for landed-cost allocation.");
        }

        var remainingOtherCharges = roundedOtherCharges;

        for (var index = 0; index < prepared.Length; index++)
        {
            var line = prepared[index];
            var quantity = Math.Round(line.Quantity, 2);
            var baseUnitCost = Math.Round(line.Cost, 2);
            var baseLineTotal = Math.Round(quantity * baseUnitCost, 2);
            var allocatedOtherCost = roundedOtherCharges == 0m
                ? 0m
                : index == prepared.Length - 1
                    ? remainingOtherCharges
                    : Math.Round(
                        roundedOtherCharges * (baseLineTotal / baseSubtotal),
                        2);
            remainingOtherCharges = Math.Round(
                remainingOtherCharges - allocatedOtherCost,
                2);

            var effectiveLineCost = Math.Round(
                baseLineTotal + allocatedOtherCost,
                2);
            var effectiveUnitCost = quantity > 0m
                ? Math.Round(effectiveLineCost / quantity, 4)
                : 0m;

            var before = line.Product.Stock;
            var after = Math.Round(before + quantity, 2);
            var previousInventoryCost = Math.Round(
                before * line.Product.Cost,
                4);
            var movingWeightedAverage = after > 0m
                ? Math.Round(
                    (previousInventoryCost + effectiveLineCost) / after,
                    4)
                : effectiveUnitCost;

            line.Product.Stock = after;
            line.Product.Cost = movingWeightedAverage;
            line.Product.Price = Math.Round(line.SalePrice, 2);

            var item = new PurchaseItemRecord
            {
                Product = line.Product,
                PurchasedQuantity = quantity,
                Cost = baseUnitCost,
                EffectiveUnitCost = effectiveUnitCost,
                SalePrice = line.Product.Price
            };
            items.Add(item);

            Movements.Insert(0, new InventoryMovementRecord
            {
                Timestamp = DateTime.Now,
                ProductId = line.Product.Id,
                ProductName = line.Product.Name,
                Kind = InventoryMovementKind.PurchaseIn,
                QuantityDelta = quantity,
                BeforeQuantity = before,
                AfterQuantity = line.Product.Stock,
                Reference = purchaseNumber,
                Reason = "Purchase intake · landed cost allocated · moving weighted average updated"
            });
        }

        var record = new PurchaseRecord
        {
            PurchaseNumber = purchaseNumber,
            Supplier = supplier.Trim(),
            InvoiceNumber = invoiceNumber.Trim(),
            Date = date,
            Note = note?.Trim() ?? string.Empty,
            OtherCharges = Math.Round(otherCharges, 2),
            Items = items
        };

        Purchases.Insert(0, record);
        RaiseStateChanged();
        return record;
    }

    public decimal ReturnPurchase(
        PurchaseRecord purchase,
        IReadOnlyDictionary<string, decimal> returnQuantities,
        string reason,
        string note)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("Purchase return reason is required.");
        }

        var requested = purchase.Items
            .Select(item => (Item: item,
                Quantity: returnQuantities.TryGetValue(item.Product.Id, out var value)
                    ? Math.Round(value, 2) : 0m))
            .Where(x => x.Quantity > 0m)
            .ToArray();

        if (requested.Length == 0)
        {
            throw new InvalidOperationException("Select at least one quantity to return.");
        }

        foreach (var (Item, Quantity) in requested)
        {
            if (Quantity > Item.EligibleReturnQuantity)
            {
                throw new InvalidOperationException(
                    $"Return quantity for {Item.ProductName} exceeds eligible quantity.");
            }

            if (Quantity > Item.Product.Stock)
            {
                throw new InvalidOperationException(
                    $"Only {Item.Product.Stock:0.##} sellable units are currently available.");
            }
        }

        var returnNumber = $"PR-{_purchaseReturnSequence++:D4}";
        var timestamp = DateTime.Now;
        var returnItems = new List<PurchaseReturnLineRecord>(requested.Length);
        decimal total = 0m;

        foreach (var (Item, Quantity) in requested)
        {
            var product = Item.Product;
            var before = product.Stock;
            product.Stock = Math.Round(before - Quantity, 2);
            Item.ReturnedQuantity =
                Math.Round(Item.ReturnedQuantity + Quantity, 2);
            total += Quantity * Item.Cost;
            returnItems.Add(new PurchaseReturnLineRecord
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Quantity = Quantity,
                UnitCost = Item.Cost
            });

            Movements.Insert(0, new InventoryMovementRecord
            {
                Timestamp = timestamp,
                ProductId = product.Id,
                ProductName = product.Name,
                Kind = InventoryMovementKind.PurchaseReturnOut,
                QuantityDelta = -Quantity,
                BeforeQuantity = before,
                AfterQuantity = product.Stock,
                Reference = returnNumber,
                Reason = $"{purchase.PurchaseNumber} · {reason}{(string.IsNullOrWhiteSpace(note) ? string.Empty : $" · {note.Trim()}")}"
            });
        }

        PurchaseReturns.Insert(0, new PurchaseReturnRecord
        {
            ReturnNumber = returnNumber,
            PurchaseNumber = purchase.PurchaseNumber,
            Timestamp = timestamp,
            Reason = reason.Trim(),
            Note = note?.Trim() ?? string.Empty,
            Items = returnItems
        });

        RaiseStateChanged();
        return Math.Round(total, 2);
    }

    public InventoryMovementRecord AdjustStock(
        PosProductItemViewModel product,
        decimal delta,
        string reason,
        string note)
    {
        ArgumentNullException.ThrowIfNull(product);
        delta = Math.Round(delta, 2);
        if (delta == 0m)
        {
            throw new InvalidOperationException("Adjustment quantity cannot be zero.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("Adjustment reason is required.");
        }

        var before = product.Stock;
        var after = Math.Round(before + delta, 2);
        if (after < 0m)
        {
            throw new InvalidOperationException("Adjustment cannot make sellable stock negative.");
        }

        product.Stock = after;
        var movement = new InventoryMovementRecord
        {
            Timestamp = DateTime.Now,
            ProductId = product.Id,
            ProductName = product.Name,
            Kind = delta > 0m ? InventoryMovementKind.AdjustmentIn : InventoryMovementKind.AdjustmentOut,
            QuantityDelta = delta,
            BeforeQuantity = before,
            AfterQuantity = after,
            Reference = "ADJ",
            Reason = $"{reason}{(string.IsNullOrWhiteSpace(note) ? string.Empty : $" · {note.Trim()}")}"
        };

        Movements.Insert(0, movement);
        RaiseStateChanged();
        return movement;
    }

    public IReadOnlyList<InventoryMovementRecord> GetMovements(string productId) =>
        [.. Movements.Where(m => string.Equals(m.ProductId, productId, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.Timestamp)];

    public IReadOnlyList<PurchaseReturnRecord> GetPurchaseReturns(string purchaseNumber) =>
        [.. PurchaseReturns
            .Where(item => string.Equals(item.PurchaseNumber, purchaseNumber, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Timestamp)];

    public IReadOnlyList<InventoryStockTruthResult> AuditStockTruth() =>
        [.. _retailState.Products.Select(AuditProductStock)];

    private InventoryStockTruthResult AuditProductStock(PosProductItemViewModel product)
    {
        var productMovements = Movements
            .Where(movement => string.Equals(movement.ProductId, product.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var baselineIndex = productMovements.FindIndex(
            movement => movement.Kind == InventoryMovementKind.OpeningBalance);

        if (baselineIndex < 0)
        {
            return new InventoryStockTruthResult
            {
                ProductId = product.Id,
                ProductName = product.Name,
                HasOpeningBalance = false,
                ActualQuantity = product.Stock
            };
        }

        var baseline = productMovements[baselineIndex];
        var delta = productMovements.Take(baselineIndex).Sum(movement => movement.QuantityDelta);
        var projected = Math.Round(baseline.AfterQuantity + delta, 2);

        return new InventoryStockTruthResult
        {
            ProductId = product.Id,
            ProductName = product.Name,
            HasOpeningBalance = true,
            BaselineQuantity = baseline.AfterQuantity,
            MovementDelta = Math.Round(delta, 2),
            ProjectedQuantity = projected,
            ActualQuantity = product.Stock
        };
    }

    private void OnRetailStockMutationRecorded(object? _, DemoStockMutationRecord mutation)
    {
        AddRetailMutation(mutation);
        RaiseStateChanged();
    }

    private void OnRetailStateChanged(object? _, EventArgs __)
    {
        if (EnsureOpeningBalancesForNewProducts())
        {
            RaiseStateChanged();
        }
    }

    private void AddRetailMutation(DemoStockMutationRecord mutation)
    {
        var kind = mutation.Kind switch
        {
            DemoStockMutationKind.SaleOut => InventoryMovementKind.SaleOut,
            DemoStockMutationKind.SaleReturnIn => InventoryMovementKind.SaleReturnIn,
            DemoStockMutationKind.ThakaOut => InventoryMovementKind.ThakaOut,
            _ => InventoryMovementKind.Damage
        };

        Movements.Insert(0, new InventoryMovementRecord
        {
            Timestamp = mutation.Timestamp,
            ProductId = mutation.ProductId,
            ProductName = mutation.ProductName,
            Kind = kind,
            QuantityDelta = mutation.QuantityDelta,
            BeforeQuantity = mutation.BeforeQuantity,
            AfterQuantity = mutation.AfterQuantity,
            Reference = mutation.Reference,
            Reason = mutation.Reason
        });
    }

    private void SeedOpeningBalances()
    {
        EnsureOpeningBalancesForNewProducts();
    }

    private bool EnsureOpeningBalancesForNewProducts()
    {
        var added = false;
        foreach (var product in _retailState.Products)
        {
            var hasBaseline = Movements.Any(movement =>
                movement.Kind == InventoryMovementKind.OpeningBalance &&
                string.Equals(movement.ProductId, product.Id, StringComparison.OrdinalIgnoreCase));
            if (hasBaseline)
            {
                continue;
            }

            Movements.Insert(0, new InventoryMovementRecord
            {
                Timestamp = DateTime.Now,
                ProductId = product.Id,
                ProductName = product.Name,
                Kind = InventoryMovementKind.OpeningBalance,
                QuantityDelta = product.Stock,
                BeforeQuantity = 0m,
                AfterQuantity = product.Stock,
                Reference = "OPENING-S4",
                Reason = "Canonical Sprint 4 stock baseline"
            });
            added = true;
        }

        return added;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void SeedPurchases()
    {
        var products = _retailState.Products.ToDictionary(p => p.Id);
        Purchases.Add(new PurchaseRecord
        {
            PurchaseNumber = "P-0252",
            Supplier = "ABC Electrical",
            InvoiceNumber = "INV-4589",
            Date = new DateTime(2026, 9, 17),
            OtherCharges = 2500m,
            Items =
            [
                new PurchaseItemRecord { Product = products["P1"], PurchasedQuantity = 100m, UsedQuantity = 48m, Cost = 350m, SalePrice = 500m },
                new PurchaseItemRecord { Product = products["P2"], PurchasedQuantity = 200m, UsedQuantity = 180m, Cost = 120m, SalePrice = 300m },
                new PurchaseItemRecord { Product = products["P4"], PurchasedQuantity = 20m, UsedQuantity = 8m, ReturnedQuantity = 2m, Cost = 5000m, SalePrice = 6500m }
            ]
        });

        Purchases.Add(new PurchaseRecord
        {
            PurchaseNumber = "P-0251",
            Supplier = "XYZ Traders",
            InvoiceNumber = "INV-4520",
            Date = new DateTime(2026, 9, 16),
            Items =
            [
                new PurchaseItemRecord { Product = products["P6"], PurchasedQuantity = 40m, UsedQuantity = 10m, Cost = 650m, SalePrice = 850m },
                new PurchaseItemRecord { Product = products["P7"], PurchasedQuantity = 20m, UsedQuantity = 2m, Cost = 550m, SalePrice = 750m }
            ]
        });
    }

    private void SeedPurchaseReturns()
    {
        var purchase = Purchases.First(item =>
            string.Equals(item.PurchaseNumber, "P-0252", StringComparison.OrdinalIgnoreCase));
        var wire = purchase.Items.First(item =>
            string.Equals(item.Product.Id, "P4", StringComparison.OrdinalIgnoreCase));

        PurchaseReturns.Add(new PurchaseReturnRecord
        {
            ReturnNumber = "PR-0001",
            PurchaseNumber = purchase.PurchaseNumber,
            Timestamp = new DateTime(2026, 9, 18, 11, 30, 0),
            Reason = "Supplier Return",
            Note = "Seeded historical return",
            Items =
            [
                new PurchaseReturnLineRecord
                {
                    ProductId = wire.Product.Id,
                    ProductName = wire.Product.Name,
                    Quantity = wire.ReturnedQuantity,
                    UnitCost = wire.Cost
                }
            ]
        });
    }

    private void SeedMovements()
    {
        var products = _retailState.Products.ToDictionary(p => p.Id);
        Movements.Add(new InventoryMovementRecord
        {
            Timestamp = DateTime.Today.AddHours(16).AddMinutes(20),
            ProductId = "P1",
            ProductName = products["P1"].Name,
            Kind = InventoryMovementKind.SaleOut,
            QuantityDelta = -2m,
            BeforeQuantity = 84m,
            AfterQuantity = 82m,
            Reference = "#1292"
        });
        Movements.Add(new InventoryMovementRecord
        {
            Timestamp = DateTime.Today.AddHours(15).AddMinutes(10),
            ProductId = "P2",
            ProductName = products["P2"].Name,
            Kind = InventoryMovementKind.ThakaOut,
            QuantityDelta = -4m,
            BeforeQuantity = 128m,
            AfterQuantity = 124m,
            Reference = "THK-001"
        });
        Movements.Add(new InventoryMovementRecord
        {
            Timestamp = DateTime.Today.AddHours(13).AddMinutes(30),
            ProductId = "P4",
            ProductName = products["P4"].Name,
            Kind = InventoryMovementKind.PurchaseIn,
            QuantityDelta = 20m,
            BeforeQuantity = 0m,
            AfterQuantity = 20m,
            Reference = "P-0252"
        });
    }
}
