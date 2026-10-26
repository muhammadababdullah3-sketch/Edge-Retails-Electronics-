using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Licensing;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/licenses")]
public sealed class CanonicalLicensePolicyController(ISensitiveLifecycleService lifecycle) : ControllerBase
{
    private static readonly string[] EnvelopeFields = ["meta", "payload"];
    private static readonly string[] MetaFields = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];
    private static readonly string[] PayloadFields = ["shopId", "licenseId", "policyVersion", "enforcementMode", "extensionEndUtc", "afterExtensionPolicyId", "leaseDurationSeconds", "offlineGraceSeconds", "rollbackToleranceSeconds", "capabilityPolicyId", "approvedByAdminId", "approvedAtUtc"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [HttpPost("{licenseId:guid}/policy")]
    public async Task<IActionResult> Configure(Guid licenseId, [FromBody] JsonElement envelope, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        if (licenseId == Guid.Empty || !ReadEnvelope(envelope, out var meta, out var payload) ||
            !TryReadPolicy(payload, licenseId, session.AdminUserId, out var policy))
        {
            return BadRequest(new { code = "INVALID_POLICY_COMMAND", safeMessageKey = "policy.invalid_command" });
        }

        var result = await lifecycle.ConfigureCanonicalPolicyAsync(licenseId, policy!, payload, meta!,
            session.AdminUserId, session.SessionId, cancellationToken);
        return new JsonResult(result, WebJson);
    }

    private static bool ReadEnvelope(JsonElement envelope, out CanonicalDirectoryMeta? meta, out JsonElement payload)
    {
        meta = null;
        payload = default;
        if (!HasExactUniqueProperties(envelope, EnvelopeFields) ||
            !envelope.TryGetProperty("meta", out var metaElement) || metaElement.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("payload", out payload) || payload.ValueKind != JsonValueKind.Object ||
            !HasOnlyUniqueProperties(metaElement, MetaFields))
        {
            return false;
        }
        try { meta = JsonSerializer.Deserialize<CanonicalDirectoryMeta>(metaElement.GetRawText(), WebJson); }
        catch (JsonException) { return false; }
        return meta is not null && meta.ClientOperationId != Guid.Empty && meta.OperationType == "license.policy" &&
            meta.FingerprintVersion == 1 && meta.ExpectedVersion >= 0 && meta.PolicyVersion > 0 &&
            meta.PolicyVersion > meta.ExpectedVersion && !string.IsNullOrWhiteSpace(meta.Reason) &&
            Regex.IsMatch(meta.PayloadSha256 ?? string.Empty, "\\A[a-f0-9]{64}\\z");
    }

    private static bool TryReadPolicy(JsonElement payload, Guid routeLicenseId, Guid adminId, out PolicyDto? policy)
    {
        policy = null;
        if (!HasOnlyUniqueProperties(payload, PayloadFields) ||
            !TryGuid(payload, "shopId", out _) || !TryGuid(payload, "licenseId", out var licenseId) || licenseId != routeLicenseId ||
            !TryInt(payload, "policyVersion", out var version) || version < 1 ||
            !TryString(payload, "enforcementMode", out var mode) ||
            !TryGuid(payload, "afterExtensionPolicyId", out _) ||
            !TryInt(payload, "leaseDurationSeconds", out var lease) || lease < 1 ||
            !TryInt(payload, "offlineGraceSeconds", out var grace) || grace < 86400 ||
            !TryInt(payload, "rollbackToleranceSeconds", out var rollback) || rollback < 0 ||
            !TryGuid(payload, "capabilityPolicyId", out _) ||
            !TryGuid(payload, "approvedByAdminId", out var approver) || approver != adminId ||
            !payload.TryGetProperty("approvedAtUtc", out var approvedAt) || approvedAt.ValueKind != JsonValueKind.String ||
            !approvedAt.TryGetDateTimeOffset(out _))
        {
            return false;
        }

        if (mode == ShopEnforcementPolicy.FullStop)
        {
            if (payload.TryGetProperty("extensionEndUtc", out _))
            {
                return false;
            }
        }
        else if (mode == ShopEnforcementPolicy.ConfigurableExtension)
        {
            if (!payload.TryGetProperty("extensionEndUtc", out var end) || end.ValueKind != JsonValueKind.String || !end.TryGetDateTimeOffset(out _))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        try { policy = JsonSerializer.Deserialize<PolicyDto>(payload.GetRawText(), WebJson); }
        catch (JsonException) { return false; }
        return policy is not null && policy.PolicyVersion == version && policy.LeaseDurationSeconds == lease &&
            policy.OfflineGraceSeconds == grace && policy.RollbackToleranceSeconds == rollback;
    }

    private static bool TryGuid(JsonElement value, string name, out Guid result)
    {
        result = Guid.Empty;
        return value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && Guid.TryParse(p.GetString(), out result) && result != Guid.Empty;
    }
    private static bool TryInt(JsonElement value, string name, out int result)
    {
        result = 0;
        return value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out result);
    }
    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        result = p.GetString() ?? string.Empty;
        return result.Length > 0;
    }
    private static bool HasExactUniqueProperties(JsonElement value, string[] fields) =>
        value.ValueKind == JsonValueKind.Object && HasOnlyUniqueProperties(value, fields) && value.EnumerateObject().Count() == fields.Length;
    private static bool HasOnlyUniqueProperties(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!fields.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
            {
                return false;
            }
        }
        return true;
    }
}
