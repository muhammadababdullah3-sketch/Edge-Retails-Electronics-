using System.Data;
using Dapper;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class ThakaReadService : IThakaReadService
{
    private readonly EdgeRetailsDbContext _db;

    public ThakaReadService(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<ThakaProjectSummaryDto>> GetProjectsAsync(
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            const string sql = """
                WITH issued AS (
                    SELECT project_id, sum(total_charge) AS amount
                    FROM thaka.material_issues
                    GROUP BY project_id
                ),
                material_reversed AS (
                    SELECT project_id, sum(reversed_charge) AS amount
                    FROM thaka.material_reversals
                    GROUP BY project_id
                ),
                paid AS (
                    SELECT project_id, sum(amount) AS amount
                    FROM thaka.payments
                    GROUP BY project_id
                ),
                payment_reversed AS (
                    SELECT project_id, sum(amount) AS amount
                    FROM thaka.payment_reversals
                    GROUP BY project_id
                ),
                discounts AS (
                    SELECT project_id, sum(settlement_discount) AS amount
                    FROM thaka.settlements
                    GROUP BY project_id
                )
                SELECT
                    p.id AS ProjectId,
                    p.project_number AS ProjectNumber,
                    p.customer_id AS CustomerId,
                    p.project_name AS ProjectName,
                    c.name AS CustomerName,
                    c.phone AS CustomerPhone,
                    p.site_address AS SiteAddress,
                    p.started_on AS StartedOn,
                    p.status AS Status,
                    COALESCE(i.amount, 0) - COALESCE(mr.amount, 0) AS MaterialValue,
                    COALESCE(pd.amount, 0) - COALESCE(pr.amount, 0) AS Paid,
                    COALESCE(d.amount, 0) AS SettlementDiscount,
                    GREATEST(
                        0,
                        COALESCE(i.amount, 0) - COALESCE(mr.amount, 0)
                        - COALESCE(d.amount, 0)
                        - (COALESCE(pd.amount, 0) - COALESCE(pr.amount, 0))
                    ) AS Balance,
                    p.note AS Note
                FROM thaka.projects p
                INNER JOIN parties.customers c ON c.id = p.customer_id
                LEFT JOIN issued i ON i.project_id = p.id
                LEFT JOIN material_reversed mr ON mr.project_id = p.id
                LEFT JOIN paid pd ON pd.project_id = p.id
                LEFT JOIN payment_reversed pr ON pr.project_id = p.id
                LEFT JOIN discounts d ON d.project_id = p.id
                ORDER BY p.started_on DESC, p.id DESC;
                """;

            var rows = await connection.QueryAsync<ThakaProjectSummaryDto>(
                new CommandDefinition(
                    sql,
                    cancellationToken: cancellationToken));
            return (IReadOnlyList<ThakaProjectSummaryDto>)rows.ToArray();
        }, cancellationToken);

    public Task<ThakaProjectPageDto> GetProjectsPageAsync(
        ThakaProjectPageQuery request,
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            var take = Math.Clamp(request.PageSize, 1, 200);
            const string sql = """
                WITH paged_projects AS (
                    SELECT
                        p.id AS project_id,
                        p.project_number,
                        p.customer_id,
                        p.project_name,
                        c.name AS customer_name,
                        c.phone AS customer_phone,
                        p.site_address,
                        p.started_on,
                        p.status,
                        p.note
                    FROM thaka.projects p
                    INNER JOIN parties.customers c ON c.id = p.customer_id
                    WHERE (
                        cast(@Search as text) IS NULL
                        OR p.project_number ILIKE '%' || cast(@Search as text) || '%'
                        OR p.project_name ILIKE '%' || cast(@Search as text) || '%'
                        OR c.name ILIKE '%' || cast(@Search as text) || '%'
                        OR COALESCE(c.phone, '') ILIKE '%' || cast(@Search as text) || '%'
                    )
                    AND (cast(@Status as integer) IS NULL OR p.status = cast(@Status as integer))
                    AND (
                        cast(@BeforeStartedOn as date) IS NULL
                        OR p.started_on < cast(@BeforeStartedOn as date)
                        OR (p.started_on = cast(@BeforeStartedOn as date) AND p.id < cast(@BeforeProjectId as uuid))
                    )
                    ORDER BY p.started_on DESC, p.id DESC
                    LIMIT @TakePlusOne
                ),
                issued AS (
                    SELECT project_id, sum(total_charge) AS amount
                    FROM thaka.material_issues
                    WHERE project_id IN (SELECT project_id FROM paged_projects)
                    GROUP BY project_id
                ),
                material_reversed AS (
                    SELECT project_id, sum(reversed_charge) AS amount
                    FROM thaka.material_reversals
                    WHERE project_id IN (SELECT project_id FROM paged_projects)
                    GROUP BY project_id
                ),
                paid AS (
                    SELECT project_id, sum(amount) AS amount
                    FROM thaka.payments
                    WHERE project_id IN (SELECT project_id FROM paged_projects)
                    GROUP BY project_id
                ),
                payment_reversed AS (
                    SELECT project_id, sum(amount) AS amount
                    FROM thaka.payment_reversals
                    WHERE project_id IN (SELECT project_id FROM paged_projects)
                    GROUP BY project_id
                ),
                discounts AS (
                    SELECT project_id, sum(settlement_discount) AS amount
                    FROM thaka.settlements
                    WHERE project_id IN (SELECT project_id FROM paged_projects)
                    GROUP BY project_id
                ),
                all_issued AS (
                    SELECT project_id, sum(total_charge) AS amount
                    FROM thaka.material_issues
                    GROUP BY project_id
                ),
                all_material_reversed AS (
                    SELECT project_id, sum(reversed_charge) AS amount
                    FROM thaka.material_reversals
                    GROUP BY project_id
                ),
                all_paid AS (
                    SELECT project_id, sum(amount) AS amount
                    FROM thaka.payments
                    GROUP BY project_id
                ),
                all_payment_reversed AS (
                    SELECT project_id, sum(amount) AS amount
                    FROM thaka.payment_reversals
                    GROUP BY project_id
                ),
                all_discounts AS (
                    SELECT project_id, sum(settlement_discount) AS amount
                    FROM thaka.settlements
                    GROUP BY project_id
                ),
                active_totals AS (
                    SELECT
                        count(*)::int AS total_active_count,
                        COALESCE(sum(COALESCE(i.amount, 0) - COALESCE(mr.amount, 0)), 0) AS total_active_material,
                        COALESCE(sum(GREATEST(
                            0,
                            COALESCE(i.amount, 0) - COALESCE(mr.amount, 0)
                            - COALESCE(d.amount, 0)
                            - (COALESCE(pd.amount, 0) - COALESCE(pr.amount, 0))
                        )), 0) AS total_active_balance
                    FROM thaka.projects p
                    LEFT JOIN all_issued i ON i.project_id = p.id
                    LEFT JOIN all_material_reversed mr ON mr.project_id = p.id
                    LEFT JOIN all_paid pd ON pd.project_id = p.id
                    LEFT JOIN all_payment_reversed pr ON pr.project_id = p.id
                    LEFT JOIN all_discounts d ON d.project_id = p.id
                    WHERE p.status = 1
                )
                SELECT
                    p.project_id AS ProjectId,
                    p.project_number AS ProjectNumber,
                    p.customer_id AS CustomerId,
                    p.project_name AS ProjectName,
                    p.customer_name AS CustomerName,
                    p.customer_phone AS CustomerPhone,
                    p.site_address AS SiteAddress,
                    p.started_on AS StartedOn,
                    p.status AS Status,
                    COALESCE(i.amount, 0) - COALESCE(mr.amount, 0) AS MaterialValue,
                    COALESCE(pd.amount, 0) - COALESCE(pr.amount, 0) AS Paid,
                    COALESCE(d.amount, 0) AS SettlementDiscount,
                    GREATEST(
                        0,
                        COALESCE(i.amount, 0) - COALESCE(mr.amount, 0)
                        - COALESCE(d.amount, 0)
                        - (COALESCE(pd.amount, 0) - COALESCE(pr.amount, 0))
                    ) AS Balance,
                    p.note AS Note,
                    a.total_active_count AS TotalActiveCount,
                    a.total_active_material AS TotalActiveMaterialValue,
                    a.total_active_balance AS TotalActiveBalance
                FROM paged_projects p
                LEFT JOIN issued i ON i.project_id = p.project_id
                LEFT JOIN material_reversed mr ON mr.project_id = p.project_id
                LEFT JOIN paid pd ON pd.project_id = p.project_id
                LEFT JOIN payment_reversed pr ON pr.project_id = p.project_id
                LEFT JOIN discounts d ON d.project_id = p.project_id
                CROSS JOIN active_totals a
                ORDER BY p.started_on DESC, p.project_id DESC;
                """;

            var rows = (await connection.QueryAsync<ThakaProjectPageRow>(
                new CommandDefinition(
                    sql,
                    new
                    {
                        Search = string.IsNullOrWhiteSpace(request.Search) ? null : request.Search.Trim(),
                        Status = request.Status,
                        TakePlusOne = take + 1,
                        request.BeforeStartedOn,
                        request.BeforeProjectId
                    },
                    cancellationToken: cancellationToken))).ToArray();

            var hasMore = rows.Length > take;
            var pageRows = hasMore ? rows[..take] : rows;
            var items = pageRows.Select(x => new ThakaProjectSummaryDto(
                x.ProjectId,
                x.ProjectNumber,
                x.CustomerId,
                x.ProjectName,
                x.CustomerName,
                x.CustomerPhone,
                x.SiteAddress,
                x.StartedOn,
                x.Status,
                x.MaterialValue,
                x.Paid,
                x.SettlementDiscount,
                x.Balance,
                x.Note)).ToArray();
            var last = items.LastOrDefault();
            var totals = pageRows.FirstOrDefault();

            return new ThakaProjectPageDto(
                items,
                hasMore ? last?.StartedOn : null,
                hasMore ? last?.ProjectId : null,
                hasMore,
                totals?.TotalActiveCount ?? 0,
                totals?.TotalActiveMaterialValue ?? 0m,
                totals?.TotalActiveBalance ?? 0m);
        }, cancellationToken);

    public async Task<ThakaProjectDetailDto?> GetProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        return await WithConnectionAsync(async connection =>
        {
            const string projectSql = """
                WITH issued AS (
                    SELECT project_id, sum(total_charge) AS amount
                    FROM thaka.material_issues
                    WHERE project_id = @ProjectId
                    GROUP BY project_id
                ),
                material_reversed AS (
                    SELECT project_id, sum(reversed_charge) AS amount
                    FROM thaka.material_reversals
                    WHERE project_id = @ProjectId
                    GROUP BY project_id
                ),
                paid AS (
                    SELECT project_id, sum(amount) AS amount
                    FROM thaka.payments
                    WHERE project_id = @ProjectId
                    GROUP BY project_id
                ),
                payment_reversed AS (
                    SELECT project_id, sum(amount) AS amount
                    FROM thaka.payment_reversals
                    WHERE project_id = @ProjectId
                    GROUP BY project_id
                ),
                discounts AS (
                    SELECT project_id, sum(settlement_discount) AS amount
                    FROM thaka.settlements
                    WHERE project_id = @ProjectId
                    GROUP BY project_id
                )
                SELECT
                    p.id AS ProjectId,
                    p.project_number AS ProjectNumber,
                    p.customer_id AS CustomerId,
                    p.project_name AS ProjectName,
                    c.name AS CustomerName,
                    c.phone AS CustomerPhone,
                    p.site_address AS SiteAddress,
                    p.started_on AS StartedOn,
                    p.status AS Status,
                    COALESCE(i.amount, 0) - COALESCE(mr.amount, 0) AS MaterialValue,
                    COALESCE(pd.amount, 0) - COALESCE(pr.amount, 0) AS Paid,
                    COALESCE(d.amount, 0) AS SettlementDiscount,
                    GREATEST(
                        0,
                        COALESCE(i.amount, 0) - COALESCE(mr.amount, 0)
                        - COALESCE(d.amount, 0)
                        - (COALESCE(pd.amount, 0) - COALESCE(pr.amount, 0))
                    ) AS Balance,
                    p.note AS Note
                FROM thaka.projects p
                INNER JOIN parties.customers c ON c.id = p.customer_id
                LEFT JOIN issued i ON i.project_id = p.id
                LEFT JOIN material_reversed mr ON mr.project_id = p.id
                LEFT JOIN paid pd ON pd.project_id = p.id
                LEFT JOIN payment_reversed pr ON pr.project_id = p.id
                LEFT JOIN discounts d ON d.project_id = p.id
                WHERE p.id = @ProjectId
                LIMIT 1;
                """;

            var project = await connection.QuerySingleOrDefaultAsync<ThakaProjectSummaryDto>(
                new CommandDefinition(
                    projectSql,
                    new { ProjectId = projectId },
                    cancellationToken: cancellationToken));
            if (project is null)
            {
                return null;
            }

            const string materialsSql = """
                SELECT
                    mi.id AS MaterialIssueId,
                    mi.challan_number AS ChallanNumber,
                    mi.issued_at AS IssuedAt,
                    ii.product_id AS ProductId,
                    ii.product_name_snapshot AS ProductName,
                    ii.product_unit_id AS ProductUnitId,
                    u.symbol AS UnitSymbol,
                    ii.entered_quantity AS EnteredQuantity,
                    ii.unit_charge AS UnitCharge,
                    ii.line_charge AS LineCharge,
                    (mr.id IS NOT NULL) AS IsReversed
                FROM thaka.material_issues mi
                INNER JOIN thaka.material_issue_items ii
                    ON ii.material_issue_id = mi.id
                INNER JOIN catalog.product_units pu
                    ON pu.id = ii.product_unit_id
                INNER JOIN catalog.units u
                    ON u.id = pu.unit_id
                LEFT JOIN thaka.material_reversals mr
                    ON mr.material_issue_id = mi.id
                WHERE mi.project_id = @ProjectId
                ORDER BY mi.issued_at DESC, mi.id DESC, ii.id;
                """;

            const string paymentsSql = """
                SELECT
                    p.id AS PaymentId,
                    p.receipt_number AS ReceiptNumber,
                    p.recorded_at AS RecordedAt,
                    p.payment_method AS PaymentMethod,
                    p.amount AS Amount,
                    p.recorded_by AS RecordedBy,
                    p.reference AS Reference,
                    (r.id IS NOT NULL) AS IsReversed
                FROM thaka.payments p
                LEFT JOIN thaka.payment_reversals r
                    ON r.payment_id = p.id
                WHERE p.project_id = @ProjectId
                ORDER BY p.recorded_at DESC, p.id DESC;
                """;

            var args = new { ProjectId = projectId };
            var materials = (await connection.QueryAsync<ThakaMaterialLedgerRowDto>(
                new CommandDefinition(
                    materialsSql,
                    args,
                    cancellationToken: cancellationToken))).ToArray();
            var payments = (await connection.QueryAsync<ThakaPaymentLedgerRowDto>(
                new CommandDefinition(
                    paymentsSql,
                    args,
                    cancellationToken: cancellationToken))).ToArray();

            return new ThakaProjectDetailDto(project, materials, payments);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ThakaCatalogItemDto>> GetMaterialCatalogAsync(
        CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            const string sql = """
                SELECT
                    p.id AS ProductId,
                    pu.id AS ProductUnitId,
                    p.name AS Name,
                    p.sku AS Sku,
                    u.symbol AS UnitSymbol,
                    COALESCE(sb.sellable_qty, 0) AS SellableStock,
                    p.default_sale_price AS UnitCharge,
                    (p.tracking_mode = 3) AS IsSerialized
                FROM catalog.products p
                INNER JOIN catalog.product_units pu
                    ON pu.product_id = p.id
                   AND pu.unit_id = p.base_unit_id
                INNER JOIN catalog.units u ON u.id = pu.unit_id
                LEFT JOIN inventory.stock_balances sb ON sb.product_id = p.id
                WHERE p.is_active = TRUE
                  AND pu.is_active = TRUE
                  AND pu.can_use_in_thaka = TRUE
                  AND pu.factor_to_base_unit = 1
                ORDER BY p.name, p.id;
                """;

            var rows = await connection.QueryAsync<ThakaCatalogItemDto>(
                new CommandDefinition(
                    sql,
                    cancellationToken: cancellationToken));
            return (IReadOnlyList<ThakaCatalogItemDto>)rows.ToArray();
        }, cancellationToken);

    private sealed record ThakaProjectPageRow(
        Guid ProjectId,
        string ProjectNumber,
        Guid CustomerId,
        string ProjectName,
        string CustomerName,
        string? CustomerPhone,
        string? SiteAddress,
        DateOnly StartedOn,
        ThakaProjectStatus Status,
        decimal MaterialValue,
        decimal Paid,
        decimal SettlementDiscount,
        decimal Balance,
        string? Note,
        int TotalActiveCount,
        decimal TotalActiveMaterialValue,
        decimal TotalActiveBalance);

    private async Task<T> WithConnectionAsync<T>(
        Func<IDbConnection, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return await action(connection);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}
