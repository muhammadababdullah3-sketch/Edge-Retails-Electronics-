using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Gateways;

public sealed class RemoteApplicationGateway : IApplicationGateway
{
    private readonly HttpClient _httpClient;
    private readonly ConnectivityStateMachine _stateMachine;
    private Guid? _currentTerminalId;
    private string? _clientAuthSecret;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public RemoteApplicationGateway(
        HttpClient httpClient,
        ConnectivityState initialState = ConnectivityState.Connected)
    {
        _httpClient = httpClient;
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

    private void ApplySecurityHeaders(HttpRequestMessage request)
    {
        if (_currentTerminalId.HasValue)
        {
            request.Headers.Add("X-Terminal-Id", _currentTerminalId.Value.ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(_clientAuthSecret))
        {
            request.Headers.Add("X-Terminal-Secret", _clientAuthSecret);
        }

        request.Headers.Add("X-Protocol-Version", TerminalProtocol.CurrentProtocolVersion);
    }

    private async Task<Result<TResult>> SendPostAsync<TRequest, TResult>(
        string endpoint,
        TRequest payload,
        bool isMutation,
        CancellationToken cancellationToken)
    {
        if (isMutation && !CanMutate)
        {
            return Result<TResult>.Failure(
                "network.offline_mutation_forbidden",
                $"Authoritative mutations are prohibited while in connectivity state '{CurrentState}'. Client must be Connected.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        ApplySecurityHeaders(request);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                try
                {
                    var parsedError = JsonSerializer.Deserialize<GatewayErrorResponse>(errorBody, JsonOptions);
                    if (parsedError is not null && !string.IsNullOrWhiteSpace(parsedError.Code))
                    {
                        return Result<TResult>.Failure(parsedError.Code, parsedError.Message ?? "Gateway request failed.");
                    }
                }
                catch
                {
                    // Ignore JSON deserialization error and fallback
                }

                return Result<TResult>.Failure(
                    $"http.{(int)response.StatusCode}",
                    string.IsNullOrWhiteSpace(errorBody) ? response.ReasonPhrase ?? "HTTP error" : errorBody);
            }

            var result = await response.Content.ReadFromJsonAsync<TResult>(JsonOptions, cancellationToken);
            if (result is null)
            {
                return Result<TResult>.Failure("gateway.empty_response", "Server returned an empty response body.");
            }

            return Result<TResult>.Success(result);
        }
        catch (HttpRequestException ex)
        {
            _stateMachine.RecordHeartbeatFailure(ex.Message);
            return Result<TResult>.Failure("network.communication_error", ex.Message);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _stateMachine.RecordHeartbeatFailure("Request timed out");
            return Result<TResult>.Failure("network.timeout", "Request timed out awaiting server response: " + ex.Message);
        }
    }

    public async Task<Result<RegisterTerminalResult>> RegisterTerminalAsync(
        RegisterTerminalCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<RegisterTerminalCommand, RegisterTerminalResult>(
            "/api/terminals/register",
            command,
            isMutation: false,
            cancellationToken);
    }

    public async Task<Result<TerminalHeartbeatResult>> SendHeartbeatAsync(
        TerminalHeartbeatCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await SendPostAsync<TerminalHeartbeatCommand, TerminalHeartbeatResult>(
            "/api/terminals/heartbeat",
            command,
            isMutation: false,
            cancellationToken);

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
        return await SendPostAsync<UpdateTerminalStatusCommand, UpdateTerminalStatusResult>(
            "/api/terminals/status",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<AuthoritativeRevalidationResult>> RevalidateAuthoritativeStateAsync(
        AuthoritativeRevalidationQuery query,
        CancellationToken cancellationToken = default)
    {
        var result = await SendPostAsync<AuthoritativeRevalidationQuery, AuthoritativeRevalidationResult>(
            "/api/terminals/revalidate",
            query,
            isMutation: false,
            cancellationToken);

        if (result.IsSuccess)
        {
            _stateMachine.TransitionTo(ConnectivityState.Connected, "Revalidation succeeded");
        }

        return result;
    }

    public async Task<Result<CompleteSaleResult>> CompleteSaleAsync(
        CompleteSaleCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<CompleteSaleCommand, CompleteSaleResult>(
            "/api/sales/complete",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<CompleteSaleResult>> CompletePosDraftAsync(
        CompletePosDraftCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<CompletePosDraftCommand, CompleteSaleResult>(
            "/api/sales/drafts/complete",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<CreateSaleReturnResult>> CreateSaleReturnAsync(
        CreateSaleReturnCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<CreateSaleReturnCommand, CreateSaleReturnResult>(
            "/api/sales/return",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<CommercialExchangeResult>> ExecuteCommercialExchangeAsync(
        CommercialExchangeCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<CommercialExchangeCommand, CommercialExchangeResult>(
            "/api/sales/exchange",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<CreatePurchaseResult>> CreatePurchaseAsync(
        CreatePurchaseCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<CreatePurchaseCommand, CreatePurchaseResult>(
            "/api/purchasing/create",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<CreatePurchaseReturnResult>> CreatePurchaseReturnAsync(
        CreatePurchaseReturnCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<CreatePurchaseReturnCommand, CreatePurchaseReturnResult>(
            "/api/purchasing/return",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<VoidPurchaseResult>> VoidPurchaseAsync(
        VoidPurchaseCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<VoidPurchaseCommand, VoidPurchaseResult>(
            "/api/purchasing/void",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<SupplierPaymentResult>> CreateSupplierPaymentAsync(
        CreateSupplierPaymentCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<CreateSupplierPaymentCommand, SupplierPaymentResult>(
            "/api/finance/supplier-payment",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<SupplierRefundResult>> CreateSupplierRefundAsync(
        CreateSupplierRefundCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<CreateSupplierRefundCommand, SupplierRefundResult>(
            "/api/finance/supplier-refund",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<Guid>> CreateWarrantyClaimAsync(
        CreateWarrantyClaimCommand command,
        CancellationToken cancellationToken = default)
    {
        return await SendPostAsync<CreateWarrantyClaimCommand, Guid>(
            "/api/warranty/claims",
            command,
            isMutation: true,
            cancellationToken);
    }

    public async Task<Result<OperationStatusResult>> QueryOperationStatusAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/system/operations/{clientOperationId}");
        ApplySecurityHeaders(request);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result<OperationStatusResult>.Failure(
                    $"http.{(int)response.StatusCode}",
                    response.ReasonPhrase ?? "HTTP error");
            }

            var result = await response.Content.ReadFromJsonAsync<OperationStatusResult>(JsonOptions, cancellationToken);
            if (result is null)
            {
                return Result<OperationStatusResult>.Failure("gateway.empty_response", "Server returned an empty response.");
            }

            return Result<OperationStatusResult>.Success(result);
        }
        catch (Exception ex)
        {
            return Result<OperationStatusResult>.Failure("network.communication_error", ex.Message);
        }
    }

    private sealed record GatewayErrorResponse(string? Code, string? Message);
}
