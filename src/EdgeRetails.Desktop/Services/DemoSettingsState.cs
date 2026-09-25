using System.Collections.ObjectModel;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Services;

public sealed class SettingsUserRecord(string name, string role, bool isActive = true) : ViewModelBase
{
    private string _name = name;
    private string _role = role;
    private bool _isActive = isActive;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value.Trim());
    }

    public string Role
    {
        get => _role;
        set => SetProperty(ref _role, value.Trim());
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public string Status => IsActive ? "Active" : "Inactive";
}

public sealed class SettingsCategoryRecord(string name, bool isActive = true, Guid? id = null) : ViewModelBase
{
    public Guid? Id { get; } = id;
    private string _name = name;
    private bool _isActive = isActive;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value.Trim());
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(ToggleActionText));
            }
        }
    }

    public string Status => IsActive ? "Active" : "Inactive";
    public string ToggleActionText => IsActive ? "Deactivate" : "Activate";
    public int ProductCount => DemoRetailState.Instance.Products.Count(product =>
        string.Equals(product.Category, Name, StringComparison.OrdinalIgnoreCase));

    public void RefreshCount() => OnPropertyChanged(nameof(ProductCount));
}

public sealed class SettingsUnitRecord(string name, string symbol, bool isActive = true, Guid? id = null) : ViewModelBase
{
    public Guid? Id { get; } = id;
    private string _name = name;
    private string _symbol = symbol;
    private bool _isActive = isActive;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value.Trim());
    }

    public string Symbol
    {
        get => _symbol;
        set => SetProperty(ref _symbol, value.Trim());
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(ToggleActionText));
            }
        }
    }

    public string Status => IsActive ? "Active" : "Inactive";
    public string ToggleActionText => IsActive ? "Deactivate" : "Activate";
}

public sealed class SettingsBackupRecord
{
    public required DateTime Timestamp { get; init; }
    public required string Type { get; init; }
    public required string Status { get; init; }
    public required string Size { get; init; }
    public string TimestampDisplay => Timestamp.ToString("dd MMM yyyy hh:mm tt");
    public string Summary => $"{TimestampDisplay} · {Type} · {Size}";
}

public sealed class SettingsPermissionRow
{
    public required string Permission { get; init; }
    public required string Owner { get; init; }
    public required string Manager { get; init; }
    public required string Cashier { get; init; }
}

public sealed class DemoSettingsState
{
    private static readonly Lazy<DemoSettingsState> s_instance =
        new(() => new DemoSettingsState());

    public static DemoSettingsState Instance => s_instance.Value;

    public event EventHandler? StateChanged;

    public string ShopName { get; private set; } = "Edge Electronics";
    public string OwnerName { get; private set; } = "Abdullah";
    public string Phone { get; private set; } = "03xx-xxxxxxx";
    public string Address { get; private set; } = "Main Market, Lahore";
    public string LogoPath { get; private set; } = string.Empty;

    public string Printer { get; private set; } = "Thermal 80mm";
    public string PaperSize { get; private set; } = "80mm";
    public string HeaderText { get; private set; } = "Edge Electronics - Main Branch";
    public string FooterText { get; private set; } = "Thank you for your business!";
    public bool ShowCustomer { get; private set; } = true;
    public bool ShowCashier { get; private set; } = true;
    public bool AutoPrint { get; private set; } = true;

    public ObservableCollection<SettingsUserRecord> Users { get; } = [];
    public ObservableCollection<SettingsCategoryRecord> Categories { get; } = [];
    public ObservableCollection<SettingsUnitRecord> Units { get; } = [];
    public ObservableCollection<SettingsBackupRecord> Backups { get; } = [];
    public IReadOnlyList<SettingsPermissionRow> PermissionMatrix { get; }

    public string LicenseId { get; private set; } = "ER-2026-LAHORE-001";
    public string LicenseStore { get; private set; } = "Edge Electronics";
    public string LicenseModule { get; private set; } = "Electronics";
    public DateTime LicenseExpiry { get; private set; } = new(2027, 12, 31);
    public int LicensedTerminals { get; private set; } = 1;
    public string LicenseStatus { get; private set; } = "Preview · Integration Pending";

