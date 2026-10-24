using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Storage;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
public sealed class CanonicalEvidenceDownloadController(
    Wave3StorageAndSupportService storage,
    ICentralAuditService audit) : ControllerBase
{
    private static readonly string[] QueryFields = ["objectId", "purpose", "retentionPolicyId", "restoreAuthorizationId"];

    [HttpGet("/api/admin/v1/evidence/{objectId:guid}/download")]
    public async Task<IActionResult> Download(
        [FromRoute] Guid objectId,
        [FromQuery(Name = "objectId")] Guid? requestObjectId,
        [FromQuery] string? purpose,
        [FromQuery] Guid? retentionPolicyId,
        [FromQuery] Guid? restoreAuthorizationId,
        CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        if (objectId == Guid.Empty || requestObjectId != objectId ||
            Request.Query.Keys.Any(key => !QueryFields.Contains(key, StringComparer.Ordinal) || Request.Query[key].Count != 1) ||
            !Request.Query.ContainsKey("objectId") || !Request.Query.ContainsKey("purpose") ||
            purpose != EncryptedObjectRecord.PurposePaymentProof ||
            retentionPolicyId == Guid.Empty || restoreAuthorizationId == Guid.Empty)
        {
            return BadRequest(new { code = "INVALID_EVIDENCE_DOWNLOAD", safeMessageKey = "evidence.download_invalid" });
        }

        // Restore authorization applies to a restore operation, not an evidence read.
        if (restoreAuthorizationId is not null)
        {
            return BadRequest(new { code = "RESTORE_AUTHORIZATION_NOT_APPLICABLE", safeMessageKey = "evidence.download_invalid" });
        }

        var (stream, record) = await storage.DownloadEvidenceCiphertextAsync(
            objectId,
            retentionPolicyId,
            cancellationToken);

        await audit.RecordAuditAsync(
            "EVIDENCE_DOWNLOADED",
            "ADMIN",
            session.AdminUserId,
            session.SessionId,
            "Authorized payment evidence download",
            null,
            null,
            nameof(EncryptedObjectRecord),
            record.Id.ToString("D"),
            JsonSerializer.Serialize(new { objectId = record.Id, shopId = record.ShopId, purpose = record.Purpose }),
            true,
            null,
            cancellationToken);

        Response.Headers["Digest"] = $"sha-256={Convert.ToBase64String(Convert.FromHexString(record.CiphertextSha256))}";
        Response.Headers["X-Object-Id"] = record.Id.ToString("D");
        Response.Headers["Content-Length"] = record.ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return File(stream, record.ContentType, $"{record.Id:N}.bin", enableRangeProcessing: true);
    }
}
