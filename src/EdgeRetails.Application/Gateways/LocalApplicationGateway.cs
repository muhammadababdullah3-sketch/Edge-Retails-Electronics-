using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Gateways;

public sealed class LocalApplicationGateway : IApplicationGateway
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConnectivityStateMachine _stateMachine;
    private Guid? _currentTerminalId;
    private string? _clientAuthSecret;

    public LocalApplicationGateway(
        IServiceProvider serviceProvider,
        ConnectivityState initialState = ConnectivityState.Connected)
    {
        _serviceProvider = serviceProvider;
        _stateMachine = new ConnectivityStateMachine(initialState);
        _stateMachine.StateChanged += (sender, args) => ConnectivityChanged?.Invoke(this, args);
    }

    public ConnectivityState CurrentState => _stateMachine.CurrentState;
    public Guid? CurrentTerminalId => _currentTerminalId;
    public bool CanMutate => _stateMachine.CanMutate;

    public event EventHandler<ConnectivityStateChangedEventArgs>? ConnectivityChanged;

    public void SetTerminalContext(Guid terminalId, string? clientAuthSecret = null)
    {
        _currentTerminalId = terminalId;
        _clientAuthSecret = clientAuthSecret;
    }

    public void UpdateConnectivityState(ConnectivityState newState, string? reason = null)
    {
        _stateMachine.TransitionTo(newState, reason);
    }

    private T GetService<T>() where T : notnull
    {
        var service = _serviceProvider.GetService(typeof(T));
        if (service is null)
        {
            throw new InvalidOperationException($"Required service of type '{typeof(T).FullName}' could not be resolved.");
        }
        return (T)service;
    }

    private Result<T> CheckCanMutate<T>()
    {
        if (!CanMutate)
        {
            return Result<T>.Failure(
                "network.offline_mutation_forbidden",
                $"Authoritative mutations are prohibited while in connectivity state '{CurrentState}'. Client must be Connected.");
        }
        return Result<T>.Success(default!);
    }

    public async Task<Result<RegisterTerminalResult>> RegisterTerminalAsync(
        RegisterTerminalCommand command,
        CancellationToken cancellationToken = default)
    {
        var handler = GetService<RegisterTerminalHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<TerminalHeartbeatResult>> SendHeartbeatAsync(
        TerminalHeartbeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var handler = GetService<TerminalHeartbeatHandler>();
        var result = await handler.HandleAsync(command, cancellationToken);
        if (result.IsSuccess)
        {
            _stateMachine.RecordHeartbeatSuccess();
        }
        else
        {
            _stateMachine.RecordHeartbeatFailure(result.Error?.Message);
        }
        return result;
    }

    public async Task<Result<UpdateTerminalStatusResult>> UpdateTerminalStatusAsync(
        UpdateTerminalStatusCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<UpdateTerminalStatusResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<UpdateTerminalStatusHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<AuthoritativeRevalidationResult>> RevalidateAuthoritativeStateAsync(
        AuthoritativeRevalidationQuery query,
        CancellationToken cancellationToken = default)
    {
        var handler = GetService<AuthoritativeRevalidationHandler>();
        var result = await handler.HandleAsync(query, cancellationToken);
        if (result.IsSuccess)
        {
            _stateMachine.TransitionTo(ConnectivityState.Connected, "Authoritative revalidation succeeded");
        }
        return result;
    }

    public async Task<Result<CompleteSaleResult>> CompleteSaleAsync(
        CompleteSaleCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<CompleteSaleResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<CompleteSaleHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<CompleteSaleResult>> CompletePosDraftAsync(
        CompletePosDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<CompleteSaleResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<CompletePosDraftHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<CreateSaleReturnResult>> CreateSaleReturnAsync(
        CreateSaleReturnCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<CreateSaleReturnResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<CreateSaleReturnHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<CommercialExchangeResult>> ExecuteCommercialExchangeAsync(
        CommercialExchangeCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<CommercialExchangeResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<CommercialExchangeHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<CreatePurchaseResult>> CreatePurchaseAsync(
        CreatePurchaseCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<CreatePurchaseResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<CreatePurchaseHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<CreatePurchaseReturnResult>> CreatePurchaseReturnAsync(
        CreatePurchaseReturnCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<CreatePurchaseReturnResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<CreatePurchaseReturnHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<VoidPurchaseResult>> VoidPurchaseAsync(
        VoidPurchaseCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<VoidPurchaseResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<VoidPurchaseHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<SupplierPaymentResult>> CreateSupplierPaymentAsync(
        CreateSupplierPaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<SupplierPaymentResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<CreateSupplierPaymentHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<SupplierRefundResult>> CreateSupplierRefundAsync(
        CreateSupplierRefundCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<SupplierRefundResult>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<CreateSupplierRefundHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<Guid>> CreateWarrantyClaimAsync(
        CreateWarrantyClaimCommand command,
        CancellationToken cancellationToken = default)
    {
        var guard = CheckCanMutate<Guid>();
        if (!guard.IsSuccess)
        {
            return guard;
        }

        var handler = GetService<CreateWarrantyClaimHandler>();
        return await handler.HandleAsync(command, cancellationToken);
    }

    public async Task<Result<OperationStatusResult>> QueryOperationStatusAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var handler = GetService<OperationStatusQueryHandler>();
        return await handler.HandleAsync(new OperationStatusQuery(clientOperationId), cancellationToken);
    }
}
