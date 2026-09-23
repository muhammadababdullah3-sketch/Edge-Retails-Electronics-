using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Parties;

public sealed class Customer : Entity
{
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsWalkIn { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class Supplier : Entity
{
    public string? DealerCode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? City { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }
}
