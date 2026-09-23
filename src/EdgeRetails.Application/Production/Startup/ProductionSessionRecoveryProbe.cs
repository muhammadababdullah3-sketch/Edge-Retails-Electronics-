namespace EdgeRetails.Application.Production.Startup;

/// <summary>
/// Session recovery never authenticates a user by itself. It only determines whether the existing
/// Login / User Switch screen may show a safe recovery hint after all production startup gates pass.
/// </summary>
public sealed class ProductionSessionRecoveryProbe : ISessionRecoveryProbe
{
    private readonly IProductionSessionStore _store;
    private readonly IProductionSessionUserProbe _users;
    private readonly TimeProvider _timeProvider;

    public ProductionSessionRecoveryProbe(IProductionSessionStore store, IProductionSessionUserProbe users, TimeProvider? timeProvider = null)
    {
        _store = store;
        _users = users;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SessionRecoveryResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await _store.ReadAsync(cancellationToken);
            if (session is null)
            {
                return new SessionRecoveryResult(false, null) { State = SessionRecoveryState.None };
            }

            if (session.ExpiresAtUtc is not null && session.ExpiresAtUtc.Value <= _timeProvider.GetUtcNow())
            {
                await _store.ClearAsync(cancellationToken);
                return new SessionRecoveryResult(false, null) { State = SessionRecoveryState.Expired };
            }

            var eligibility = await _users.CheckAsync(session.UserId, cancellationToken);
            if (!eligibility.Eligible)
            {
                await _store.ClearAsync(cancellationToken);
                return new SessionRecoveryResult(false, null) { State = SessionRecoveryState.UserUnavailable };
            }

            var displayName = eligibility.DisplayName ?? session.DisplayName;
            var hint = string.IsNullOrWhiteSpace(displayName)
                ? "A previous eligible session was detected. Select the account and authenticate to continue."
                : $"Previous account: {displayName}. Authenticate to continue.";
            return new SessionRecoveryResult(true, hint) { State = SessionRecoveryState.Recoverable };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            try { await _store.ClearAsync(CancellationToken.None); } catch { }
            return new SessionRecoveryResult(false, null) { State = SessionRecoveryState.ProbeFailed };
        }
    }
}
