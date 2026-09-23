using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Finance;

public enum ExpenseStatus
{
    Posted = 1,
    Voided = 2
}

public enum ExpensePaymentMethod
{
    Cash = 1,
    Bank = 2,
    Other = 3
}

public sealed class ExpenseCategory : Entity
{
    public string Name { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }
}

public sealed class ExpenseSubcategory : Entity
{
    public Guid CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public long Version { get; set; }
}
public sealed class Expense : Entity
{
    public string ExpenseNumber { get; set; } = string.Empty;
    public Guid CategoryId { get; set; }
    public Guid? SubcategoryId { get; set; }
    public DateOnly ExpenseDate { get; set; }
    public decimal Amount { get; set; }
    public ExpensePaymentMethod PaymentMethod { get; set; }
    public Guid? CashSessionId { get; set; }
    public string? Reference { get; set; }
    public string Description { get; set; } = string.Empty;
    public ExpenseStatus Status { get; set; } = ExpenseStatus.Posted;
    public Guid ClientOperationId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }
    public long Version { get; set; }
}
