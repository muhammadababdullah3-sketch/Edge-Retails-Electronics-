using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Features.Terminals;

public sealed class RegisterTerminalHandler
{
    private readonly ITerminalRepository _terminals;
    private readonly RuntimeLicenseService _license;
    private readonly ITransactionRunner? _transactions;
    private readonly IResourceLock? _resourceLock;

    public RegisterTerminalHandler(
        ITerminalRepository terminals,
        RuntimeLicenseService license,
        ITransactionRunner? transactions = null,
        IResourceLock? resourceLock = null)
    {
        _terminals = terminals;
        _license = license;
        _transactions = transactions;
        _resourceLock = resourceLock;
    }

    public async Task<Result<RegisterTerminalResult>> HandleAsync(
        RegisterTerminalCommand command,
        CancellationToken cancellationToken = default)
    {
        if (_transactions is not null && _resourceLock is not null)
        {
            return await _transactions.ExecuteAsync(async ct =>
            {
                await _resourceLock.AcquireAsync("terminal", "registration_quota", ct);
                return await RegisterCoreAsync(command, ct);
            }, cancellationToken);
        }

        return await RegisterCoreAsync(command, cancellationToken);
    }

    private async Task<Result<RegisterTerminalResult>> RegisterCoreAsync(
        RegisterTerminalCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.TerminalCode))
        {
            return Result<RegisterTerminalResult>.Failure(
                "terminals.code_required",
                "Terminal code is required.");
        }

        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Result<RegisterTerminalResult>.Failure(
                "terminals.name_required",
                "Terminal name is required.");
        }

        if (!TerminalProtocol.IsCompatible(command.ProtocolVersion))
        {
            return Result<RegisterTerminalResult>.Failure(
                "terminals.protocol_incompatible",
                $"Terminal protocol version '{command.ProtocolVersion}' is incompatible with server '{TerminalProtocol.CurrentProtocolVersion}'.");
        }

        var code = command.TerminalCode.Trim().ToUpperInvariant();
        var existing = await _terminals.GetByCodeAsync(code, cancellationToken);
        if (existing is not null)
        {
            if (existing.Status == TerminalStatus.Revoked)
            {
                return Result<RegisterTerminalResult>.Failure(
                    "terminals.revoked",
                    $"Terminal '{code}' has been revoked and cannot connect.");
            }

            if (string.IsNullOrWhiteSpace(command.ClientAuthSecret))
            {
                return Result<RegisterTerminalResult>.Failure(
                    "terminals.secret_required",
                    "Terminal authentication secret is required.");
            }

            if (!existing.VerifySecret(command.ClientAuthSecret))
            {
                return Result<RegisterTerminalResult>.Failure(
                    "terminals.invalid_secret",
                    "Terminal authentication failed.");
            }

            existing.LastSeenAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(command.LastKnownIpAddress))
            {
                existing.LastKnownIpAddress = command.LastKnownIpAddress.Trim();
            }
            if (!string.IsNullOrWhiteSpace(command.HardwareFingerprint))
            {
                existing.HardwareFingerprint = command.HardwareFingerprint.Trim();
            }
            existing.ProtocolVersion = command.ProtocolVersion;

            await _terminals.UpdateAsync(existing, cancellationToken);

            return Result<RegisterTerminalResult>.Success(new RegisterTerminalResult(
                existing.Id,
                existing.TerminalCode,
                existing.Name,
                existing.Status,
                TerminalProtocol.CurrentProtocolVersion,
                existing.RegisteredAt));
        }

        if (string.IsNullOrWhiteSpace(command.ClientAuthSecret))
        {
            return Result<RegisterTerminalResult>.Failure(
                "terminals.secret_required",
                "Terminal authentication secret is required.");
        }

        // Validate license terminal quota
        var licenseState = await _license.ValidatePersistedAsync(cancellationToken);
        var activeCount = await _terminals.GetActiveCountAsync(cancellationToken);
        if (licenseState.Payload is not null && activeCount >= licenseState.Payload.MaxTerminals)
        {
            return Result<RegisterTerminalResult>.Failure(
                "terminals.capacity_exceeded",
                $"Active terminal limit of {licenseState.Payload.MaxTerminals} has been reached for this installation.");
        }

        string? secretHash = null;
        if (!string.IsNullOrWhiteSpace(command.ClientAuthSecret))
        {
            secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.ClientAuthSecret)));
        }

        var terminal = new Terminal
        {
            Id = Guid.CreateVersion7(),
            TerminalCode = code,
            Name = command.Name.Trim(),
            Status = TerminalStatus.Active,
            HardwareFingerprint = command.HardwareFingerprint?.Trim(),
            ProtocolVersion = command.ProtocolVersion?.Trim(),
            LastKnownIpAddress = command.LastKnownIpAddress?.Trim(),
            AuthSecretHash = secretHash,
            RegisteredAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };

        await _terminals.AddAsync(terminal, cancellationToken);

        return Result<RegisterTerminalResult>.Success(new RegisterTerminalResult(
            terminal.Id,
            terminal.TerminalCode,
            terminal.Name,
            terminal.Status,
            TerminalProtocol.CurrentProtocolVersion,
            terminal.RegisteredAt));
    }
}

