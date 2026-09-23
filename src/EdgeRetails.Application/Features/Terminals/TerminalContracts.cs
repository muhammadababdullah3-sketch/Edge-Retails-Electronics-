using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Features.Terminals;

public static class TerminalProtocol
{
    public const string CurrentProtocolVersion = "1.0.0";
    public const string MinimumSupportedProtocolVersion = "1.0.0";

    public static bool IsCompatible(string? clientVersion)
    {
        if (string.IsNullOrWhiteSpace(clientVersion))
        {
            return false;
        }

        return string.Equals(clientVersion.Trim(), CurrentProtocolVersion, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record RegisterTerminalCommand(
    string TerminalCode,
    string Name,
    string? HardwareFingerprint,
    string? ProtocolVersion,
    string? LastKnownIpAddress,
    string? ClientAuthSecret);

public sealed record RegisterTerminalResult(
    Guid TerminalId,
    string TerminalCode,
    string Name,
    TerminalStatus Status,
    string ServerProtocolVersion,
    DateTimeOffset RegisteredAt);

public sealed record TerminalHeartbeatCommand(
    Guid TerminalId,
    string? ProtocolVersion,
    string? CurrentIpAddress);

public sealed record TerminalHeartbeatResult(
    Guid TerminalId,
    TerminalStatus Status,
    bool CanMutate,
    DateTimeOffset ServerTimeUtc,
    string ServerProtocolVersion);

public sealed record UpdateTerminalStatusCommand(
    Guid TerminalId,
    TerminalStatus NewStatus,
    Guid ActorUserId);

public sealed record UpdateTerminalStatusResult(
    Guid TerminalId,
    TerminalStatus Status);
