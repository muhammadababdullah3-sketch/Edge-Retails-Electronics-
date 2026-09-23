using System.Collections.ObjectModel;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Services;

public sealed class ExpenseRecord : ViewModelBase
{
    private string _category = string.Empty;
    private string _subcategory = string.Empty;
    private decimal _amount;
    private DateTime _date;
    private string _paymentMethod = string.Empty;
    private string _staffMember = string.Empty;
    private string _note = string.Empty;

    public required string Id { get; init; }

    public Guid? BackendId { get; init; }

    public string Category
    {
        get => _category;
        set => SetProperty(ref _category, value?.Trim() ?? string.Empty);
    }

    public string Subcategory
    {
        get => _subcategory;
        set => SetProperty(ref _subcategory, value?.Trim() ?? string.Empty);
    }

    public decimal Amount
    {
        get => _amount;
        set
        {
            if (SetProperty(ref _amount, Math.Max(0m, Math.Round(value, 2))))
            {
                OnPropertyChanged(nameof(AmountDisplay));
            }
        }
    }

    public DateTime Date
    {
        get => _date;
        set
        {
            if (SetProperty(ref _date, value.Date))
            {
                OnPropertyChanged(nameof(DateDisplay));
            }
        }
    }

    public string PaymentMethod
    {
        get => _paymentMethod;
        set => SetProperty(ref _paymentMethod, value?.Trim() ?? string.Empty);
    }

    public string StaffMember
    {
        get => _staffMember;
        set => SetProperty(ref _staffMember, value?.Trim() ?? string.Empty);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value?.Trim() ?? string.Empty);
    }

    public string DateDisplay => Date.ToString("dd MMM yyyy");
    public string AmountDisplay => $"Rs. {Amount:N0}";
}

public sealed class CustomerDirectoryRecord : ViewModelBase
{
    private string _name = string.Empty;
    private string _phone = string.Empty;
    private string _address = string.Empty;
    private string _notes = string.Empty;
    private decimal _localSales;
    private string _activeThaka = "—";
    private DateTime? _lastSale;

    public required string Id { get; init; }

    public Guid? BackendId { get; init; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value?.Trim() ?? string.Empty);
    }

    public string Phone
    {
        get => _phone;
        set => SetProperty(ref _phone, value?.Trim() ?? string.Empty);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value?.Trim() ?? string.Empty);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value?.Trim() ?? string.Empty);
    }

    public decimal LocalSales
    {
        get => _localSales;
        internal set
        {
            if (SetProperty(ref _localSales, Math.Max(0m, Math.Round(value, 2))))
            {
                OnPropertyChanged(nameof(LocalSalesDisplay));
            }
        }
    }

    public string ActiveThaka
    {
        get => _activeThaka;
        internal set => SetProperty(ref _activeThaka, string.IsNullOrWhiteSpace(value) ? "—" : value);
    }

    public DateTime? LastSale
    {
        get => _lastSale;
        internal set
        {
            if (SetProperty(ref _lastSale, value))
            {
                OnPropertyChanged(nameof(LastSaleDisplay));
            }
        }
    }

    public string LocalSalesDisplay => $"Rs. {LocalSales:N0}";
    public string LastSaleDisplay =>
        LastSale is null ? "—" :
        LastSale.Value.Date == DateTime.Today ? "Today" :
        LastSale.Value.ToString("dd MMM");
}

public sealed class SupplierDirectoryRecord : ViewModelBase
{
    private string _name = string.Empty;
    private string _phone = string.Empty;
    private string _city = string.Empty;
    private string _address = string.Empty;
    private string _notes = string.Empty;
    private decimal _totalPurchases;
    private DateTime? _lastPurchase;

    public required string Id { get; init; }

    public Guid? BackendId { get; init; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value?.Trim() ?? string.Empty);
    }

    public string Phone
    {
        get => _phone;
        set => SetProperty(ref _phone, value?.Trim() ?? string.Empty);
    }

    public string City
    {
        get => _city;
        set => SetProperty(ref _city, value?.Trim() ?? string.Empty);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value?.Trim() ?? string.Empty);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value?.Trim() ?? string.Empty);
    }

    public decimal TotalPurchases
    {
        get => _totalPurchases;
        internal set
        {
            if (SetProperty(ref _totalPurchases, Math.Max(0m, Math.Round(value, 2))))
            {
                OnPropertyChanged(nameof(TotalPurchasesDisplay));
            }
        }
    }

    public DateTime? LastPurchase
    {
        get => _lastPurchase;
        internal set
        {
            if (SetProperty(ref _lastPurchase, value))
            {
                OnPropertyChanged(nameof(LastPurchaseDisplay));
            }
        }
    }

    public string TotalPurchasesDisplay => $"Rs. {TotalPurchases:N0}";
    public string LastPurchaseDisplay => LastPurchase?.ToString("dd MMM") ?? "—";
}
public sealed class DemoBusinessDirectoryService
{
    private static readonly Lazy<DemoBusinessDirectoryService> s_instance =
        new(() => new DemoBusinessDirectoryService());

