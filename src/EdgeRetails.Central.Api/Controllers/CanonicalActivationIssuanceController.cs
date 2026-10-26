using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/activation-tokens")]
public sealed class CanonicalActivationIssuanceController(IActivationService activation, ICanonicalJsonSerializer serializer) : ControllerBase
{
    private static readonly string[] EnvelopeFields = ["meta", "payload"];
    private static readonly string[] MetaFields = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];
    private static readonly string[] PayloadFields = ["licenseId", "installationId", "installationPublicKeyFingerprint", "hardwareFingerprintHash", "tokenPolicyId", "deliveryChannelId"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [HttpPost]
    public async Task<IActionResult> Issue([FromBody] JsonElement envelope, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        if (!ReadEnvelope(envelope, out var meta, out var payload) || !ReadPayload(payload))
        {
            return BadRequest(new { code = "INVALID_ACTIVATION_ISSUE", safeMessageKey = "activation.issue_invalid" });
        }
        if (!string.Equals(serializer.SerializeAndHash(payload).Sha256Hex, meta!.PayloadSha256, StringComparison.Ordinal))
        {
            return BadRequest(new { code = "PAYLOAD_HASH_MISMATCH", safeMessageKey = "operation.payload_hash_mismatch" });
        }

        var result = await activation.IssueCanonicalActivationTokenAsync(payload, meta, session.AdminUserId, session.SessionId, cancellationToken);
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
        return meta is not null && meta.ClientOperationId != Guid.Empty && meta.OperationType == "activation.issue" &&
            meta.FingerprintVersion == 1 && meta.ExpectedVersion >= 1 && meta.PolicyVersion >= 1 &&
            !string.IsNullOrWhiteSpace(meta.Reason) && Regex.IsMatch(meta.PayloadSha256 ?? string.Empty, "\\A[a-f0-9]{64}\\z");
    }

    private static bool ReadPayload(JsonElement payload)
    {
        return HasExactUniqueProperties(payload, PayloadFields) &&
            IsGuid(payload, "licenseId") && IsGuid(payload, "installationId") && IsGuid(payload, "tokenPolicyId") && IsGuid(payload, "deliveryChannelId") &&
            IsNonEmptyString(payload, "installationPublicKeyFingerprint") && IsNonEmptyString(payload, "hardwareFingerprintHash");
    }

    private static bool IsGuid(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty;

    private static bool IsNonEmptyString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

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
