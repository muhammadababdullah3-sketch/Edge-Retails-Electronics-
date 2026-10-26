using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Storage;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/evidence/uploads")]
public sealed class CanonicalEvidenceUploadController(Wave3StorageAndSupportService storage) : ControllerBase
{
    private static readonly string[] EnvelopeFields = ["meta", "payload"];
    private static readonly string[] MetaFields = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];
    private static readonly string[] InitFields = ["shopId", "installationId", "objectId", "purpose", "contentLength", "contentType", "ciphertextSha256", "localManifestAuthenticationOpaque", "retentionPolicyId"];
    private static readonly string[] FinalizeFields = ["uploadId", "objectId", "expectedLength", "expectedSha256"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [HttpPost]
    public async Task<IActionResult> Init([FromBody] JsonElement envelope, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        if (!ReadEnvelope(envelope, "evidence.upload.init", out var meta, out var payload) ||
            !HasOnlyUniqueProperties(payload, InitFields))
        {
            return BadRequest(new { code = "INVALID_UPLOAD_ENVELOPE", safeMessageKey = "upload.invalid_envelope" });
        }

        UploadInitDto? command;
        try { command = JsonSerializer.Deserialize<UploadInitDto>(payload.GetRawText(), WebJson); }
        catch (JsonException) { return BadRequest(new { code = "INVALID_UPLOAD_PAYLOAD", safeMessageKey = "upload.invalid_payload" }); }
        if (command is null || command.ShopId == Guid.Empty || command.InstallationId == Guid.Empty ||
            command.ObjectId == Guid.Empty || command.RetentionPolicyId == Guid.Empty || command.ContentLength < 1 ||
            string.IsNullOrWhiteSpace(command.ContentType) ||
            !Regex.IsMatch(command.CiphertextSha256 ?? string.Empty, "\\A[a-f0-9]{64}\\z") ||
            command.Purpose is not (EncryptedObjectRecord.PurposePaymentProof or EncryptedObjectRecord.PurposeEncryptedBackup or EncryptedObjectRecord.PurposeSupportAttachment) ||
            (payload.TryGetProperty("localManifestAuthenticationOpaque", out var opaque) && opaque.ValueKind != JsonValueKind.String))
        {
            return BadRequest(new { code = "INVALID_UPLOAD_PAYLOAD", safeMessageKey = "upload.invalid_payload" });
        }

        var (_, response) = await storage.InitUploadAsync(meta!.ToLedgerMeta(), command,
            $"admin:{session.AdminUserId:D}", cancellationToken, payload);
        return new JsonResult(response, WebJson);
    }

    [HttpPost("{uploadId:guid}/finalize")]
    public async Task<IActionResult> Finalize(
        [FromRoute] Guid uploadId,
        [FromBody] JsonElement envelope,
        CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        if (uploadId == Guid.Empty || !ReadEnvelope(envelope, "evidence.upload.finalize", out var meta, out var payload) ||
            !HasExactUniqueProperties(payload, FinalizeFields) ||
            !TryGuid(payload, "uploadId", out var payloadUploadId) || payloadUploadId != uploadId ||
            !TryGuid(payload, "objectId", out var objectId) ||
            !TryLong(payload, "expectedLength", out var expectedLength) || expectedLength < 1 ||
            !TryString(payload, "expectedSha256", out var expectedSha256) ||
            !Regex.IsMatch(expectedSha256, "\\A[a-f0-9]{64}\\z"))
        {
            return BadRequest(new { code = "INVALID_UPLOAD_FINALIZE", safeMessageKey = "upload.invalid_finalize" });
        }

        var command = new UploadFinalizeDto
        {
            UploadId = uploadId,
            ObjectId = objectId,
            ExpectedLength = expectedLength,
            ExpectedSha256 = expectedSha256
        };
        var (outcome, _) = await storage.FinalizeUploadAsync(meta!.ToLedgerMeta(), command, Stream.Null,
            $"admin:{session.AdminUserId:D}", cancellationToken, payload);
        return new JsonResult(outcome, WebJson);
    }

    private static bool ReadEnvelope(JsonElement envelope, string operationType,
        out CanonicalDirectoryMeta? meta, out JsonElement payload)
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
        return meta is not null && meta.ClientOperationId != Guid.Empty && meta.OperationType == operationType &&
            meta.FingerprintVersion == 1 && meta.ExpectedVersion >= 0 && meta.PolicyVersion >= 0 &&
            !string.IsNullOrWhiteSpace(meta.Reason) && Regex.IsMatch(meta.PayloadSha256 ?? string.Empty, "\\A[a-f0-9]{64}\\z");
    }

    private static bool TryGuid(JsonElement value, string name, out Guid result)
    {
        result = Guid.Empty;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            Guid.TryParse(property.GetString(), out result) && result != Guid.Empty;
    }

    private static bool TryLong(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out result);
    }

    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) { return false; }
        result = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(result);
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
}
