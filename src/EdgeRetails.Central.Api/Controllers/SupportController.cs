using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/support/v1")]
public class SupportController : ControllerBase
{
    private readonly Wave3StorageAndSupportService _supportService;
    private readonly IAdminAuthService _authService;
    private readonly ICentralDbContext _dbContext;
    private readonly ICanonicalJsonSerializer _serializer;

    public SupportController(Wave3StorageAndSupportService supportService, IAdminAuthService authService, ICentralDbContext dbContext, ICanonicalJsonSerializer serializer)
    {
        _supportService = supportService ?? throw new ArgumentNullException(nameof(supportService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    [HttpGet("~/api/admin/v1/support/tickets")]
    public async Task<IActionResult> ViewTickets(
        [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? filter = null,
        [FromQuery] long? asOfVersion = null,
        CancellationToken cancellationToken = default)
    {
        var session = await GetValidatedSessionAsync(cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        var offset = 0;
        if (pageSize is < 1 or > 100 || filter?.Length > 200 || asOfVersion < 0 ||
            cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > 100000) ||
            Request.Query.Keys.Any(key => key is not ("cursor" or "pageSize" or "filter" or "asOfVersion")))
        {
            return BadRequest(new { code = "INVALID_QUERY", safeMessageKey = "query.invalid" });
        }

        var term = filter?.Trim() ?? string.Empty;
        var query = _dbContext.SupportTickets.AsNoTracking().Where(x =>
            term == string.Empty || x.Subject.Contains(term) || x.Status == term);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var count = 0;
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var projection = query.Select(x => new
        {
            x.Id, x.ShopId, x.RequesterPrincipalId, x.AssignedAdminUserId, x.Subject, x.Summary, x.Status,
            x.RedactionProfileVersion, x.AttachmentObjectIdsJson, x.RepliesJson,
            x.CreatedAtUtc, x.UpdatedAtUtc, x.RowVersion
        });
        await foreach (var item in projection.OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id)
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            fingerprint.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
            fingerprint.AppendData(new byte[] { 10 });
            count++;
        }

        var version = (long)(BinaryPrimitives.ReadUInt64BigEndian(fingerprint.GetHashAndReset()) & 0x001fffffffffffffUL);
        if (asOfVersion is not null && asOfVersion != version)
        {
            return Conflict(new { code = "PROJECTION_VERSION_CHANGED", safeMessageKey = "query.projection_changed" });
        }

        var items = await projection.OrderByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Id)
            .Skip(offset).Take(pageSize).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var nextOffset = offset + items.Count;
        var nextCursor = nextOffset < count && nextOffset <= 100000
            ? nextOffset.ToString(CultureInfo.InvariantCulture)
            : null;
        return Ok(new { items, nextCursor, asOfVersion = version, authorizedCount = count, projectionRevision = version });
    }

    [HttpPost("tickets")]
    public async Task<IActionResult> CreateTicket(
        [FromHeader(Name = "X-Client-Operation-Id")] Guid clientOperationId,
        [FromBody] TicketDto dto,
        CancellationToken cancellationToken)
    {
        var session = await GetValidatedSessionAsync(cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        var meta = CreateMeta(clientOperationId, "SUPPORT_TICKET_CREATE", dto);
        var (outcome, ticketId) = await _supportService.CreateSupportTicketAsync(meta, dto, session.AdminUserId, $"admin:{session.AdminUserId:N}", cancellationToken);
        return Ok(new { outcome, ticketId });
    }

    [HttpPost("tickets/{ticketId:guid}/actions")]
    [HttpPost("~/api/admin/v1/support/tickets/{ticketId:guid}/actions")]
    public async Task<IActionResult> TicketAction(
        [FromRoute] Guid ticketId,
        [FromHeader(Name = "X-Client-Operation-Id")] Guid clientOperationId,
        [FromBody] TicketActionDto dto,
        CancellationToken cancellationToken)
    {
        var session = await GetValidatedSessionAsync(cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }
        if (dto.TicketId != Guid.Empty && dto.TicketId != ticketId)
        {
            return BadRequest(new { code = "SUPPORT_TICKET_ID_MISMATCH", safeMessageKey = "support.ticket_id_mismatch" });
        }
        dto.TicketId = ticketId;
        var meta = CreateMeta(clientOperationId, dto.Action.Equals("STATUS", StringComparison.OrdinalIgnoreCase) ? "SUPPORT_TICKET_STATUS" : "SUPPORT_TICKET_REPLY", dto);
        var (outcome, success) = await _supportService.ReplySupportTicketAsync(meta, dto, session.AdminUserId.ToString("D"), $"admin:{session.AdminUserId:N}", cancellationToken);
        return Ok(new { outcome, success });
    }

    [HttpGet("~/api/admin/v1/support/tickets/{ticketId:guid}/attachments/{objectId:guid}/download")]
    public async Task<IActionResult> DownloadAttachment(
        [FromRoute] Guid ticketId,
        [FromRoute] Guid objectId,
        CancellationToken cancellationToken)
    {
        var session = await GetValidatedSessionAsync(cancellationToken);
        if (session is null)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        var (stream, record) = await _supportService.DownloadSupportAttachmentAsync(ticketId, objectId, cancellationToken);
        Response.Headers["Digest"] = $"sha-256={Convert.ToBase64String(Convert.FromHexString(record.CiphertextSha256))}";
        Response.Headers["X-Object-Id"] = record.Id.ToString("D");
        Response.Headers["Content-Length"] = record.ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return File(stream, record.ContentType, $"{record.Id:N}.bin", enableRangeProcessing: true);
    }

    private async Task<AdminSessionDto?> GetValidatedSessionAsync(CancellationToken cancellationToken)
    {
        var credentials = CentralSessionCredentials.Parse(Request);
        return credentials.IsValid
            ? await _authService.ValidateSessionAsync(credentials.SessionId, cancellationToken)
            : null;
    }

    private CommandMetaDto CreateMeta<T>(Guid operationId, string operationType, T payload)
    {
        return new CommandMetaDto
        {
            ClientOperationId = operationId == Guid.Empty ? Guid.NewGuid() : operationId,
            OperationType = operationType,
            PayloadSha256 = _serializer.SerializeAndHash(payload).Sha256Hex,
            Reason = operationType
        };
    }
}