    private readonly DemoTransactionService _transactionService = DemoTransactionService.Instance;
    private readonly DemoRetailState _retailState = DemoRetailState.Instance;
    private readonly DemoPurchaseInventoryService _purchaseService = DemoPurchaseInventoryService.Instance;
    private int _expenseSequence = 6;
    private int _customerSequence = 5;
    private int _supplierSequence = 5;

    public static DemoBusinessDirectoryService Instance => s_instance.Value;

    public event EventHandler? StateChanged;

    public ObservableCollection<ExpenseRecord> Expenses { get; } = [];
    public ObservableCollection<CustomerDirectoryRecord> Customers { get; } = [];
    public ObservableCollection<SupplierDirectoryRecord> Suppliers { get; } = [];

    public IReadOnlyList<string> ExpenseCategories { get; } =
        ["Staff Expense", "Utilities", "Rent", "Transport", "Maintenance", "Miscellaneous"];

    public IReadOnlyList<string> PaymentMethods { get; } =
        ["Cash", "Bank", "Other"];

    private DemoBusinessDirectoryService()
    {
        SeedExpenses();
        SeedCustomers();
        SeedSuppliers();
        RefreshCustomerMetrics();
        RefreshSupplierMetrics();

        _transactionService.TransactionRecorded += OnTransactionRecorded;
        _transactionService.ReturnRecorded += OnReturnRecorded;
        _retailState.StateChanged += OnRetailStateChanged;
        _purchaseService.StateChanged += OnPurchaseStateChanged;
    }

