using EdgeRetails.Application.Gateways;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Features.Terminals;

public sealed class ConnectivityStateMachine
{
    private readonly object _syncRoot = new();
    private ConnectivityState _currentState;
    private int _consecutiveHeartbeatFailures;
    private DateTimeOffset _lastStateChangeUtc;

    public const int MaxHeartbeatFailuresBeforeDegraded = 2;
    public const int MaxHeartbeatFailuresBeforeDisconnected = 5;

    public event EventHandler<ConnectivityStateChangedEventArgs>? StateChanged;

    public ConnectivityStateMachine(ConnectivityState initialState = ConnectivityState.Connected)
    {
        _currentState = initialState;
        _lastStateChangeUtc = DateTimeOffset.UtcNow;
    }

    public ConnectivityState CurrentState
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentState;
            }
        }
    }

    public bool CanMutate
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentState == ConnectivityState.Connected;
            }
        }
    }

    public int ConsecutiveHeartbeatFailures
    {
        get
        {
            lock (_syncRoot)
            {
                return _consecutiveHeartbeatFailures;
            }
        }
    }

    public DateTimeOffset LastStateChangeUtc
    {
        get
        {
            lock (_syncRoot)
            {
                return _lastStateChangeUtc;
            }
        }
    }

    public ConnectivityState RecordHeartbeatSuccess()
    {
        lock (_syncRoot)
        {
            _consecutiveHeartbeatFailures = 0;
            return TransitionTo(ConnectivityState.Connected, "Heartbeat succeeded");
        }
    }

    public ConnectivityState RecordHeartbeatFailure(string? reason = null)
    {
        lock (_syncRoot)
        {
            _consecutiveHeartbeatFailures++;

            if (_consecutiveHeartbeatFailures >= MaxHeartbeatFailuresBeforeDisconnected)
            {
                return TransitionTo(ConnectivityState.Disconnected, reason ?? "Max heartbeat failures exceeded");
            }

            if (_consecutiveHeartbeatFailures >= MaxHeartbeatFailuresBeforeDegraded)
            {
                if (_currentState == ConnectivityState.Connected)
                {
                    return TransitionTo(ConnectivityState.Degraded, reason ?? "Consecutive heartbeat failures");
                }
            }

            return _currentState;
        }
    }

    public ConnectivityState TransitionTo(ConnectivityState newState, string? reason = null)
    {
        ConnectivityState previousState;
        bool changed = false;

        lock (_syncRoot)
        {
            if (_currentState == newState)
            {
                return _currentState;
            }

            previousState = _currentState;
            _currentState = newState;
            _lastStateChangeUtc = DateTimeOffset.UtcNow;
            changed = true;
        }

        if (changed)
        {
            StateChanged?.Invoke(this, new ConnectivityStateChangedEventArgs(previousState, newState, reason));
        }

        return newState;
    }

    public void AssertCanMutate()
    {
        lock (_syncRoot)
        {
            if (_currentState != ConnectivityState.Connected)
            {
                throw new InvalidOperationException(
                    $"Authoritative mutations are prohibited while in connectivity state '{_currentState}'. Client must be Connected.");
            }
        }
    }
}
