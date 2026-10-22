using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Outbox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/orders")]
public sealed class CanonicalOrderCommandsController(
    ICentralDbContext db,
    IOperationLedgerService ledger,
    ICentralAuditService audit,
    ICanonicalJsonSerializer serializer) : ControllerBase
{
    private static readonly string[] EnvelopeFields = ["meta", "payload"];
    private static readonly string[] MetaFields = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];
    private static readonly string[] PayloadFields = ["shopId", "orderType", "planId", "pricingSnapshotId", "requestedPrimaryCapacity", "termKind", "purchaseTarget"];
    private static readonly string[] PurchaseTargetFields = ["kind", "baseLicenseId", "existingEntitlementId"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement envelope, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        if (!HasExactUniqueProperties(envelope, EnvelopeFields) ||
            !envelope.TryGetProperty("meta", out var metaElement) || metaElement.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new { code = "INVALID_ORDER_ENVELOPE", safeMessageKey = "order.invalid_envelope" });
        }

        CanonicalDirectoryMeta? meta;
        try
        {
            if (!HasOnlyUniqueProperties(metaElement, MetaFields))
            {
                return BadRequest(new { code = "INVALID_OPERATION_META", safeMessageKey = "operation.invalid_meta" });
            }
            meta = JsonSerializer.Deserialize<CanonicalDirectoryMeta>(metaElement.GetRawText(), WebJson);
        }
        catch (JsonException)
        {
            return BadRequest(new { code = "INVALID_OPERATION_META", safeMessageKey = "operation.invalid_meta" });
        }

        if (meta is null || meta.ClientOperationId == Guid.Empty || meta.OperationType != "order.create" ||
            meta.FingerprintVersion != 1 || meta.ExpectedVersion < 0 || meta.PolicyVersion < 0 ||
            string.IsNullOrWhiteSpace(meta.Reason) || meta.PayloadSha256 is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(meta.PayloadSha256, "\\A[a-f0-9]{64}\\z"))
        {
            return BadRequest(new { code = "INVALID_OPERATION_META", safeMessageKey = "operation.invalid_meta" });
        }

        if (!TryReadPayload(payload, out var command))
        {
            return BadRequest(new { code = "INVALID_ORDER_PAYLOAD", safeMessageKey = "order.invalid_payload" });
        }

        var actorScope = $"admin:{session.AdminUserId:D}";
        var outcome = await ledger.ExecuteOrReplayAsync<JsonElement, Guid>(
            actorScope,
            command.ShopId,
            meta.ToLedgerMeta(),
            payload,
            async _ =>
            {
                var shop = await db.Shops.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.ShopId, cancellationToken)
                    ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SHOP_NOT_FOUND", "shop.not_found", "The selected shop does not exist.");
                var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shop.CustomerId, cancellationToken)
                    ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("CUSTOMER_NOT_FOUND", "customer.not_found", "The persisted shop customer does not exist.");
                var snapshot = await db.PricingSnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.PricingSnapshotId, cancellationToken)
                    ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SNAPSHOT_NOT_FOUND", "pricing.snapshot_not_found", "The pricing snapshot does not exist.");
                if (snapshot.ShopId != shop.Id || snapshot.CatalogItemId != command.PlanId ||
                    snapshot.PurchaseTarget != command.PurchaseTargetKind || snapshot.TermKind != command.TermKind ||
                    command.RequestedPrimaryCapacity != shop.PrimaryCapacity || shop.PrimaryCapacity != 1 ||
                    !OrderTypeMatchesTarget(command.OrderType, command.PurchaseTargetKind))
                {
                    throw new EdgeRetails.Central.Domain.Common.CentralDomainException("ORDER_SNAPSHOT_MISMATCH", "order.snapshot_mismatch", "The shop, plan, term, target or single-primary capacity does not match the persisted pricing snapshot.");
                }
                if (meta.ExpectedVersion != snapshot.AuthorityVersion)
                {
                    throw new EdgeRetails.Central.Domain.Common.CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The pricing snapshot authority version changed before the order was created.");
                }

                if (command.BaseLicenseId is Guid baseLicenseId &&
                    !await db.CommercialLicenses.AsNoTracking().AnyAsync(x => x.Id == baseLicenseId && x.ShopId == shop.Id, cancellationToken))
                {
                    throw new EdgeRetails.Central.Domain.Common.CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", "The referenced base license does not belong to this shop.");
                }
                if (command.ExistingEntitlementId is Guid entitlementId &&
                    !await (from entitlement in db.CommercialEntitlements.AsNoTracking()
                            join license in db.CommercialLicenses.AsNoTracking() on entitlement.LicenseId equals license.Id
                            where entitlement.Id == entitlementId && license.ShopId == shop.Id &&
                                  (command.PurchaseTargetKind == PlanCatalogItem.TargetBaseLicense || entitlement.Kind == command.PurchaseTargetKind) &&
                                  (!command.BaseLicenseId.HasValue || license.Id == command.BaseLicenseId.Value)
                            select entitlement.Id).AnyAsync(cancellationToken))
                {
                    throw new EdgeRetails.Central.Domain.Common.CentralDomainException("ENTITLEMENT_NOT_FOUND", "entitlement.not_found", "The referenced entitlement does not belong to this shop.");
                }

                var now = DateTimeOffset.UtcNow;
                var order = Order.CreateFromSnapshot(snapshot, customer.Id, now, command.OrderType,
                    command.RequestedPrimaryCapacity, command.BaseLicenseId, command.ExistingEntitlementId);
                db.Orders.Add(order);
                await db.SaveChangesAsync(cancellationToken);

                db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                    meta.ClientOperationId,
                    "commercial.order_created",
                    nameof(Order),
                    order.Id.ToString("D"),
                    serializer.SerializeCanonical(new { order.Id, order.ShopId, order.TotalAmount, clientOperationId = meta.ClientOperationId }),
                    now));
                await audit.RecordAuditAsync("COMMERCIAL_ORDER_CREATED", "ADMIN", session.AdminUserId,
                    session.SessionId, meta.Reason, meta.ClientOperationId, meta.ClientOperationId,
                    nameof(Order), order.Id.ToString("D"),
                    serializer.SerializeCanonical(new { order.Id, order.ShopId, order.SnapshotId, order.OrderType, order.PurchaseTarget, order.TermKind }),
                    true, meta.PayloadSha256, cancellationToken);
                return (order.Id, $"orders/{order.Id:D}", order.RowVersion, snapshot.AuthorityVersion);
            },
            targetResource: $"order/{command.PricingSnapshotId:D}",
            cancellationToken: cancellationToken);

        return new JsonResult(outcome, WebJson);
    }

    private static bool TryReadPayload(JsonElement payload, out CanonicalOrderPayload command)
    {
        command = default!;
        if (!HasOnlyUniqueProperties(payload, PayloadFields) ||
            !TryGuid(payload, "shopId", out var shopId) || !TryGuid(payload, "planId", out var planId) ||
            !TryGuid(payload, "pricingSnapshotId", out var snapshotId) ||
            !TryString(payload, "orderType", out var orderType) ||
            !TryString(payload, "termKind", out var termKind) ||
            !payload.TryGetProperty("requestedPrimaryCapacity", out var capacity) || capacity.ValueKind != JsonValueKind.Number ||
            !capacity.TryGetInt32(out var requestedCapacity) || requestedCapacity != 1 ||
            !payload.TryGetProperty("purchaseTarget", out var target) || target.ValueKind != JsonValueKind.Object ||
            !HasOnlyUniqueProperties(target, PurchaseTargetFields) || !TryString(target, "kind", out var targetKind))
        {
            return false;
        }

        if (!new[] { "NEW_LICENSE", "RENEWAL", "PLAN_CHANGE", "SUPPORT_RENEWAL", "UPGRADE" }.Contains(orderType, StringComparer.Ordinal) ||
            !new[] { "FIXED_TERM", "LIFETIME" }.Contains(termKind, StringComparer.Ordinal) ||
            !new[] { "BASE_LICENSE", "SUPPORT", "PREMIUM_UPGRADE" }.Contains(targetKind, StringComparer.Ordinal) ||
            (targetKind != "BASE_LICENSE" && termKind != "FIXED_TERM"))
        {
            return false;
        }

        Guid? baseLicenseId = null;
        Guid? existingEntitlementId = null;
        if (target.TryGetProperty("baseLicenseId", out _))
        {
            if (!TryGuid(target, "baseLicenseId", out var parsed)) { return false; }
            baseLicenseId = parsed;
        }
        else if (targetKind is "SUPPORT" or "PREMIUM_UPGRADE")
        {
            return false;
        }
        if (target.TryGetProperty("existingEntitlementId", out _))
        {
            if (!TryGuid(target, "existingEntitlementId", out var parsed)) { return false; }
            existingEntitlementId = parsed;
        }

        command = new CanonicalOrderPayload(shopId, orderType, planId, snapshotId, requestedCapacity,
            termKind, targetKind, baseLicenseId, existingEntitlementId);
        return true;
    }

    private static bool OrderTypeMatchesTarget(string orderType, string targetKind) => orderType switch
    {
        "NEW_LICENSE" or "RENEWAL" or "PLAN_CHANGE" => targetKind == "BASE_LICENSE",
        "SUPPORT_RENEWAL" => targetKind == "SUPPORT",
        "UPGRADE" => targetKind == "PREMIUM_UPGRADE",
        _ => false
    };

    private static bool TryGuid(JsonElement value, string name, out Guid result)
    {
        result = default;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            Guid.TryParse(property.GetString(), out result) && result != Guid.Empty;
    }

    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) { return false; }
        result = property.GetString() ?? string.Empty;
        return result.Length > 0;
    }

    private static bool HasExactUniqueProperties(JsonElement value, string[] fields) =>
        value.ValueKind == JsonValueKind.Object && HasOnlyUniqueProperties(value, fields) && value.EnumerateObject().Count() == fields.Length;

    private static bool HasOnlyUniqueProperties(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) { return false; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name)) { return false; }
        }
        return true;
    }

    private sealed record CanonicalOrderPayload(Guid ShopId, string OrderType, Guid PlanId, Guid PricingSnapshotId,
        int RequestedPrimaryCapacity, string TermKind, string PurchaseTargetKind, Guid? BaseLicenseId, Guid? ExistingEntitlementId);
}