    public string DatabaseName => "edgeretails_v1 (planned)";
    public string DatabaseSize => "Preview · 48.2 MB";
    public string DatabaseStatus => "Integration Pending";
    public string ConnectionStatus => "Not Connected · Frontend Shell";
    public string WorkerStatus => "Integration Pending";
    public DateTime LastBackup => new(2026, 9, 17, 2, 30, 0);

    private DemoSettingsState()
    {
        Users.Add(new SettingsUserRecord("Abdullah", "Owner"));
        Users.Add(new SettingsUserRecord("Ali", "Cashier"));

        foreach (var name in new[] { "Lighting", "Switches", "Breakers", "Cables", "Accessories" })
        {
            Categories.Add(new SettingsCategoryRecord(name));
        }

        Units.Add(new SettingsUnitRecord("Piece", "pc"));
        Units.Add(new SettingsUnitRecord("Box", "box"));
        Units.Add(new SettingsUnitRecord("Roll", "roll"));
        Units.Add(new SettingsUnitRecord("Meter", "m"));
        Units.Add(new SettingsUnitRecord("Foot", "ft"));

        Backups.Add(new SettingsBackupRecord
        {
            Timestamp = new DateTime(2026, 9, 17, 2, 30, 0),
            Type = "Cloud Preview",
            Status = "Sample",
            Size = "48.2 MB"
        });
        Backups.Add(new SettingsBackupRecord
        {
            Timestamp = new DateTime(2026, 9, 16, 20, 30, 0),
            Type = "Cloud Preview",
            Status = "Sample",
            Size = "47.9 MB"
        });
        Backups.Add(new SettingsBackupRecord
        {
            Timestamp = new DateTime(2026, 9, 16, 14, 30, 0),
            Type = "Cloud Preview",
            Status = "Sample",
            Size = "47.8 MB"
        });

        PermissionMatrix = CreatePermissionMatrix();
        DemoRetailState.Instance.StateChanged += (_, _) =>
        {
            foreach (var category in Categories)
            {
                category.RefreshCount();
            }

            RaiseStateChanged();
        };
    }

    public void SaveShop(
        string shopName,
        string ownerName,
        string phone,
        string address,
        string logoPath)
    {
        if (string.IsNullOrWhiteSpace(shopName) || string.IsNullOrWhiteSpace(ownerName))
        {
            throw new InvalidOperationException("Shop name and owner name are required.");
        }

        ShopName = shopName.Trim();
        OwnerName = ownerName.Trim();
        Phone = phone.Trim();
        Address = address.Trim();
        LogoPath = logoPath.Trim();
        RaiseStateChanged();
    }

    public void SaveReceipt(
        string printer,
        string paperSize,
        string headerText,
        string footerText,
        bool showCustomer,
        bool showCashier,
        bool autoPrint)
    {
        Printer = string.IsNullOrWhiteSpace(printer) ? "Thermal 80mm" : printer.Trim();
        PaperSize = string.IsNullOrWhiteSpace(paperSize) ? "80mm" : paperSize.Trim();
        HeaderText = headerText.Trim();
        FooterText = footerText.Trim();
        ShowCustomer = showCustomer;
        ShowCashier = showCashier;
        AutoPrint = autoPrint;
        RaiseStateChanged();
    }

    public void SaveUser(
        SettingsUserRecord? existing,
        string name,
        string role,
        bool isActive)
    {
        var normalizedName = name.Trim();
        var normalizedRole = role.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new InvalidOperationException("User name is required.");
        }

        if (normalizedRole is not ("Owner" or "Manager" or "Cashier"))
        {
            throw new InvalidOperationException("Select a valid user role.");
        }

