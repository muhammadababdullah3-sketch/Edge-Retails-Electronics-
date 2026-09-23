namespace EdgeRetails.Domain.SystemConfiguration;

public enum TerminalStatus
{
    Active = 1,
    Suspended = 2,
    Revoked = 3
}

public enum ConnectivityState
{
    Connected = 1,
    Degraded = 2,
    Reconnecting = 3,
    Disconnected = 4
}

public sealed class Terminal
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string TerminalCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TerminalStatus Status { get; set; } = TerminalStatus.Active;
    public string? HardwareFingerprint { get; set; }
    public string? ProtocolVersion { get; set; }
    public string? LastKnownIpAddress { get; set; }
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public string? AuthSecretHash { get; set; }

    public bool IsActive => Status == TerminalStatus.Active;
    public bool IsSuspended => Status == TerminalStatus.Suspended;
    public bool IsRevoked => Status == TerminalStatus.Revoked;
    public bool CanMutate => Status == TerminalStatus.Active;

    public bool VerifySecret(string? rawSecret)
    {
        if (string.IsNullOrWhiteSpace(AuthSecretHash))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawSecret))
        {
            return false;
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawSecret)));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(AuthSecretHash),
            System.Text.Encoding.UTF8.GetBytes(hash));
    }
}

public sealed record TerminalSessionContext(
    Guid? TerminalId,
    Guid? UserId,
    Guid? SessionId,
    Guid? CorrelationId,
    Guid? ClientOperationId);
