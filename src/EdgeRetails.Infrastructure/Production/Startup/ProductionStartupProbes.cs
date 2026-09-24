using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Production.Startup;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Infrastructure.Production.Startup;

public sealed class InstallationStateProbe : ISetupStateProbe
{
    private readonly IInstallationStateReadService _readService;

    public InstallationStateProbe(IInstallationStateReadService readService)
    {
        _readService = readService;
    }

    public async Task<SetupStateResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var state = await _readService.GetAsync(cancellationToken);
        var isComplete = state?.SetupStatus == SetupStatus.Complete;
        return new SetupStateResult(
            isComplete,
            isComplete ? "Setup is complete." : "Setup is incomplete or has not started.");
    }
}

public sealed class DefaultSessionRecoveryProbe : ISessionRecoveryProbe
{
    public Task<SessionRecoveryResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SessionRecoveryResult(false, null)
        {
            State = SessionRecoveryState.None
        });
    }
}
