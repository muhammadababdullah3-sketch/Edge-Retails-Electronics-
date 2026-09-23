using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.SystemConfiguration;

public sealed class ShopProfile : Entity
{
    public string ProfileKey { get; set; } = "PRIMARY";
    public string ShopName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ReceiptTemplateSettings : Entity
{
    public string TemplateKey { get; set; } = "PRIMARY";
    public string? Header { get; set; }
    public string? Footer { get; set; }
    public bool ShowCustomer { get; set; } = true;
    public bool ShowCashier { get; set; } = true;
    public string LogoBehavior { get; set; } = "NONE";
    public int TemplateVersion { get; set; } = 1;
    public bool AutoPrintDefault { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}
