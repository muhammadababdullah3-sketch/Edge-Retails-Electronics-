namespace EdgeRetails.Domain.SystemConfiguration;

public enum SetupStatus
{
    NotStarted = 0,
    InProgress = 1,
    Complete = 2
}

public sealed class InstallationState
{
    public Guid InstallationId { get; set; } = Guid.CreateVersion7();
    public SetupStatus SetupStatus { get; set; } = SetupStatus.NotStarted;
    public string? SelectedModule { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Version { get; set; }
}