public sealed class TerminalHeartbeatHandler
{
    private readonly ITerminalRepository _terminals;
    private readonly IClock _clock;

    public TerminalHeartbeatHandler(ITerminalRepository terminals, IClock clock)
    {
        _terminals = terminals;
        _clock = clock;
    }

    public async Task<Result<TerminalHeartbeatResult>> HandleAsync(
        TerminalHeartbeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var terminal = await _terminals.GetByIdAsync(command.TerminalId, cancellationToken);
        if (terminal is null)
        {
            return Result<TerminalHeartbeatResult>.Failure(
                "terminals.not_found",
                $"Terminal '{command.TerminalId}' is not registered.");
        }

        if (terminal.Status == TerminalStatus.Revoked)
        {
            return Result<TerminalHeartbeatResult>.Failure(
                "terminals.revoked",
                "Terminal has been revoked.");
        }

        if (!TerminalProtocol.IsCompatible(command.ProtocolVersion))
        {
            return Result<TerminalHeartbeatResult>.Failure(
                "terminals.protocol_incompatible",
                $"Terminal protocol version '{command.ProtocolVersion}' is incompatible with server '{TerminalProtocol.CurrentProtocolVersion}'.");
        }

        terminal.LastSeenAt = _clock.UtcNow;
        if (!string.IsNullOrWhiteSpace(command.CurrentIpAddress))
        {
            terminal.LastKnownIpAddress = command.CurrentIpAddress.Trim();
        }
        await _terminals.UpdateAsync(terminal, cancellationToken);

        return Result<TerminalHeartbeatResult>.Success(new TerminalHeartbeatResult(
            terminal.Id,
            terminal.Status,
            terminal.CanMutate,
            _clock.UtcNow,
            TerminalProtocol.CurrentProtocolVersion));
    }
}

public sealed class UpdateTerminalStatusHandler
{
    private readonly ITerminalRepository _terminals;
    private readonly IApplicationPermissionAuthorizer? _authorization;

    public UpdateTerminalStatusHandler(
        ITerminalRepository terminals,
        IApplicationPermissionAuthorizer? authorization = null)
    {
        _terminals = terminals;
        _authorization = authorization;
    }

    public async Task<Result<UpdateTerminalStatusResult>> HandleAsync(
        UpdateTerminalStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        if (_authorization is not null)
        {
            var authResult = await _authorization.AuthorizeAsync(
                command.ActorUserId,
                PermissionKeys.SettingsManage,
                cancellationToken);
            if (!authResult.IsSuccess)
            {
                return Result<UpdateTerminalStatusResult>.Failure(
                    authResult.Error!.Code,
                    authResult.Error.Message);
            }
        }

        var terminal = await _terminals.GetByIdAsync(command.TerminalId, cancellationToken);
        if (terminal is null)
        {
            return Result<UpdateTerminalStatusResult>.Failure(
                "terminals.not_found",
                $"Terminal '{command.TerminalId}' was not found.");
        }

        terminal.Status = command.NewStatus;
        terminal.LastSeenAt = DateTimeOffset.UtcNow;
        await _terminals.UpdateAsync(terminal, cancellationToken);

        return Result<UpdateTerminalStatusResult>.Success(new UpdateTerminalStatusResult(
            terminal.Id,
            terminal.Status));
    }
}
