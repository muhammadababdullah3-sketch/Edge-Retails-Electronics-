using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Posting;

public static class PostingOperationTypes
{
    public const string Purchase = "Purchase";
    public const string PurchaseReturn = "PurchaseReturn";
    public const string Sale = "Sale";
    public const string SaleReturn = "SaleReturn";
    public const string Expense = "Expense";
    public const string Thaka = "Thaka";
    public const string ThakaCreditRefund = "ThakaCreditRefund";
    public const string StockAdjustment = "StockAdjustment";
    public const string SupplierPayment = "SupplierPayment";
    public const string ProductIntake = "ProductIntake";
}

public sealed record ShopPostingDateRecord(
    DateTimeOffset EventTimestampUtc,
    DateOnly PostingDate,
    DateOnly? SupplierDocumentDate)
{
    public void ValidateInvariants()
    {
        if (EventTimestampUtc == default || EventTimestampUtc == DateTimeOffset.MinValue || EventTimestampUtc == DateTimeOffset.MaxValue)
        {
            throw new BusinessRuleException(
                "posting.event_timestamp_invalid",
                "Authoritative UTC event timestamp must be a valid, non-default timestamp.");
        }

        if (PostingDate == default || PostingDate == DateOnly.MinValue || PostingDate == DateOnly.MaxValue)
        {
            throw new BusinessRuleException(
                "posting.posting_date_invalid",
                "Shop business posting date must be a valid, non-default calendar date.");
        }
    }
}

public sealed record SupervisorApprovalRequest(
    Guid SupervisorId,
    string SupervisorPin,
    string Reason,
    DateTimeOffset RequestedAtUtc)
{
    public void ValidateInvariants()
    {
        if (SupervisorId == Guid.Empty)
        {
            throw new BusinessRuleException(
                "authorization.supervisor_id_required",
                "Supervisor identifier is required for backdated posting approval.");
        }

        if (string.IsNullOrWhiteSpace(SupervisorPin) || SupervisorPin.Length != 4 || SupervisorPin.Any(ch => !char.IsAsciiDigit(ch)))
        {
            throw new BusinessRuleException(
                "authorization.invalid_supervisor_pin",
                "Supervisor approval requires an exact 4-digit cryptographic PIN.");
        }

        if (string.IsNullOrWhiteSpace(Reason))
        {
            throw new BusinessRuleException(
                "authorization.reason_required",
                "A clear business justification reason is required for backdated posting approval.");
        }
    }
}

public sealed class PeriodLock : Entity
{
    public string PeriodName { get; set; } = string.Empty;
    public DateOnly PeriodStartDate { get; set; }
    public DateOnly PeriodEndDate { get; set; }
    public bool IsLocked { get; set; }
    public DateTimeOffset LockedAt { get; set; }
    public Guid LockedBy { get; set; }
    public string? LockReason { get; set; }

    public bool IsDateInLockedPeriod(DateOnly date)
    {
        return IsLocked && date >= PeriodStartDate && date <= PeriodEndDate;
    }
}

public sealed class PostingAuditEntry : Entity
{
    public string OperationType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string EntityNumber { get; set; } = string.Empty;
    public Guid ActorId { get; set; }
    public Guid? SupervisorId { get; set; }
    public DateOnly ShopDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public DateOnly? SupplierDocumentDate { get; set; }
    public DateTimeOffset EventTimestampUtc { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public void ValidateInvariants()
    {
        if (string.IsNullOrWhiteSpace(OperationType))
        {
            throw new BusinessRuleException("posting.audit_operation_type_required", "Operation type is required for posting audit.");
        }
        if (EntityId == Guid.Empty)
        {
            throw new BusinessRuleException("posting.audit_entity_id_required", "Entity identifier is required for posting audit.");
        }
        if (ActorId == Guid.Empty)
        {
            throw new BusinessRuleException("posting.audit_actor_id_required", "Actor identifier is required for posting audit.");
        }
        if (ShopDate == default)
        {
            throw new BusinessRuleException("posting.audit_shop_date_required", "Authoritative shop date is required for posting audit.");
        }
        if (PostingDate == default)
        {
            throw new BusinessRuleException("posting.audit_posting_date_required", "Effective posting date is required for posting audit.");
        }
        if (EventTimestampUtc == default)
        {
            throw new BusinessRuleException("posting.audit_event_timestamp_required", "UTC event timestamp is required for posting audit.");
        }
    }
}

public static class ShopPostingPolicy
{
    public const int MaxBackdateCalendarDays = 7;

    public static void AssertPostingDateAllowed(
        DateOnly shopDate,
        DateOnly postingDate,
        bool hasSupervisorApproval)
    {
        if (postingDate > shopDate)
        {
            throw new BusinessRuleException(
                "posting.future_date_forbidden",
                $"Posting date {postingDate:yyyy-MM-dd} cannot be in the future. Authoritative shop date is {shopDate:yyyy-MM-dd}.");
        }

        var earliestAllowed = shopDate.AddDays(-MaxBackdateCalendarDays);
        if (postingDate < earliestAllowed)
        {
            throw new BusinessRuleException(
                "posting.backdate_exceeds_window",
                $"Posting date {postingDate:yyyy-MM-dd} is older than the allowed {MaxBackdateCalendarDays}-calendar-day window (earliest allowed: {earliestAllowed:yyyy-MM-dd}).");
        }

        if (postingDate < shopDate && !hasSupervisorApproval)
        {
            throw new BusinessRuleException(
                "posting.supervisor_authorization_required",
                $"Historical posting date {postingDate:yyyy-MM-dd} requires supervisor authorization.");
        }
    }
}
