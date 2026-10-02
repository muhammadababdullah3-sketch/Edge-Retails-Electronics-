using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Production.Recovery;
using EdgeRetails.Domain.Identity;

namespace EdgeRetails.Application.Features.Identity;

public sealed record RecoverOwnerPinCommand(
    string? SignedAuthorization,
    Guid TargetUserId,
    string NewPin);

public sealed record RecoverOwnerPinResult(Guid TargetUserId, DateTimeOffset RecoveredAt);

/// <summary>
/// Applies Owner PIN recovery only when the Shop Server validates a one-time,
/// separately signed Recovery Authorization. The caller has no database authority.
/// </summary>
public sealed class RecoverOwnerPinHandler
{
    private readonly IIdentityCredentialRecoveryRepository _recovery;
    private readonly IIdentityReadRepository _identity;
    private readonly IRecoveryAuthorizationValidator _authorizationValidator;
    private readonly IPinCredentialService _pinCredentials;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;

    public RecoverOwnerPinHandler(
        IIdentityCredentialRecoveryRepository recovery,
        IIdentityReadRepository identity,
        IRecoveryAuthorizationValidator authorizationValidator,
        IPinCredentialService pinCredentials,
        IBusinessAuditWriter audit,
        IClock clock)
    {
        _recovery = recovery;
        _identity = identity;
        _authorizationValidator = authorizationValidator;
        _pinCredentials = pinCredentials;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Result<RecoverOwnerPinResult>> HandleAsync(
        RecoverOwnerPinCommand command,
        CancellationToken cancellationToken)
    {
        var validation = await _authorizationValidator.ValidateAsync(
            command.SignedAuthorization,
            command.TargetUserId,
            cancellationToken);
        if (!validation.IsValid)
        {
            await RecordDenialAsync(command.TargetUserId, Guid.NewGuid(), Guid.Empty,
                "identity.recovery_authorization_invalid", cancellationToken);
            return Result<RecoverOwnerPinResult>.Failure(
                validation.FailureCode,
                "A valid governance-issued Recovery Authorization is required. No account was changed.");
        }

        var authorization = validation.Authorization!;
        var correlationId = authorization.Nonce;
        var actorId = GetAuditActorId(authorization.IssuerId);

        if (await _recovery.HasRecoveryOperationAsync(correlationId, cancellationToken))
        {
            await RecordDenialAsync(command.TargetUserId, correlationId, actorId,
                "identity.recovery_replay", cancellationToken);
            return Result<RecoverOwnerPinResult>.Failure(
                "identity.recovery_replay",
                "This Recovery Authorization has already been used.");
        }

        if (command.TargetUserId == Guid.Empty ||
            string.IsNullOrEmpty(command.NewPin) ||
            command.NewPin.Length != 4 ||
            command.NewPin.Any(ch => !char.IsAsciiDigit(ch)))
        {
            await ConsumeAndDenyAsync(command.TargetUserId, correlationId, actorId,
                "identity.recovery_invalid_request", cancellationToken);
            return Result<RecoverOwnerPinResult>.Failure(
                "identity.recovery_invalid_request",
                "Enter a four-digit PIN for the Owner account bound to the authorization.");
        }

        var user = await _recovery.GetUserForRecoveryUpdateAsync(command.TargetUserId, cancellationToken);
        var role = user is null ? null : await _identity.GetRoleAsync(user.RoleId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active || role is null || !role.IsActive ||
            !string.Equals(role.Name, "Owner", StringComparison.OrdinalIgnoreCase))
        {
            await ConsumeAndDenyAsync(command.TargetUserId, correlationId, actorId,
                "identity.recovery_target_unavailable", cancellationToken);
            return Result<RecoverOwnerPinResult>.Failure(
                "identity.recovery_target_unavailable",
                "The authorized account is not an active Owner account.");
        }

        var now = _clock.UtcNow;
        var credential = _pinCredentials.Hash(command.NewPin);
        user.PinHash = credential.Hash;
        user.PinSalt = credential.Salt;
        user.PinIterations = credential.Iterations;
        user.PinAlgorithm = credential.Algorithm;
        user.UpdatedAt = now;
        user.Version++;
        await _recovery.RevokeActiveSessionsAsync(user.Id, now, cancellationToken);

        RecordConsumed(user.Id, correlationId, actorId);
        _audit.Record(
            "USER_PIN_RECOVERY_SUCCEEDED",
            "USER",
            user.Id,
            actorId,
            correlationId,
            "Owner PIN recovered using signed Recovery Authorization; active sessions revoked.");

        if (!await _recovery.TrySaveRecoveryChangesAsync(cancellationToken))
        {
            var failureCode = await _recovery.HasRecoveryOperationAsync(correlationId, cancellationToken)
                ? "identity.recovery_replay"
                : "identity.recovery_concurrent";
            var failureMessage = failureCode == "identity.recovery_replay"
                ? "This Recovery Authorization has already been used."
                : "Another recovery request changed this account. Review the account and issue a new authorization.";
            await RecordDenialAsync(command.TargetUserId, correlationId, actorId, failureCode, cancellationToken);
            return Result<RecoverOwnerPinResult>.Failure(
                failureCode,
                failureMessage);
        }

        return Result<RecoverOwnerPinResult>.Success(new RecoverOwnerPinResult(user.Id, now));
    }

    private async Task ConsumeAndDenyAsync(
        Guid targetUserId,
        Guid nonce,
        Guid actorId,
        string code,
        CancellationToken cancellationToken)
    {
        RecordConsumed(targetUserId, nonce, actorId);
        _audit.Record("USER_PIN_RECOVERY_FAILED", "USER", targetUserId == Guid.Empty ? null : targetUserId,
            actorId, nonce, $"Signed Owner PIN recovery was denied ({code}).");
        await _recovery.TrySaveRecoveryChangesAsync(cancellationToken);
    }

    private async Task RecordDenialAsync(
        Guid targetUserId,
        Guid correlationId,
        Guid actorId,
        string code,
        CancellationToken cancellationToken)
    {
        _audit.Record("USER_PIN_RECOVERY_DENIED", "USER", targetUserId == Guid.Empty ? null : targetUserId,
            actorId, correlationId, $"Owner PIN recovery was denied ({code}).");
        await _recovery.TrySaveRecoveryChangesAsync(cancellationToken);
    }

    private void RecordConsumed(Guid targetUserId, Guid nonce, Guid actorId) =>
        _audit.Record("USER_PIN_RECOVERY_AUTHORIZATION_CONSUMED", "USER",
            targetUserId == Guid.Empty ? null : targetUserId, actorId, nonce,
            "Signed Owner PIN Recovery Authorization consumed.");

    private static Guid GetAuditActorId(string issuerId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(issuerId));
        return new Guid(hash.AsSpan(0, 16));
    }
}
