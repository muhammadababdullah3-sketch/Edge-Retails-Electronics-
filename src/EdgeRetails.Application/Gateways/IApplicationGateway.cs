using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Gateways;

public sealed record ConnectivityStateChangedEventArgs(
    ConnectivityState PreviousState,
    ConnectivityState NewState,
    string? Reason = null);

public interface IApplicationGateway
{
    ConnectivityState CurrentState { get; }
    Guid? CurrentTerminalId { get; }
    bool CanMutate { get; }

    event EventHandler<ConnectivityStateChangedEventArgs>? ConnectivityChanged;

    void SetTerminalContext(Guid terminalId, string? clientAuthSecret = null);
    void UpdateConnectivityState(ConnectivityState newState, string? reason = null);

    // Terminal lifecycle
    Task<Result<RegisterTerminalResult>> RegisterTerminalAsync(
        RegisterTerminalCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<TerminalHeartbeatResult>> SendHeartbeatAsync(
        TerminalHeartbeatCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<UpdateTerminalStatusResult>> UpdateTerminalStatusAsync(
        UpdateTerminalStatusCommand command,
        CancellationToken cancellationToken = default);

    // Authoritative Reconnect Revalidation
    Task<Result<AuthoritativeRevalidationResult>> RevalidateAuthoritativeStateAsync(
        AuthoritativeRevalidationQuery query,
        CancellationToken cancellationToken = default);

    // Core Business Operations (routed through Gateway)
    Task<Result<CompleteSaleResult>> CompleteSaleAsync(
        CompleteSaleCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<CompleteSaleResult>> CompletePosDraftAsync(
        CompletePosDraftCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<CreateSaleReturnResult>> CreateSaleReturnAsync(
        CreateSaleReturnCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<CommercialExchangeResult>> ExecuteCommercialExchangeAsync(
        CommercialExchangeCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<CreatePurchaseResult>> CreatePurchaseAsync(
        CreatePurchaseCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<CreatePurchaseReturnResult>> CreatePurchaseReturnAsync(
        CreatePurchaseReturnCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<VoidPurchaseResult>> VoidPurchaseAsync(
        VoidPurchaseCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<SupplierPaymentResult>> CreateSupplierPaymentAsync(
        CreateSupplierPaymentCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<SupplierRefundResult>> CreateSupplierRefundAsync(
        CreateSupplierRefundCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<Guid>> CreateWarrantyClaimAsync(
        CreateWarrantyClaimCommand command,
        CancellationToken cancellationToken = default);

    Task<Result<OperationStatusResult>> QueryOperationStatusAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default);
}

public sealed record OperationStatusResult(
    Guid ClientOperationId,
    bool Found,
    string? OperationType,
    Guid? EntityId,
    string? DocumentNumber,
    bool WasCommitted);
