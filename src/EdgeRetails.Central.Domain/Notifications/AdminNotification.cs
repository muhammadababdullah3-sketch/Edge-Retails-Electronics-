namespace EdgeRetails.Central.Domain.Notifications;

// An operational read model. Reading a notice never changes its domain case.
public sealed class AdminNotification
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public Guid TargetId { get; set; }
    public Guid? ShopId { get; set; }
    public string RequiredCapability { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}
