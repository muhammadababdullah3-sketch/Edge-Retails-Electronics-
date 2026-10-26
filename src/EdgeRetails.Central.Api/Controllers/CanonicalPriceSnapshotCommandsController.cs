using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Outbox;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/pricing-snapshots")]
public sealed class CanonicalPriceSnapshotCommandsController(
    ICentralDbContext db,
    IOperationLedgerService ledger,
    ICentralAuditService audit,
    ICanonicalJsonSerializer serializer) : ControllerBase
{
    private static readonly string[] EnvelopeFields = ["meta", "payload"];
    private static readonly string[] MetaFields = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];
    private static readonly string[] PayloadFields = ["shopId", "planId", "orderType", "billingPeriodId", "currency", "requestedPrimaryCapacity", "termKind", "purchaseTarget"];
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
            return BadRequest(new { code = "INVALID_PRICE_ENVELOPE", safeMessageKey = "pricing.invalid_envelope" });
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
        if (meta is null || meta.ClientOperationId == Guid.Empty || meta.OperationType != "price.snapshot" ||
            meta.FingerprintVersion != 1 || meta.ExpectedVersion < 0 || meta.PolicyVersion < 0 ||
            string.IsNullOrWhiteSpace(meta.Reason) || meta.PayloadSha256 is null ||
            !Regex.IsMatch(meta.PayloadSha256, "\\A[a-f0-9]{64}\\z"))
        {
            return BadRequest(new { code = "INVALID_OPERATION_META", safeMessageKey = "operation.invalid_meta" });
        }
        if (!TryReadPayload(payload, out var command))
        {
            return BadRequest(new { code = "INVALID_PRICE_PAYLOAD", safeMessageKey = "pricing.invalid_payload" });
        }

        var outcome = await ledger.ExecuteOrReplayAsync<JsonElement, Guid>(
            $"admin:{session.AdminUserId:D}", command.ShopId, meta.ToLedgerMeta(), payload,
            async _ =>
            {
                var shopExists = await db.Shops.AsNoTracking().AnyAsync(x => x.Id == command.ShopId, cancellationToken);
                var plan = await db.PlanCatalogItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.PlanId, cancellationToken)
                    ?? throw new CentralDomainException("PLAN_NOT_FOUND", "pricing.plan_not_found", "The requested plan does not exist.");
                if (!shopExists || !plan.IsActive || plan.PurchaseTarget != command.PurchaseTargetKind ||
                    plan.TermKind != command.TermKind || plan.BillingPeriodId != command.BillingPeriodId ||
                    !string.Equals(plan.Currency, command.Currency, StringComparison.OrdinalIgnoreCase) ||
                    command.RequestedPrimaryCapacity != 1 || !OrderTypeMatchesTarget(command.OrderType, command.PurchaseTargetKind))
                {
                    throw new CentralDomainException("PRICE_PLAN_MISMATCH", "pricing.plan_mismatch", "The requested shop, plan, currency, term, target or primary capacity does not match the active plan.");
                }
                if (meta.ExpectedVersion != plan.RowVersion)
                {
                    throw new CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The plan authority version changed before the pricing snapshot was created.");
                }
                if (command.BaseLicenseId is Guid licenseId &&
                    !await db.CommercialLicenses.AsNoTracking().AnyAsync(x => x.Id == licenseId && x.ShopId == command.ShopId, cancellationToken))
                {
                    throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", "The referenced base license does not belong to this shop.");
                }
                if (command.ExistingEntitlementId is Guid entitlementId &&
                    !await (from entitlement in db.CommercialEntitlements.AsNoTracking()
                            join license in db.CommercialLicenses.AsNoTracking() on entitlement.LicenseId equals license.Id
                            where entitlement.Id == entitlementId && license.ShopId == command.ShopId &&
                                  (command.PurchaseTargetKind == PlanCatalogItem.TargetBaseLicense || entitlement.Kind == command.PurchaseTargetKind) &&
                                  (!command.BaseLicenseId.HasValue || license.Id == command.BaseLicenseId.Value)
                            select entitlement.Id).AnyAsync(cancellationToken))
                {
                    throw new CentralDomainException("ENTITLEMENT_NOT_FOUND", "entitlement.not_found", "The referenced entitlement does not belong to this shop.");
                }

                var now = DateTimeOffset.UtcNow;
                var snapshot = PricingSnapshot.Create(command.ShopId, plan, plan.RowVersion, now);
                db.PricingSnapshots.Add(snapshot);
                db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(
                    meta.ClientOperationId, "commercial.pricing_snapshot_created", nameof(PricingSnapshot), snapshot.Id.ToString("D"),
                    serializer.SerializeCanonical(new { snapshot.Id, snapshot.ShopId, snapshot.CatalogItemId, snapshot.ServerGeneratedDigest, clientOperationId = meta.ClientOperationId }), now));
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAuditAsync("COMMERCIAL_PRICING_SNAPSHOT_CREATED", "ADMIN", session.AdminUserId,
                    session.SessionId, meta.Reason, meta.ClientOperationId, meta.ClientOperationId,
                    nameof(PricingSnapshot), snapshot.Id.ToString("D"),
                    serializer.SerializeCanonical(new { snapshot.Id, snapshot.ShopId, snapshot.CatalogItemId, snapshot.AuthorityVersion }),
                    true, meta.PayloadSha256, cancellationToken);
                return (snapshot.Id, $"pricing-snapshots/{snapshot.Id:D}", 1, snapshot.AuthorityVersion);
            }, targetResource: $"plan/{command.PlanId:D}", cancellationToken: cancellationToken);

        return new JsonResult(outcome, WebJson);
    }

    private static bool TryReadPayload(JsonElement payload, out CanonicalPricePayload command)
    {
        command = default!;
        if (!HasExactUniqueProperties(payload, PayloadFields) ||
            !TryGuid(payload, "shopId", out var shopId) || !TryGuid(payload, "planId", out var planId) ||
            !TryString(payload, "orderType", out var orderType) || !TryString(payload, "currency", out var currency) ||
            !TryString(payload, "termKind", out var termKind) ||
            !payload.TryGetProperty("requestedPrimaryCapacity", out var capacity) || capacity.ValueKind != JsonValueKind.Number ||
            !capacity.TryGetInt32(out var requestedCapacity) || requestedCapacity != 1 ||
            !payload.TryGetProperty("billingPeriodId", out var billingPeriod) ||
            billingPeriod.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            !payload.TryGetProperty("purchaseTarget", out var target) || target.ValueKind != JsonValueKind.Object ||
            !HasOnlyUniqueProperties(target, PurchaseTargetFields) || !TryString(target, "kind", out var targetKind))
        {
            return false;
        }
        var billingPeriodId = billingPeriod.ValueKind == JsonValueKind.Null ? null : billingPeriod.GetString();
        if ((termKind == PlanCatalogItem.TermLifetime && billingPeriodId is not null) ||
            (termKind == PlanCatalogItem.TermFixed && string.IsNullOrWhiteSpace(billingPeriodId)) ||
            (termKind is not (PlanCatalogItem.TermFixed or PlanCatalogItem.TermLifetime)) ||
            targetKind is not (PlanCatalogItem.TargetBaseLicense or PlanCatalogItem.TargetSupport or PlanCatalogItem.TargetPremiumUpgrade) ||
            (targetKind != PlanCatalogItem.TargetBaseLicense && termKind != PlanCatalogItem.TermFixed) ||
            currency.Length > 16 || !OrderTypeMatchesTarget(orderType, targetKind))
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
        else if (targetKind is PlanCatalogItem.TargetSupport or PlanCatalogItem.TargetPremiumUpgrade)
        {
            return false;
        }
        if (target.TryGetProperty("existingEntitlementId", out _))
        {
            if (!TryGuid(target, "existingEntitlementId", out var parsed)) { return false; }
            existingEntitlementId = parsed;
        }
        command = new CanonicalPricePayload(shopId, planId, orderType, billingPeriodId, currency, requestedCapacity,
            termKind, targetKind, baseLicenseId, existingEntitlementId);
        return true;
    }

    private static bool OrderTypeMatchesTarget(string orderType, string targetKind) => orderType switch
    {
        "NEW_LICENSE" or "RENEWAL" or "PLAN_CHANGE" => targetKind == PlanCatalogItem.TargetBaseLicense,
        "SUPPORT_RENEWAL" => targetKind == PlanCatalogItem.TargetSupport,
        "UPGRADE" => targetKind == PlanCatalogItem.TargetPremiumUpgrade,
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

    private sealed record CanonicalPricePayload(Guid ShopId, Guid PlanId, string OrderType, string? BillingPeriodId,
        string Currency, int RequestedPrimaryCapacity, string TermKind, string PurchaseTargetKind,
        Guid? BaseLicenseId, Guid? ExistingEntitlementId);
}
