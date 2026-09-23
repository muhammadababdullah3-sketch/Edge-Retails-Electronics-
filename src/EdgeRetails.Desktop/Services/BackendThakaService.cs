using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Thaka;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendThakaMaterialCatalogItem(
    PosProductItemViewModel Product,
    decimal UnitCharge);

public sealed record BackendThakaWorkspaceSnapshot(
    ThakaProjectListItemViewModel Project,
    IReadOnlyList<MaterialLedgerEntry> Materials,
    IReadOnlyList<PaymentLedgerEntry> Payments);

public sealed record BackendThakaIssueResult(
    string ChallanNumber,
    decimal TotalCharge);

public sealed record BackendThakaPaymentResult(
    string ReceiptNumber,
    decimal BalanceAfter);

public sealed record BackendThakaProjectPage(
    IReadOnlyList<ThakaProjectListItemViewModel> Items,
    DateOnly? NextStartedOn,
    Guid? NextProjectId,
    bool HasMore,
    int TotalActiveCount,
    decimal TotalActiveMaterialValue,
    decimal TotalActiveBalance);

public interface IBackendThakaService
{
    Task<IReadOnlyList<ThakaProjectListItemViewModel>> GetProjectsAsync(
        CancellationToken cancellationToken = default);

    Task<BackendThakaProjectPage> GetProjectsPageAsync(
        string? search,
        string filter,
        int pageSize = 200,
        DateOnly? beforeStartedOn = null,
        Guid? beforeProjectId = null,
        CancellationToken cancellationToken = default);

    Task<BackendThakaWorkspaceSnapshot> GetWorkspaceAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);

    Task<ThakaProjectListItemViewModel> CreateProjectAsync(
        string customerName,
        string? phone,
        string projectName,
        string? location,
        string? note,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackendThakaMaterialCatalogItem>> GetMaterialCatalogAsync(
        CancellationToken cancellationToken = default);

    Task<BackendThakaIssueResult> IssueMaterialAsync(
        ThakaProjectListItemViewModel project,
        PosProductItemViewModel product,
        decimal quantity,
        IReadOnlyList<Guid> inventoryUnitIds,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);

    Task ReverseMaterialAsync(
        ThakaProjectListItemViewModel project,
        Guid materialIssueId,
        string reason,
        Guid clientOperationId,
        CancellationToken cancellationToken = default);

    Task<BackendThakaPaymentResult> RecordPaymentAsync(
        ThakaProjectListItemViewModel project,
        decimal amount,
        string paymentMethod,
        string? reference,
        CancellationToken cancellationToken = default);

    Task SettleAsync(
        ThakaProjectListItemViewModel project,
        decimal amount,
        string paymentMethod,
        CancellationToken cancellationToken = default);
}

