using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/licenses")]
public sealed class CanonicalLicenseLifecycleController(ISensitiveLifecycleService lifecycle) : ControllerBase
{
    private static readonly string[] EnvelopeFields = ["meta", "payload"];
    private static readonly string[] MetaFields = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];
    private static readonly string[] PayloadFields = ["licenseId", "effectiveAtUtc", "capabilityPolicyId"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [HttpPost("{licenseId:guid}/pause")]
    public Task<IActionResult> Pause(Guid licenseId, [FromBody] JsonElement envelope, CancellationToken token) =>
        Execute("license.pause", licenseId, envelope, token);

    [HttpPost("{licenseId:guid}/resume")]
    public Task<IActionResult> Resume(Guid licenseId, [FromBody] JsonElement envelope, CancellationToken token) =>
        Execute("license.resume", licenseId, envelope, token);

    [HttpPost("{licenseId:guid}/suspend")]
    public Task<IActionResult> Suspend(Guid licenseId, [FromBody] JsonElement envelope, CancellationToken token) =>
        Execute("license.suspend", licenseId, envelope, token);

    [HttpPost("{licenseId:guid}/reinstate")]
    public Task<IActionResult> Reinstate(Guid licenseId, [FromBody] JsonElement envelope, CancellationToken token) =>
        Execute("license.reinstate", licenseId, envelope, token);

    [HttpPost("{licenseId:guid}/revoke")]
    public Task<IActionResult> Revoke(Guid licenseId, [FromBody] JsonElement envelope, CancellationToken token) =>
        Execute("license.revoke", licenseId, envelope, token);

    private async Task<IActionResult> Execute(string operationType, Guid licenseId, JsonElement envelope, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        if (licenseId == Guid.Empty || !ReadEnvelope(envelope, operationType, out var meta, out var payload) ||
            !ReadPayload(payload, licenseId, out var effectiveAtUtc, out var capabilityPolicyId))
        {
            return BadRequest(new { code = "INVALID_LICENSE_COMMAND", safeMessageKey = "license.invalid_command" });
        }

        var outcome = await lifecycle.ExecuteCanonicalLicenseLifecycleAsync(
            operationType, licenseId, capabilityPolicyId, effectiveAtUtc, payload, meta!,
            session.AdminUserId, session.SessionId, cancellationToken);
        return new JsonResult(outcome, WebJson);
    }

    private static bool ReadEnvelope(JsonElement envelope, string operationType, out CanonicalDirectoryMeta? meta, out JsonElement payload)
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

        try
        {
            meta = JsonSerializer.Deserialize<CanonicalDirectoryMeta>(metaElement.GetRawText(), WebJson);
        }
        catch (JsonException)
        {
            return false;
        }

        return meta is not null && meta.ClientOperationId != Guid.Empty && meta.OperationType == operationType &&
            meta.FingerprintVersion == 1 && meta.ExpectedVersion >= 0 && meta.PolicyVersion > 0 &&
            !string.IsNullOrWhiteSpace(meta.Reason) &&
            Regex.IsMatch(meta.PayloadSha256 ?? string.Empty, "\\A[a-f0-9]{64}\\z");
    }

    private static bool ReadPayload(JsonElement payload, Guid routeLicenseId, out DateTimeOffset effectiveAtUtc, out Guid capabilityPolicyId)
    {
        effectiveAtUtc = default;
        capabilityPolicyId = Guid.Empty;
        if (!HasExactUniqueProperties(payload, PayloadFields) ||
            !TryGuid(payload, "licenseId", out var payloadLicenseId) || payloadLicenseId != routeLicenseId ||
            !TryGuid(payload, "capabilityPolicyId", out capabilityPolicyId) ||
            !payload.TryGetProperty("effectiveAtUtc", out var effective) || effective.ValueKind != JsonValueKind.String ||
            !effective.TryGetDateTimeOffset(out effectiveAtUtc))
        {
            return false;
        }
        return true;
    }

    private static bool TryGuid(JsonElement value, string name, out Guid result)
    {
        result = Guid.Empty;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            Guid.TryParse(property.GetString(), out result) && result != Guid.Empty;
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
