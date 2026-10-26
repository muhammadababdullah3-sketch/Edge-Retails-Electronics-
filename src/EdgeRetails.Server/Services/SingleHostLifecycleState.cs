using EdgeRetails.Application.Abstractions;

namespace EdgeRetails.Server.Services;

public sealed class SingleHostLifecycleState : ISingleHostLifecycleState
{
    public DateTimeOffset ServerStartedAtUtc { get; } = DateTimeOffset.UtcNow;

    private volatile bool _isDraining;

    public bool IsDraining => _isDraining;

    public void BeginDrain() => _isDraining = true;
}