public sealed class BackendThakaService : IBackendThakaService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<Guid?> _actorUserId;

    public BackendThakaService(
        IServiceScopeFactory scopeFactory,
        Func<Guid?> actorUserId)
    {
        _scopeFactory = scopeFactory;
        _actorUserId = actorUserId;
    }
    public async Task<IReadOnlyList<ThakaProjectListItemViewModel>> GetProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IThakaReadService>();
        var rows = await reads.GetProjectsAsync(cancellationToken);
        return rows.Select(Project).ToArray();
    }

    public async Task<BackendThakaProjectPage> GetProjectsPageAsync(
        string? search,
        string filter,
        int pageSize = 200,
        DateOnly? beforeStartedOn = null,
        Guid? beforeProjectId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IThakaReadService>();
        var status = filter.Trim().ToUpperInvariant() switch
        {
            "ACTIVE" => ThakaProjectStatus.Active,
            "SETTLED" => ThakaProjectStatus.Settled,
            _ => (ThakaProjectStatus?)null
        };
        var page = await reads.GetProjectsPageAsync(
            new ThakaProjectPageQuery(
                string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                status,
                Math.Clamp(pageSize, 1, 200),
                beforeStartedOn,
                beforeProjectId),
            cancellationToken);

        return new BackendThakaProjectPage(
            page.Items.Select(Project).ToArray(),
            page.NextStartedOn,
            page.NextProjectId,
            page.HasMore,
            page.TotalActiveCount,
            page.TotalActiveMaterialValue,
            page.TotalActiveBalance);
    }

    public async Task<BackendThakaWorkspaceSnapshot> GetWorkspaceAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IThakaReadService>();
        var detail = await reads.GetProjectAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("Thaka project was not found.");

        var materials = detail.Materials
            .Where(x => !x.IsReversed)
            .Select(x => new MaterialLedgerEntry
            {
                BackendMaterialIssueId = x.MaterialIssueId,
                Date = x.IssuedAt.LocalDateTime,
                ChallanNumber = x.ChallanNumber,
                ProductName = x.ProductName,
                Quantity = x.EnteredQuantity,
                Unit = x.UnitSymbol,
                Rate = x.UnitCharge,
                TotalValue = x.LineCharge
            })
            .ToArray();

        var payments = detail.Payments
            .Where(x => !x.IsReversed)
            .Select(x => new PaymentLedgerEntry
            {
                BackendPaymentId = x.PaymentId,
                ReceiptNumber = x.ReceiptNumber,
                Date = x.RecordedAt.LocalDateTime,
                PaymentMethod = x.PaymentMethod.ToString(),
                Amount = x.Amount,
                RecordedBy = $"User {x.RecordedBy.ToString("N")[..8]}",
                Reference = x.Reference ?? string.Empty
            })
            .ToArray();

        return new BackendThakaWorkspaceSnapshot(
            Project(detail.Project),
            materials,
            payments);
    }
    public async Task<ThakaProjectListItemViewModel> CreateProjectAsync(
        string customerName,
        string? phone,
        string projectName,
        string? location,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var customers = scope.ServiceProvider.GetRequiredService<GetCustomersHandler>();
        var saveCustomer = scope.ServiceProvider.GetRequiredService<SaveCustomerHandler>();
        var createProject = scope.ServiceProvider
            .GetRequiredService<CreateThakaProjectHandler>();
        var reads = scope.ServiceProvider.GetRequiredService<IThakaReadService>();

        var normalizedName = Required(customerName, "Customer name");
        var normalizedPhone = Normalize(phone);
        var rows = await customers.HandleAsync(false, cancellationToken);
        var nameMatches = rows
            .Where(x => !x.IsProtected &&
                        string.Equals(
                            x.Name.Trim(),
                            normalizedName,
                            StringComparison.OrdinalIgnoreCase))
            .ToArray(); PartyListItemDto? customer = null;
        if (normalizedPhone is not null)
        {
            var exact = nameMatches
                .Where(x => string.Equals(
                    Normalize(x.Phone),
                    normalizedPhone,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (exact.Length > 1)
            {
                throw new InvalidOperationException(
                    "Multiple backend customers match this name and phone.");
            }

            customer = exact.SingleOrDefault();
        }
        else if (nameMatches.Length == 1)
        {
            customer = nameMatches[0];
        }
        else if (nameMatches.Length > 1)
        {
            throw new InvalidOperationException(
                "Phone number is required to disambiguate this customer name.");
        }
        Guid customerId;
        if (customer is not null)
        {
            customerId = customer.Id;
        }
        else
        {
            var saved = await saveCustomer.HandleAsync(
                new SaveCustomerCommand(
                    null,
                    normalizedName,
                    normalizedPhone,
                    Normalize(location),
                    true,
                    actor,
                    Guid.CreateVersion7()),
                cancellationToken);

            if (!saved.IsSuccess || saved.Value == Guid.Empty)
            {
                throw new InvalidOperationException(
                    saved.Error?.Message ?? "Customer could not be created.");
            }

            customerId = saved.Value;
        }

        var created = await createProject.HandleAsync(
            new CreateThakaProjectCommand(
                customerId,
                Required(projectName, "Project name"),
                Normalize(location), Normalize(note),
                DateOnly.FromDateTime(DateTime.Today),
                actor,
                Guid.CreateVersion7()),
            cancellationToken);

        if (!created.IsSuccess || created.Value == Guid.Empty)
        {
            throw new InvalidOperationException(
                created.Error?.Message ?? "Thaka project could not be created.");
        }

        var detail = await reads.GetProjectAsync(created.Value, cancellationToken)
            ?? throw new InvalidOperationException(
                "Thaka project was committed but could not be read back.");

        return Project(detail.Project);
    }

    public async Task<IReadOnlyList<BackendThakaMaterialCatalogItem>>
        GetMaterialCatalogAsync(
            CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var reads = scope.ServiceProvider.GetRequiredService<IThakaReadService>();
        var rows = await reads.GetMaterialCatalogAsync(cancellationToken); return rows.Select(row =>
        {
            var product = new PosProductItemViewModel(
                id: row.ProductId.ToString("D"),
                name: row.Name,
                sku: row.Sku ?? string.Empty,
                brand: "—",
                category: "Thaka",
                stock: row.SellableStock,
                price: row.UnitCharge,
                unit: row.UnitSymbol,
                cost: 0m,
                minimumStock: 0m,
                backendProductId: row.ProductId,
                backendProductUnitId: row.ProductUnitId,
                isSerialized: row.IsSerialized);

            return new BackendThakaMaterialCatalogItem(
                product,
                row.UnitCharge);
        }).ToArray();
    }

    public async Task<BackendThakaIssueResult> IssueMaterialAsync(
        ThakaProjectListItemViewModel project,
        PosProductItemViewModel product,
        decimal quantity,
        IReadOnlyList<Guid> inventoryUnitIds,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        var projectId = RequireProject(project);
        var productId = product.BackendProductId
            ?? throw new BackendOperationException(
                "thaka.product_not_attached",
                "Product is not attached to the backend.");
        var productUnitId = product.BackendProductUnitId
            ?? throw new BackendOperationException(
                "thaka.product_unit_not_attached",
                "Thaka unit is not attached to the backend.");

        if (clientOperationId == Guid.Empty)
        {
            throw new BackendOperationException(
                "thaka.operation_id_required",
                "Thaka issue operation id is required.");
        }

        if (product.IsSerialized)
        {
            var baseQuantity = quantity * product.FactorToBaseUnit;
            if (baseQuantity != decimal.Truncate(baseQuantity) ||
                inventoryUnitIds.Count != decimal.ToInt32(baseQuantity))
            {
                throw new BackendOperationException(
                    "thaka.exact_unit_count_mismatch",
                    "Serialized Thaka issue requires one eligible exact unit per base quantity.");
            }
        }
        else if (inventoryUnitIds.Count > 0)
        {
            throw new BackendOperationException(
                "thaka.exact_unit_unexpected",
                "Quantity-tracked Thaka material must not submit exact-unit identities.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<IssueThakaMaterialHandler>();
        var result = await handler.HandleAsync(
            new IssueThakaMaterialCommand(
                clientOperationId,
                projectId,
                actor,
                null,
                [
                    new IssueThakaMaterialLineInput(
                        productId,
                        productUnitId,
                        quantity,
                        product.Price,
                        inventoryUnitIds)
                ]),
            cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            throw new BackendOperationException(
                result.Error?.Code ?? "thaka.issue_failed",
                result.Error?.Message ?? "Material issue failed.");
        }

        return new BackendThakaIssueResult(
            result.Value.ChallanNumber,
            result.Value.TotalCharge);
    }

    public async Task ReverseMaterialAsync(
        ThakaProjectListItemViewModel project,
        Guid materialIssueId,
        string reason,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        var projectId = RequireProject(project);
        if (materialIssueId == Guid.Empty || clientOperationId == Guid.Empty)
        {
            throw new BackendOperationException(
                "thaka.material_reversal_invalid",
                "Material issue and operation identity are required.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<ReverseThakaMaterialHandler>();
        var result = await handler.HandleAsync(
            new ReverseThakaMaterialCommand(
                clientOperationId,
                projectId,
                materialIssueId,
                string.IsNullOrWhiteSpace(reason)
                    ? "Operator material reversal"
                    : reason.Trim(),
                actor),
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new BackendOperationException(
                result.Error?.Code ?? "thaka.material_reversal_failed",
                result.Error?.Message ?? "Material reversal failed.");
        }
    }

    public async Task<BackendThakaPaymentResult> RecordPaymentAsync(
        ThakaProjectListItemViewModel project,
        decimal amount,
        string paymentMethod,
        string? reference,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        var projectId = RequireProject(project);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<RecordThakaPaymentHandler>(); var result = await handler.HandleAsync(
            new RecordThakaPaymentCommand(
                Guid.CreateVersion7(),
                projectId,
                amount,
                ParsePaymentMethod(paymentMethod),
                Normalize(reference),
                null,
                actor),
            cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Thaka payment failed.");
        }

        return new BackendThakaPaymentResult(
            result.Value.ReceiptNumber,
            result.Value.BalanceAfter);
    }

    public async Task SettleAsync(
        ThakaProjectListItemViewModel project,
        decimal amount,
        string paymentMethod,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireActor();
        var projectId = RequireProject(project); await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SettleThakaHandler>();
        var result = await handler.HandleAsync(
            new SettleThakaCommand(
                Guid.CreateVersion7(),
                projectId,
                0m,
                amount,
                ParsePaymentMethod(paymentMethod),
                null,
                actor),
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "Thaka settlement failed.");
        }
    }

    private Guid RequireActor() =>
        _actorUserId()
        ?? throw new InvalidOperationException(
            "A persistent backend user session is required for this operation.");

    private static Guid RequireProject(ThakaProjectListItemViewModel project) =>
        project.BackendProjectId
        ?? throw new InvalidOperationException(
            "Thaka project is not attached to the backend."); private static ThakaPaymentMethod ParsePaymentMethod(string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "CASH" => ThakaPaymentMethod.Cash,
            "BANK" => ThakaPaymentMethod.Bank,
            "OTHER" => ThakaPaymentMethod.Other,
            _ => throw new InvalidOperationException(
                "Unsupported Thaka payment method.")
        };

    private static string Required(string value, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{field} is required.");
        }

        return normalized;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
    private static ThakaProjectListItemViewModel Project(
        ThakaProjectSummaryDto row) =>
        new(
            id: row.ProjectNumber,
            projectName: row.ProjectName,
            customerName: row.CustomerName,
            phone: row.CustomerPhone ?? "N/A",
            location: row.SiteAddress ?? "N/A",
            startDate: row.StartedOn.ToDateTime(TimeOnly.MinValue),
            materialValue: row.MaterialValue,
            paid: row.Paid,
            status: row.Status == ThakaProjectStatus.Settled
                ? "SETTLED"
                : "ACTIVE",
            notes: row.Note ?? string.Empty,
            backendProjectId: row.ProjectId,
            settlementDiscount: row.SettlementDiscount);
}