    public ExpenseRecord SaveExpense(
        ExpenseRecord? existing,
        string category,
        string subcategory,
        decimal amount,
        DateTime date,
        string paymentMethod,
        string staffMember,
        string note)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            throw new InvalidOperationException("Expense category is required.");
        }

        if (string.IsNullOrWhiteSpace(subcategory))
        {
            throw new InvalidOperationException("Expense subcategory is required.");
        }

        if (amount <= 0m)
        {
            throw new InvalidOperationException("Expense amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            throw new InvalidOperationException("Payment method is required.");
        }

        var record = existing ?? new ExpenseRecord { Id = $"EXP-{_expenseSequence++:D4}" };
        record.Category = category;
        record.Subcategory = subcategory;
        record.Amount = amount;
        record.Date = date;
        record.PaymentMethod = paymentMethod;
        record.StaffMember = staffMember;
        record.Note = note;

        if (existing is null)
        {
            Expenses.Insert(0, record);
        }

        RaiseStateChanged();
        return record;
    }

    public CustomerDirectoryRecord SaveCustomer(
        CustomerDirectoryRecord? existing,
        string name,
        string phone,
        string address,
        string notes)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        var normalizedPhone = phone?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Customer name is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            throw new InvalidOperationException("Customer phone is required.");
        }

        var phoneKey = NormalizePhone(normalizedPhone);
        if (Customers.Any(customer =>
            !ReferenceEquals(customer, existing) &&
            NormalizePhone(customer.Phone) == phoneKey))
        {
            throw new InvalidOperationException("Another customer already uses this phone number.");
        }

        var record = existing ?? new CustomerDirectoryRecord { Id = $"CUS-{_customerSequence++:D4}" };
        record.Name = normalizedName;
        record.Phone = normalizedPhone;
        record.Address = address;
        record.Notes = notes;

        if (existing is null)
        {
            Customers.Insert(0, record);
        }

        RefreshCustomerMetrics(record);
        RaiseStateChanged();
        return record;
    }

    public SupplierDirectoryRecord SaveSupplier(
        SupplierDirectoryRecord? existing,
        string name,
        string phone,
        string city,
        string address,
        string notes)
    {
        var normalizedName = name?.Trim() ?? string.Empty;
        var normalizedPhone = phone?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("Supplier name is required.");
        }

        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            throw new InvalidOperationException("Supplier phone is required.");
        }

        if (Suppliers.Any(supplier =>
            !ReferenceEquals(supplier, existing) &&
            string.Equals(supplier.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Another supplier already uses this name.");
        }

        var record = existing ?? new SupplierDirectoryRecord { Id = $"SUP-{_supplierSequence++:D4}" };
        record.Name = normalizedName;
        record.Phone = normalizedPhone;
        record.City = city;
        record.Address = address;
        record.Notes = notes;

        if (existing is null)
        {
            Suppliers.Insert(0, record);
        }

        _purchaseService.RegisterSupplier(normalizedName);
        RefreshSupplierMetrics(record);
        RaiseStateChanged();
        return record;
    }

    public void RefreshMetrics()
    {
        RefreshCustomerMetrics();
        RefreshSupplierMetrics();
        RaiseStateChanged();
    }
    private void RefreshCustomerMetrics()
    {
        foreach (var customer in Customers)
        {
            RefreshCustomerMetrics(customer);
        }
    }

    private void RefreshCustomerMetrics(CustomerDirectoryRecord customer)
    {
        var transactions = _transactionService.GetAllTransactions()
            .Where(transaction => MatchesCustomer(
                customer,
                transaction.CustomerName,
                transaction.CustomerPhone))
            .ToArray();

        var invoiceNumbers = transactions
            .Select(transaction => transaction.InvoiceNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var refunds = _transactionService.GetAllReturns()
            .Where(returnRecord => invoiceNumbers.Contains(returnRecord.InvoiceNumber))
            .Sum(returnRecord => returnRecord.TotalRefundAmount);

        customer.LocalSales = Math.Max(
            0m,
            Math.Round(
                transactions.Sum(transaction => transaction.TotalAmount) - refunds,
                2));

        customer.LastSale = transactions
            .OrderByDescending(transaction => transaction.Timestamp)
            .Select(transaction => (DateTime?)transaction.Timestamp)
            .FirstOrDefault();

        customer.ActiveThaka = _retailState.ThakaProjects
            .Where(project => project.IsActive)
            .Where(project => MatchesCustomer(customer, project.CustomerName, project.Phone))
            .OrderByDescending(project => project.StartDate)
            .Select(project => project.ProjectName)
            .FirstOrDefault() ?? "—";
    }

    private void RefreshSupplierMetrics()
    {
        foreach (var supplier in Suppliers)
        {
            RefreshSupplierMetrics(supplier);
        }
    }

    private void RefreshSupplierMetrics(SupplierDirectoryRecord supplier)
    {
        var purchases = _purchaseService.Purchases
            .Where(purchase =>
                string.Equals(purchase.Supplier, supplier.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var purchaseNumbers = purchases
            .Select(purchase => purchase.PurchaseNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var returnedValue = _purchaseService.PurchaseReturns
            .Where(returnRecord => purchaseNumbers.Contains(returnRecord.PurchaseNumber))
            .Sum(returnRecord => returnRecord.TotalValue);

        supplier.TotalPurchases = Math.Max(
            0m,
            Math.Round(
                purchases.Sum(purchase => purchase.Total) - returnedValue,
                2));

        supplier.LastPurchase = purchases
            .OrderByDescending(purchase => purchase.Date)
            .Select(purchase => (DateTime?)purchase.Date)
            .FirstOrDefault();
    }

    private static bool MatchesCustomer(
        CustomerDirectoryRecord customer,
        string candidateName,
        string candidatePhone)
    {
        var customerPhone = NormalizePhone(customer.Phone);
        var candidatePhoneKey = NormalizePhone(candidatePhone);
        if (!string.IsNullOrWhiteSpace(customerPhone) &&
            !string.IsNullOrWhiteSpace(candidatePhoneKey) &&
            string.Equals(customerPhone, candidatePhoneKey, StringComparison.Ordinal))
        {
            return true;
        }

        var customerName = customer.Name.Trim();
        var otherName = candidateName?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(customerName) &&
            !string.IsNullOrWhiteSpace(otherName) &&
            (otherName.Contains(customerName, StringComparison.OrdinalIgnoreCase) ||
             customerName.Contains(otherName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePhone(string? value) =>
        new([.. (value ?? string.Empty).Where(char.IsDigit)]);

    private void OnTransactionRecorded(object? sender, SaleTransactionRecord record)
    {
        RefreshCustomerMetrics();
        RaiseStateChanged();
    }

    private void OnReturnRecorded(object? sender, SaleReturnRecord record)
    {
        RefreshCustomerMetrics();
        RaiseStateChanged();
    }

    private void OnRetailStateChanged(object? sender, EventArgs e)
    {
        RefreshCustomerMetrics();
        RaiseStateChanged();
    }

    private void OnPurchaseStateChanged(object? sender, EventArgs e)
    {
        RefreshSupplierMetrics();
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void SeedExpenses()
    {
        Expenses.Add(new ExpenseRecord
        {
            Id = "EXP-0001",
            Category = "Staff Expense",
            Subcategory = "Lunch",
            Amount = 2500m,
            Date = DateTime.Today,
            PaymentMethod = "Cash",
            StaffMember = "Abdullah",
            Note = "Staff Lunch"
        });
        Expenses.Add(new ExpenseRecord
        {
            Id = "EXP-0002",
            Category = "Staff Expense",
            Subcategory = "Tea",
            Amount = 700m,
            Date = DateTime.Today,
            PaymentMethod = "Cash",
            StaffMember = "Abdullah",
            Note = "Evening Tea"
        });
        Expenses.Add(new ExpenseRecord
        {
            Id = "EXP-0003",
            Category = "Utilities",
            Subcategory = "Electricity",
            Amount = 8500m,
            Date = DateTime.Today,
            PaymentMethod = "Bank",
            Note = "Shop electricity bill"
        });
        Expenses.Add(new ExpenseRecord
        {
            Id = "EXP-0004",
            Category = "Maintenance",
            Subcategory = "Repair",
            Amount = 2000m,
            Date = DateTime.Today.AddDays(-2),
            PaymentMethod = "Cash",
            Note = "Counter repair"
        });
        Expenses.Add(new ExpenseRecord
        {
            Id = "EXP-0005",
            Category = "Transport",
            Subcategory = "Delivery",
            Amount = 1800m,
            Date = DateTime.Today.AddDays(-8),
            PaymentMethod = "Cash",
            Note = "Supplier pickup"
        });
    }
    private void SeedCustomers()
    {
        Customers.Add(new CustomerDirectoryRecord
        {
            Id = "CUS-0001",
            Name = "Ahmed",
            Phone = "0300-1234567",
            Address = "Model Town, Lahore",
            Notes = "Regular counter and project customer"
        });
        Customers.Add(new CustomerDirectoryRecord
        {
            Id = "CUS-0002",
            Name = "Usman",
            Phone = "0333-5551234",
            Address = "G-11/3 Islamabad",
            Notes = "Residential project customer"
        });
        Customers.Add(new CustomerDirectoryRecord
        {
            Id = "CUS-0003",
            Name = "Ali",
            Phone = "0321-9876543",
            Address = "Blue Area Islamabad",
            Notes = "Commercial project customer"
        });
        Customers.Add(new CustomerDirectoryRecord
        {
            Id = "CUS-0004",
            Name = "Raza Khan",
            Phone = "0321-1111222",
            Address = "Lahore",
            Notes = string.Empty
        });
    }

    private void SeedSuppliers()
    {
        Suppliers.Add(new SupplierDirectoryRecord
        {
            Id = "SUP-0001",
            Name = "ABC Electrical",
            Phone = "0300-1234567",
            City = "Lahore",
            Address = "Hall Road, Lahore",
            Notes = "Primary electrical wholesaler"
        });
        Suppliers.Add(new SupplierDirectoryRecord
        {
            Id = "SUP-0002",
            Name = "XYZ Traders",
            Phone = "0312-9876543",
            City = "Multan",
            Address = "Multan",
            Notes = string.Empty
        });
        Suppliers.Add(new SupplierDirectoryRecord
        {
            Id = "SUP-0003",
            Name = "Prime Cables",
            Phone = "0333-1112223",
            City = "Karachi",
            Address = "Karachi",
            Notes = "Cable supplier"
        });
        Suppliers.Add(new SupplierDirectoryRecord
        {
            Id = "SUP-0004",
            Name = "City Electric Store",
            Phone = "0301-5558899",
            City = "Lahore",
            Address = "Lahore",
            Notes = string.Empty
        });

        foreach (var supplier in Suppliers)
        {
            _purchaseService.RegisterSupplier(supplier.Name);
        }
    }
}
