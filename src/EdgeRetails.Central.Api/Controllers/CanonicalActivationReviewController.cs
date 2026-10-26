using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/activations")]
public sealed class CanonicalActivationReviewController(
    IActivationService activation,
    ICanonicalJsonSerializer serializer) : ControllerBase
{
    private static readonly string[] EnvelopeFields = ["meta", "payload"];
    private static readonly string[] MetaFields = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];
    private static readonly string[] PayloadFields = ["activationRequestId", "decision", "reasonCode", "validationSnapshotVersion"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [HttpPost("{id:guid}/review")]
    public async Task<IActionResult> Review([FromRoute] Guid id, [FromBody] JsonElement envelope, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        if (id == Guid.Empty || !ReadEnvelope(envelope, out var meta, out var payload) ||
            !TryReadPayload(payload, out var decision) || decision.ActivationRequestId != id)
        {
            return BadRequest(new { code = "INVALID_ACTIVATION_REVIEW", safeMessageKey = "activation.review_invalid" });
        }
        if (!string.Equals(serializer.SerializeAndHash(payload).Sha256Hex, meta!.PayloadSha256, StringComparison.Ordinal))
        {
            return BadRequest(new { code = "PAYLOAD_HASH_MISMATCH", safeMessageKey = "operation.payload_hash_mismatch" });
        }

        var outcome = await activation.ReviewCanonicalActivationAsync(
            id, meta, decision, session.AdminUserId, session.SessionId, cancellationToken);
        return new JsonResult(outcome, WebJson);
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
        return meta is not null && meta.ClientOperationId != Guid.Empty && meta.OperationType == "activation.review" &&
            meta.FingerprintVersion == 1 && meta.ExpectedVersion >= 1 && meta.PolicyVersion >= 0 &&
            !string.IsNullOrWhiteSpace(meta.Reason) && Regex.IsMatch(meta.PayloadSha256 ?? string.Empty, "\\A[a-f0-9]{64}\\z");
    }

    private static bool TryReadPayload(JsonElement payload, out CanonicalActivationDecisionDto decision)
    {
        decision = default!;
        if (!HasExactUniqueProperties(payload, PayloadFields) ||
            !TryGuid(payload, "activationRequestId", out var requestId) ||
            !TryString(payload, "decision", out var decisionName) ||
            !TryString(payload, "reasonCode", out var reasonCode) ||
            !payload.TryGetProperty("validationSnapshotVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version) || version < 1 ||
            decisionName is not ("APPROVE" or "REJECT" or "REQUEST_INFORMATION" or "OPEN_HARDWARE_REVIEW"))
        {
            return false;
        }
        decision = new CanonicalActivationDecisionDto(requestId, decisionName, reasonCode, version);
        return true;
    }

    private static bool TryGuid(JsonElement value, string name, out Guid result)
    {
        result = Guid.Empty;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            Guid.TryParse(property.GetString(), out result) && result != Guid.Empty;
    }

    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        result = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(result);
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