        if (Users.Any(user =>
            !ReferenceEquals(user, existing) &&
            string.Equals(user.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Another user already uses this name.");
        }

        if (existing is not null &&
            existing.Role == "Owner" &&
            existing.IsActive &&
            (!isActive || normalizedRole != "Owner") &&
            Users.Count(user => user.IsActive && user.Role == "Owner") <= 1)
        {
            throw new InvalidOperationException("At least one active Owner must remain.");
        }

        if (existing is null)
        {
            Users.Add(new SettingsUserRecord(normalizedName, normalizedRole, isActive));
        }
        else
        {
            existing.Name = normalizedName;
            existing.Role = normalizedRole;
            existing.IsActive = isActive;
        }

        RaiseStateChanged();
    }

    public void SaveCategory(SettingsCategoryRecord? existing, string name)
    {
        var normalized = name.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Category name is required.");
        }

        if (Categories.Any(item =>
            !ReferenceEquals(item, existing) &&
            string.Equals(item.Name, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Another category already uses this name.");
        }

        if (existing is null)
        {
            Categories.Add(new SettingsCategoryRecord(normalized));
            RaiseStateChanged();
            return;
        }

        var oldName = existing.Name;
        existing.Name = normalized;

        var referencedProducts = DemoRetailState.Instance.Products
            .Where(product => string.Equals(
                product.Category,
                oldName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var product in referencedProducts)
        {
            product.Category = normalized;
        }

        existing.RefreshCount();
        if (referencedProducts.Length > 0)
        {
            DemoRetailState.Instance.NotifyProductChanged();
            return;
        }

        RaiseStateChanged();
    }

    public void SaveUnit(
        SettingsUnitRecord? existing,
        string name,
        string symbol)
    {
        var normalizedName = name.Trim();
        var normalizedSymbol = symbol.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName) ||
            string.IsNullOrWhiteSpace(normalizedSymbol))
        {
            throw new InvalidOperationException("Unit name and symbol are required.");
        }

        if (Units.Any(item =>
            !ReferenceEquals(item, existing) &&
            (string.Equals(item.Name, normalizedName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Symbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("Another unit already uses this name or symbol.");
        }

        if (existing is null)
        {
            Units.Add(new SettingsUnitRecord(normalizedName, normalizedSymbol));
            RaiseStateChanged();
            return;
        }

        var oldName = existing.Name;
        var oldSymbol = existing.Symbol;
        existing.Name = normalizedName;
        existing.Symbol = normalizedSymbol;

        var referencedProducts = DemoRetailState.Instance.Products
            .Where(product =>
                string.Equals(product.Unit, oldName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(product.Unit, oldSymbol, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(product.Unit, oldSymbol + "s", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var product in referencedProducts)
        {
            product.Unit = normalizedName;
        }

        if (referencedProducts.Length > 0)
        {
            DemoRetailState.Instance.NotifyProductChanged();
            return;
        }

        RaiseStateChanged();
    }

    public void SetCategoryActive(SettingsCategoryRecord category, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(category);
        category.IsActive = isActive;
        RaiseStateChanged();
    }

    public void SetUnitActive(SettingsUnitRecord unit, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(unit);
        unit.IsActive = isActive;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static IReadOnlyList<SettingsPermissionRow> CreatePermissionMatrix() =>
    [
        new() { Permission = "Create Sale", Owner = "Yes", Manager = "Yes", Cashier = "Yes" },
        new() { Permission = "View Sales History", Owner = "Yes", Manager = "Yes", Cashier = "Yes" },
        new() { Permission = "View Profit", Owner = "Yes", Manager = "Optional", Cashier = "No" },
        new() { Permission = "Create Thaka", Owner = "Yes", Manager = "Optional", Cashier = "Optional" },
        new() { Permission = "Add Thaka Material", Owner = "Yes", Manager = "Optional", Cashier = "Optional" },
        new() { Permission = "Record Thaka Payment", Owner = "Yes", Manager = "Optional", Cashier = "Optional" },
        new() { Permission = "Create Purchase", Owner = "Yes", Manager = "Yes", Cashier = "No" },
        new() { Permission = "Manage Inventory", Owner = "Yes", Manager = "Yes", Cashier = "No" },
        new() { Permission = "Stock Adjustment", Owner = "Yes", Manager = "Optional", Cashier = "No" },
        new() { Permission = "Manage Expenses", Owner = "Yes", Manager = "Optional", Cashier = "No" },
        new() { Permission = "View Reports", Owner = "Yes", Manager = "Optional", Cashier = "No" },
        new() { Permission = "Manage Users", Owner = "Yes", Manager = "No", Cashier = "No" },
        new() { Permission = "Manage Settings", Owner = "Yes", Manager = "No", Cashier = "No" },
        new() { Permission = "Backup / Restore", Owner = "Yes", Manager = "No", Cashier = "No" }
    ];
}
