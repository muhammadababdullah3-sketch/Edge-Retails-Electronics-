using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Application.Gateways;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Thaka;

namespace EdgeRetails.Desktop.Services;

/// <summary>Thaka project, inventory, payment, and settlement operations through the local Server.</summary>
public sealed class RemoteBackendThakaService(
    DesktopApiClient apiClient,
    IClientOperationIntentStore? operationIntents = null) : IBackendThakaService
{
    private readonly IClientOperationIntentStore _operationIntents = operationIntents ?? new FileClientOperationIntentStore();

    public async Task<IReadOnlyList<ThakaProjectListItemViewModel>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        (await GetProjectsPageAsync(null, "ALL", 200, cancellationToken: cancellationToken)).Items;

    public async Task<BackendThakaProjectPage> GetProjectsPageAsync(string? search, string filter, int pageSize = 200,
        DateOnly? beforeStartedOn = null, Guid? beforeProjectId = null, CancellationToken cancellationToken = default)
    {
        var status = filter.Trim().ToUpperInvariant() switch { "ACTIVE" => "Active", "SETTLED" => "Settled", _ => string.Empty };
        var query = $"/api/thaka/projects?search={Uri.EscapeDataString(search ?? string.Empty)}&status={status}&pageSize={Math.Clamp(pageSize, 1, 200)}";
        if (beforeStartedOn.HasValue && beforeProjectId.HasValue)
        {
            query += $"&beforeStartedOn={beforeStartedOn:yyyy-MM-dd}&beforeProjectId={beforeProjectId:D}";
        }

        var page = await apiClient.GetAsync<ThakaProjectPageDto>(query, cancellationToken);
        return new([.. page.Items.Select(MapProject)], page.NextStartedOn, page.NextProjectId, page.HasMore,
            page.TotalActiveCount, page.TotalActiveMaterialValue, page.TotalActiveBalance);
    }

    public async Task<BackendThakaWorkspaceSnapshot> GetWorkspaceAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var detail = await apiClient.GetAsync<ThakaProjectDetailDto>($"/api/thaka/projects/{projectId:D}", cancellationToken);
        return new(MapProject(detail.Project),
            [.. detail.Materials.Where(x => !x.IsReversed).Select(x => new MaterialLedgerEntry
            {
                BackendMaterialIssueId = x.MaterialIssueId, Date = x.IssuedAt.LocalDateTime, ChallanNumber = x.ChallanNumber,
                ProductName = x.ProductName, Quantity = x.EnteredQuantity, Unit = x.UnitSymbol, Rate = x.UnitCharge, TotalValue = x.LineCharge
            })],
            [.. detail.Payments.Where(x => !x.IsReversed).Select(x => new PaymentLedgerEntry
            {
                BackendPaymentId = x.PaymentId, ReceiptNumber = x.ReceiptNumber, Date = x.RecordedAt.LocalDateTime,
                PaymentMethod = x.PaymentMethod.ToString(), Amount = x.Amount, RecordedBy = $"User {x.RecordedBy.ToString("N")[..8]}",
                Reference = x.Reference ?? string.Empty
            })]);
    }

    public async Task<ThakaProjectListItemViewModel> CreateProjectAsync(string customerName, string? phone, string projectName,
        string? location, string? note, CancellationToken cancellationToken = default)
    {
        var normalizedName = Required(customerName, "Customer name");
        var normalizedProjectName = Required(projectName, "Project name");
        var phoneNormalized = Normalize(phone);
        var customers = await apiClient.GetAsync<IReadOnlyList<CustomerDirectoryDto>>(
            $"/api/customers?search={Uri.EscapeDataString(normalizedName)}&pageSize=200", cancellationToken);
        var matches = customers.Where(x => string.Equals(x.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase) &&
            (phoneNormalized is null || string.Equals(Normalize(x.Phone), phoneNormalized, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException("Multiple backend customers match this name and phone.");
        }

        var customerId = matches.SingleOrDefault()?.CustomerId;
        if (!customerId.HasValue)
        {
            var customerOperationKey = $"thaka:create-project-customer:{HashOperationKey($"{normalizedName.ToUpperInvariant()}|{phoneNormalized?.ToUpperInvariant()}")}";
            var customerPayload = $"{normalizedName}|{phoneNormalized}|{Normalize(location)}";
            var customerOperationId = GetOperationId(customerOperationKey, customerPayload);
            var customerRequest = new SaveCustomerRequest(normalizedName, phoneNormalized, Normalize(location), true,
                CorrelationId: customerOperationId, ClientOperationId: customerOperationId);
            try
            {
                customerId = await apiClient.PostAsync<SaveCustomerRequest, Guid>("/api/customers", customerRequest, cancellationToken);
            }
            catch (DesktopApiException ex) when (IsDefinitive(ex.StatusCode))
            {
                _operationIntents.Complete(customerOperationKey, customerOperationId);
                throw;
            }
            catch (DesktopApiException ex) when (IsAmbiguous(ex))
            {
                var outcome = await QueryOperationAsync(customerOperationId, cancellationToken);
                if (outcome is { WasCommitted: true, OperationType: "Customer.Save.v1", EntityId: Guid savedCustomerId })
                {
                    customerId = savedCustomerId;
                }
                else if (outcome is { EffectiveStatus: "NotFound" })
                {
                    try
                    {
                        customerId = await apiClient.PostAsync<SaveCustomerRequest, Guid>("/api/customers", customerRequest, cancellationToken);
                    }
                    catch (DesktopApiException definitiveReplayException) when (IsDefinitive(definitiveReplayException.StatusCode))
                    {
                        _operationIntents.Complete(customerOperationKey, customerOperationId);
                        throw;
                    }
                    catch (DesktopApiException ambiguousReplayException) when (IsAmbiguous(ambiguousReplayException))
                    {
                        throw UnknownOutcome("Customer", customerOperationId);
                    }
                }
                else
                {
                    throw UnknownOutcome("Customer", customerOperationId);
                }
            }

            // A confirmed customer create is safely discoverable by the preceding name/phone read.
            _operationIntents.Complete(customerOperationKey, customerOperationId);
        }

        var startedOn = DateOnly.FromDateTime(DateTime.Today);
        var projectPayload = FormattableString.Invariant(
            $"{customerId.Value:D}|{normalizedProjectName}|{Normalize(location)}|{Normalize(note)}|{startedOn:yyyy-MM-dd}");
        var projectOperationKey = "thaka:create-project";
        var projectOperationId = GetOperationId(projectOperationKey, projectPayload);
        Guid createdId;
        var projectRequest = new CreateProjectRequest(customerId.Value, normalizedProjectName, Normalize(location), Normalize(note),
            startedOn, CorrelationId: projectOperationId, ClientOperationId: projectOperationId);
        try
        {
            createdId = await apiClient.PostAsync<CreateProjectRequest, Guid>("/api/thaka/projects", projectRequest, cancellationToken);
        }
        catch (DesktopApiException ex) when (IsDefinitive(ex.StatusCode))
        {
            _operationIntents.Complete(projectOperationKey, projectOperationId);
            throw;
        }
        catch (DesktopApiException ex) when (IsAmbiguous(ex))
        {
            var outcome = await QueryOperationAsync(projectOperationId, cancellationToken);
            if (outcome is { WasCommitted: true, OperationType: "CreateThakaProject", EntityId: Guid savedProjectId })
            {
                createdId = savedProjectId;
            }
            else if (outcome is { EffectiveStatus: "NotFound" })
            {
                try
                {
                    createdId = await apiClient.PostAsync<CreateProjectRequest, Guid>("/api/thaka/projects", projectRequest, cancellationToken);
                }
                catch (DesktopApiException definitiveReplayException) when (IsDefinitive(definitiveReplayException.StatusCode))
                {
                    _operationIntents.Complete(projectOperationKey, projectOperationId);
                    throw;
                }
                catch (DesktopApiException ambiguousReplayException) when (IsAmbiguous(ambiguousReplayException))
                {
                    throw UnknownOutcome("Thaka project", projectOperationId);
                }
            }
            else
            {
                throw UnknownOutcome("Thaka project", projectOperationId);
            }
        }
        var detail = await apiClient.GetAsync<ThakaProjectDetailDto>($"/api/thaka/projects/{createdId:D}", cancellationToken);
        _operationIntents.Complete(projectOperationKey, projectOperationId);
        return MapProject(detail.Project);
    }

    public async Task<IReadOnlyList<BackendThakaMaterialCatalogItem>> GetMaterialCatalogAsync(CancellationToken cancellationToken = default)
    {
        var rows = await apiClient.GetAsync<IReadOnlyList<ThakaCatalogItemDto>>("/api/thaka/catalog", cancellationToken);
        return [.. rows.Select(row => new BackendThakaMaterialCatalogItem(
            new PosProductItemViewModel(row.ProductId.ToString("D"), row.Name, row.Sku ?? string.Empty, "—", "Thaka",
                row.SellableStock, row.UnitCharge, unit: row.UnitSymbol,
                backendProductId: row.ProductId, backendProductUnitId: row.ProductUnitId,
                isSerialized: row.IsSerialized),
            row.UnitCharge))];
    }

    public async Task<BackendThakaIssueResult> IssueMaterialAsync(ThakaProjectListItemViewModel project, PosProductItemViewModel product,
        decimal quantity, IReadOnlyList<Guid> inventoryUnitIds, Guid clientOperationId, CancellationToken cancellationToken = default)
    {
        if (clientOperationId == Guid.Empty)
        {
            throw new BackendOperationException("thaka.operation_id_required", "Thaka issue operation id is required.");
        }

        var productId = product.BackendProductId ?? throw new BackendOperationException("thaka.product_not_attached", "Product is not attached to backend.");
        var unitId = product.BackendProductUnitId ?? throw new BackendOperationException("thaka.product_unit_not_attached", "Product unit is not attached to backend.");
        if (product.IsSerialized && (quantity * product.FactorToBaseUnit != inventoryUnitIds.Count || inventoryUnitIds.Distinct().Count() != inventoryUnitIds.Count))
        {
            throw new BackendOperationException("thaka.exact_unit_count_mismatch", "Serialized Thaka issue requires one exact unit per base quantity.");
        }

        if (!product.IsSerialized && inventoryUnitIds.Count != 0)
        {
            throw new BackendOperationException("thaka.exact_unit_unexpected", "Quantity-tracked material must not submit exact-unit identities.");
        }

        var result = await apiClient.PostAsync<IssueRequest, IssueThakaMaterialResult>("/api/thaka/material-issue",
            new(clientOperationId, RequireProject(project), [new(productId, unitId, quantity, product.Price, inventoryUnitIds)]), cancellationToken);
        return new(result.ChallanNumber, result.TotalCharge);
    }

    public async Task ReverseMaterialAsync(ThakaProjectListItemViewModel project, Guid materialIssueId, string reason,
        Guid clientOperationId, CancellationToken cancellationToken = default) =>
        _ = await apiClient.PostAsync<MaterialReversalRequest, ReverseThakaMaterialResult>("/api/thaka/material-reversal",
            new(clientOperationId, RequireProject(project), materialIssueId, Required(reason, "Reason")), cancellationToken);

    public async Task<BackendThakaPaymentResult> RecordPaymentAsync(ThakaProjectListItemViewModel project, decimal amount,
        string paymentMethod, string? reference, CancellationToken cancellationToken = default)
    {
        var projectId = RequireProject(project);
        var parsedMethod = ParsePaymentMethod(paymentMethod);
        var payload = FormattableString.Invariant(
            $"{projectId:D}|{amount:G29}|{parsedMethod}|{Normalize(reference)}");
        var operationKey = $"thaka:payment:{projectId:D}";
        var operationId = GetOperationId(operationKey, payload);
        try
        {
            var result = await apiClient.PostAsync<PaymentRequest, RecordThakaPaymentResult>("/api/thaka/payments",
                new(operationId, projectId, amount, parsedMethod, Normalize(reference)), cancellationToken);
            _operationIntents.Complete(operationKey, operationId);
            return new(result.ReceiptNumber, result.BalanceAfter);
        }
        catch (DesktopApiException ex) when (IsDefinitive(ex.StatusCode))
        {
            _operationIntents.Complete(operationKey, operationId);
            throw;
        }
    }

    public async Task SettleAsync(ThakaProjectListItemViewModel project, decimal amount, string paymentMethod,
        CancellationToken cancellationToken = default)
    {
        var projectId = RequireProject(project);
        var parsedMethod = ParsePaymentMethod(paymentMethod);
        var payload = FormattableString.Invariant($"{projectId:D}|{amount:G29}|{parsedMethod}");
        var operationKey = $"thaka:settlement:{projectId:D}";
        var operationId = GetOperationId(operationKey, payload);
        try
        {
            await apiClient.PostAsync<SettlementRequest, SettleThakaResult>("/api/thaka/settlement",
                new(operationId, projectId, 0m, amount, parsedMethod), cancellationToken);
            _operationIntents.Complete(operationKey, operationId);
        }
        catch (DesktopApiException ex) when (IsDefinitive(ex.StatusCode))
        {
            _operationIntents.Complete(operationKey, operationId);
            throw;
        }
    }

    private Guid GetOperationId(string key, string payload)
    {
        try
        {
            return _operationIntents.GetOrCreate(key, payload);
        }
        catch (InvalidOperationException)
        {
            throw new BackendOperationException("thaka.operation_outcome_unknown",
                "The previous Thaka result is unknown. Retry the same operation or reconcile its status before changing the request.");
        }
    }

    private Task<OperationStatusResult?> QueryOperationAsync(Guid id, CancellationToken cancellationToken) =>
        apiClient.GetAsync<OperationStatusResult?>($"/api/system/operations/{id:D}", cancellationToken);

    private static bool IsAmbiguous(DesktopApiException ex) =>
        ex.Code.StartsWith("network.", StringComparison.OrdinalIgnoreCase) ||
        ex.Code.StartsWith("gateway.communication", StringComparison.OrdinalIgnoreCase) ||
        ex.StatusCode >= System.Net.HttpStatusCode.InternalServerError;

    private static BackendOperationException UnknownOutcome(string name, Guid id) =>
        new("thaka.operation_outcome_unknown", $"{name} status is not confirmed. Retry with the same operation id {id:D}.");

    private static bool IsDefinitive(System.Net.HttpStatusCode? status) => status is >= System.Net.HttpStatusCode.BadRequest and < System.Net.HttpStatusCode.InternalServerError;
    private static Guid RequireProject(ThakaProjectListItemViewModel project) => project.BackendProjectId ?? throw new BackendOperationException("thaka.project_not_attached", "Project is not attached to the backend.");
    private static ThakaPaymentMethod ParsePaymentMethod(string value) => value.Trim().ToUpperInvariant() switch { "CASH" => ThakaPaymentMethod.Cash, "BANK" => ThakaPaymentMethod.Bank, "OTHER" => ThakaPaymentMethod.Other, _ => throw new InvalidOperationException("Unsupported Thaka payment method.") };
    private static string Required(string value, string field) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{field} is required.") : value.Trim();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string HashOperationKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ThakaProjectListItemViewModel MapProject(ThakaProjectSummaryDto row) => new(row.ProjectNumber, row.ProjectName,
        row.CustomerName, row.CustomerPhone ?? "N/A", row.SiteAddress ?? "N/A", row.StartedOn.ToDateTime(TimeOnly.MinValue),
        row.MaterialValue, row.Paid, row.Status == ThakaProjectStatus.Settled ? "SETTLED" : "ACTIVE", row.Note ?? string.Empty,
        backendProjectId: row.ProjectId, settlementDiscount: row.SettlementDiscount);

    private sealed record SaveCustomerRequest(string Name, string? Phone, string? Address, bool IsActive, string? Notes = null, Guid? ActorId = null, Guid? CorrelationId = null, Guid? ClientOperationId = null);
    private sealed record CreateProjectRequest(Guid CustomerId, string ProjectName, string? SiteAddress, string? Note, DateOnly StartedOn, Guid? ActorId = null, Guid? CorrelationId = null, Guid? ClientOperationId = null);
    private sealed record IssueRequest(Guid ClientOperationId, Guid ProjectId, IReadOnlyList<IssueThakaMaterialLineInput> Lines, string? Note = null);
    private sealed record PaymentRequest(Guid ClientOperationId, Guid ProjectId, decimal Amount, ThakaPaymentMethod PaymentMethod, string? Reference, string? Note = null);
    private sealed record SettlementRequest(Guid ClientOperationId, Guid ProjectId, decimal SettlementDiscount, decimal FinalPaymentAmount, ThakaPaymentMethod PaymentMethod, string? PaymentReference = null);
    private sealed record MaterialReversalRequest(Guid ClientOperationId, Guid ProjectId, Guid MaterialIssueId, string Reason);
}
