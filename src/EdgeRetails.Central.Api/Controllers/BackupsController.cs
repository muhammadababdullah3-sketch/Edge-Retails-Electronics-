using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Storage;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using EdgeRetails.Central.Application.Services;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/licensing/v1/backups")]
public class BackupsController : ControllerBase
{
    private readonly Wave3StorageAndSupportService _storageService;

    public BackupsController(Wave3StorageAndSupportService storageService)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
    }

    [HttpPut("/api/licensing/v1/uploads/{uploadId:guid}/content")]
    [Consumes("application/octet-stream")]
    public async Task<IActionResult> AppendUploadChunk(
        [FromRoute] Guid uploadId,
        [FromHeader(Name = "chunk-sha256")] string? chunkSha256,
        CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AuthenticatedInstallation"] is not Installation installation)
        {
            return Unauthorized(new { code = "INSTALLATION_AUTHORITY_NOT_ACTIVE", safeMessageKey = "auth.installation_required" });
        }
        if (!ContentRangeHeaderValue.TryParse(Request.Headers["Content-Range"], out var range) ||
            !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            range.From is null || range.To is null || range.Length is null ||
            Request.ContentLength is null || Request.ContentLength != range.To.Value - range.From.Value + 1 ||
            string.IsNullOrWhiteSpace(chunkSha256))
        {
            return BadRequest(new { code = "INVALID_UPLOAD_RANGE", safeMessageKey = "upload.range_invalid" });
        }

        var acceptedOffset = await _storageService.AppendUploadChunkAsync(
            uploadId, range.From.Value, range.Length.Value, chunkSha256, Request.Body,
            $"installation:{installation.Id:D}", cancellationToken);
        return Ok(new { uploadId, acceptedOffset });
    }

    [HttpPost("uploads")]
    [HttpPost("uploads/init")]
    public async Task<IActionResult> InitUpload(
        [FromHeader(Name = "X-Client-Operation-Id")] string? clientOperationId,
        [FromHeader(Name = "X-Reason")] string? reason,
        [FromBody] UploadInitDto dto,
        CancellationToken cancellationToken)
    {
        if (!TryCreateMeta(clientOperationId, reason, "storage.upload_init", out var meta))
        {
            return BadRequest(new { code = "INVALID_COMMAND_META", safeMessageKey = "command.metadata_invalid" });
        }

        if (HttpContext.Items["AuthenticatedInstallation"] is not Installation authenticatedInstallation ||
            dto.InstallationId != authenticatedInstallation.Id || dto.ShopId != authenticatedInstallation.ShopId)
        {
            return Unauthorized(new { code = "INSTALLATION_AUTHORITY_NOT_ACTIVE", safeMessageKey = "auth.installation_required" });
        }
        var callerScope = $"installation:{authenticatedInstallation.Id:D}";

        var (outcome, response) = await _storageService.InitUploadAsync(meta, dto, callerScope, cancellationToken);
        return Ok(new { outcome, response });
    }

    [HttpPost("uploads/{uploadId:guid}/finalize")]
    public async Task<IActionResult> FinalizeUpload(
        [FromRoute] Guid uploadId,
        [FromHeader(Name = "X-Client-Operation-Id")] string? clientOperationId,
        [FromHeader(Name = "X-Reason")] string? reason,
        [FromHeader(Name = "X-Object-Id")] Guid? headerObjectId,
        [FromHeader(Name = "X-Expected-Sha256")] string? headerExpectedSha256,
        [FromHeader(Name = "X-Expected-Length")] long? headerExpectedLength,
        [FromQuery] Guid? objectId,
        [FromQuery] string? expectedSha256,
        [FromQuery] long? expectedLength,
        CancellationToken cancellationToken)
    {
        if (!TryCreateMeta(clientOperationId, reason, "storage.upload_finalize", out var meta))
        {
            return BadRequest(new { code = "INVALID_COMMAND_META", safeMessageKey = "command.metadata_invalid" });
        }

        if (HttpContext.Items["AuthenticatedInstallation"] is not Installation authenticatedInstallation)
        {
            return Unauthorized(new { code = "INSTALLATION_AUTHORITY_NOT_ACTIVE", safeMessageKey = "auth.installation_required" });
        }
        var callerScope = $"installation:{authenticatedInstallation.Id:D}";

        UploadFinalizeDto dto;
        Stream streamToProcess;

        var hasContentType = MediaTypeHeaderValue.TryParse(Request.ContentType, out var requestContentType);
        var mediaType = hasContentType ? requestContentType?.MediaType : null;
        var isJsonBody = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        if (!isJsonBody && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { code = "UNSUPPORTED_UPLOAD_CONTENT_TYPE", safeMessageKey = "upload.content_type_unsupported" });
        }

        if (isJsonBody)
        {
            if (!StorageUploadLimits.TryRead(out var uploadLimits))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { code = "STORAGE_CONFIG_NOT_ACTIVE", safeMessageKey = "upload.configuration_required" });
            }
            if (Request.ContentLength is null || Request.ContentLength < 1 || Request.ContentLength > uploadLimits.MaxObjectBytes * 2L)
            {
                return BadRequest(new { code = "INVALID_UPLOAD_BODY_LENGTH", safeMessageKey = "upload.length_mismatch" });
            }

            UploadFinalizeDto? bodyDto;
            try
            {
                bodyDto = await System.Text.Json.JsonSerializer.DeserializeAsync<UploadFinalizeDto>(
                    Request.Body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new { code = "INVALID_UPLOAD_BODY", safeMessageKey = "upload.body_invalid" });
            }

            if (bodyDto is null || (bodyDto.UploadId != Guid.Empty && bodyDto.UploadId != uploadId))
            {
                return BadRequest(new { code = "INVALID_UPLOAD_BODY", safeMessageKey = "upload.body_invalid" });
            }
            byte[]? decodedCiphertext = null;
            if (bodyDto.CiphertextBase64 is not null)
            {
                if (bodyDto.CiphertextBase64.Length > uploadLimits.MaxObjectBytes * 4L / 3 + 8)
                {
                    return BadRequest(new { code = "INVALID_UPLOAD_BODY_LENGTH", safeMessageKey = "upload.length_mismatch" });
                }
                try
                {
                    decodedCiphertext = Convert.FromBase64String(bodyDto.CiphertextBase64);
                }
                catch (FormatException)
                {
                    return BadRequest(new { code = "INVALID_UPLOAD_BODY", safeMessageKey = "upload.body_invalid" });
                }
                if (decodedCiphertext.Length > uploadLimits.MaxObjectBytes)
                {
                    return BadRequest(new { code = "INVALID_UPLOAD_BODY_LENGTH", safeMessageKey = "upload.length_mismatch" });
                }
            }
            dto = bodyDto;
            dto.UploadId = uploadId;
            if (!string.IsNullOrWhiteSpace(dto.CiphertextBase64))
            {
                streamToProcess = new MemoryStream(decodedCiphertext!, writable: false);
            }
            else
            {
                streamToProcess = new MemoryStream();
            }
        }
        else
        {
            dto = new UploadFinalizeDto
            {
                UploadId = uploadId,
                ObjectId = objectId ?? headerObjectId ?? Guid.Empty,
                ExpectedSha256 = expectedSha256 ?? headerExpectedSha256 ?? string.Empty,
                ExpectedLength = expectedLength ?? headerExpectedLength ?? Request.ContentLength ?? 0
            };
            streamToProcess = Request.Body;
        }

        var (outcome, success) = await _storageService.FinalizeUploadAsync(meta, dto, streamToProcess, callerScope, cancellationToken);
        return Ok(new { outcome, success });
    }

    private static bool TryCreateMeta(string? operationIdHeader, string? reason, string operationType, out CommandMetaDto meta)
    {
        meta = new CommandMetaDto();
        if (!Guid.TryParse(operationIdHeader, out var operationId) || operationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(reason) || reason.Any(char.IsControl))
        {
            return false;
        }
        meta.ClientOperationId = operationId;
        meta.Reason = reason.Trim();
        meta.OperationType = operationType;
        return true;
    }

    [HttpGet("{objectId:guid}/download")]
    public async Task<IActionResult> CreateDownloadGrant(
        [FromRoute] Guid objectId,
        [FromBody] ObjectActionDto request,
        CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AuthenticatedInstallation"] is not Installation authenticatedInstallation)
        {
            return Unauthorized(new { code = "INSTALLATION_AUTHORITY_NOT_ACTIVE", safeMessageKey = "auth.installation_required" });
        }
        if (request.ObjectId != objectId || request.Purpose != EncryptedObjectRecord.PurposeEncryptedBackup)
        {
            return BadRequest(new { code = "INVALID_DOWNLOAD_REQUEST", safeMessageKey = "storage.download_request_invalid" });
        }

        var grant = await _storageService.CreateBackupDownloadGrantAsync(objectId, authenticatedInstallation.Id, cancellationToken);
        return Ok(grant);
    }

    [HttpGet("download-grants/{grantId:guid}/content")]
    public async Task<IActionResult> DownloadWithGrant(
        [FromRoute] Guid grantId,
        [FromHeader(Name = "X-Central-Download-Token")] string? token,
        CancellationToken cancellationToken)
    {
        var (stream, record) = await _storageService.DownloadWithGrantAsync(grantId, token, cancellationToken);
        Response.Headers["Digest"] = $"sha-256={Convert.ToBase64String(Convert.FromHexString(record.CiphertextSha256))}";
        Response.Headers["X-Object-Id"] = record.Id.ToString("D");
        Response.Headers["Content-Length"] = record.ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return File(stream, record.ContentType, $"{record.Id:N}.bin", enableRangeProcessing: true);
    }

    [HttpPost("rehearse")]
    public IActionResult RehearseRestore()
    {
        // Keep the legacy endpoint fail-closed without binding, validating, or logging client-supplied key fields.
        // Central control-plane recovery is exposed through a separate operator-owned workflow.
        Response.StatusCode = StatusCodes.Status501NotImplemented;
        Response.ContentType = "application/json";
        return new ContentResult
        {
            StatusCode = StatusCodes.Status501NotImplemented,
            ContentType = "application/json",
            Content = "{\"code\":\"CUSTOMER_RESTORE_NOT_SUPPORTED\",\"safeMessageKey\":\"restore.edge_verifier_required\"}"
        };
    }
}
