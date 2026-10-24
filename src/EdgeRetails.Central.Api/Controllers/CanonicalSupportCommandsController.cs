using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Common;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Storage;
using EdgeRetails.Central.Domain.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/support/tickets")]
public sealed class CanonicalSupportCommandsController(
    ICentralDbContext db,
    IOperationLedgerService ledger,
    ICentralAuditService audit,
    ICanonicalJsonSerializer serializer) : ControllerBase
{
    private static readonly string[] EnvelopeFields = ["meta", "payload"];
    private static readonly string[] MetaFields = ["clientOperationId", "operationType", "fingerprintVersion", "payloadSha256", "expectedVersion", "policyVersion", "reason", "sensitiveApprovalId"];
    private static readonly string[] TicketFields = ["shopId", "subject", "summary", "attachmentObjectIds", "redactionProfileVersion"];
    private static readonly string[] ActionFields = ["ticketId", "action", "replyText", "status", "assigneeId", "replyChannelId"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [HttpPost("~/api/licensing/v1/support/bundles")]
    public async Task<IActionResult> CreateInstallationBundle([FromBody] JsonElement envelope, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AuthenticatedInstallation"] is not Installation authenticated || authenticated.Id == Guid.Empty)
        { return Unauthorized(new { code = "INSTALLATION_AUTHORITY_NOT_ACTIVE", safeMessageKey = "auth.installation_required" }); }
        if (!ReadEnvelope(envelope, "support.bundle", out var meta, out var payload) || !HasExactUniqueProperties(payload, TicketFields))
        { return BadRequest(new { code = "INVALID_SUPPORT_ENVELOPE", safeMessageKey = "support.invalid_envelope" }); }

        TicketDto? command;
        try { command = JsonSerializer.Deserialize<TicketDto>(payload.GetRawText(), WebJson); }
        catch (JsonException) { return BadRequest(new { code = "INVALID_SUPPORT_PAYLOAD", safeMessageKey = "support.invalid_payload" }); }
        if (command is null || command.ShopId == Guid.Empty || string.IsNullOrWhiteSpace(command.Subject) ||
            string.IsNullOrWhiteSpace(command.Summary) || command.RedactionProfileVersion < 1 ||
            command.AttachmentObjectIds is null || command.AttachmentObjectIds.Any(x => x == Guid.Empty))
        { return BadRequest(new { code = "INVALID_SUPPORT_PAYLOAD", safeMessageKey = "support.invalid_payload" }); }
        if (meta!.ExpectedVersion != 0)
        { return Conflict(new { code = "CONCURRENCY_CONFLICT", safeMessageKey = "operation.concurrency_conflict" }); }

        // The proof filter authenticates the installation and consumes its nonce. Reload its
        // persisted state before applying any ticket mutation; the submitted shop ID is only
        // checked against that server-owned identity and is never used as authority.
        var installation = await db.Installations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == authenticated.Id, cancellationToken);
        if (installation is null || installation.Status != Installation.StatusActive)
        { return Unauthorized(new { code = "INSTALLATION_AUTHORITY_NOT_ACTIVE", safeMessageKey = "auth.installation_required" }); }
        if (command.ShopId != installation.ShopId)
        { return new ObjectResult(new { code = "FORBIDDEN", safeMessageKey = "support.shop_scope_denied" }) { StatusCode = StatusCodes.Status403Forbidden }; }

        var outcome = await ledger.ExecuteOrReplayAsync<JsonElement, Guid>(
            $"installation:{installation.Id:D}", installation.ShopId, meta.ToLedgerMeta(), payload,
            async _ =>
            {
                var shopExists = await db.Shops.AsNoTracking().AnyAsync(x => x.Id == installation.ShopId, cancellationToken);
                if (!shopExists)
                { throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SHOP_NOT_FOUND", "support.shop_not_found", "The installation's shop does not exist."); }

                var attachmentIds = command.AttachmentObjectIds;
                if (attachmentIds.Count > 20 || attachmentIds.Distinct().Count() != attachmentIds.Count)
                { throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SUPPORT_ATTACHMENTS_INVALID", "support.attachments_invalid", "Ticket attachments must be unique and limited to 20 objects."); }
                if (attachmentIds.Count > 0)
                {
                    var records = await db.EncryptedObjects.AsNoTracking().Where(x => attachmentIds.Contains(x.Id)).ToListAsync(cancellationToken);
                    if (records.Count != attachmentIds.Count || records.Any(x => x.ShopId != installation.ShopId || x.IsTombstoned || x.Purpose != EncryptedObjectRecord.PurposeSupportAttachment))
                    { throw new EdgeRetails.Central.Domain.Common.CentralDomainException("FORBIDDEN", "support.attachment_access_denied", "Every attachment must be active support ciphertext owned by this shop."); }
                }

                var now = DateTimeOffset.UtcNow;
                var ticket = SupportTicket.Create(Guid.NewGuid(), installation.ShopId, installation.Id, command.Subject,
                    command.Summary, attachmentIds, command.RedactionProfileVersion, now);
                db.SupportTickets.Add(ticket);
                db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId, "support.ticket_created",
                    nameof(SupportTicket), ticket.Id.ToString("D"),
                    serializer.SerializeCanonical(new { ticket.Id, ticket.ShopId, clientOperationId = meta.ClientOperationId }), now));
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAuditAsync("SUPPORT_TICKET_CREATED", $"installation:{installation.Id:D}", null, null,
                    meta.Reason, meta.ClientOperationId, meta.ClientOperationId, nameof(SupportTicket), ticket.Id.ToString("D"),
                    serializer.SerializeCanonical(new { ticket.Id, ticket.ShopId, ticket.RequesterPrincipalId, ticket.AttachmentObjectIdsJson }), true,
                    meta.PayloadSha256, cancellationToken);
                return (ticket.Id, $"support/tickets/{ticket.Id:D}", ticket.RowVersion, 1);
            }, targetResource: $"support-shop:{installation.ShopId:D}", cancellationToken: cancellationToken);
        return new JsonResult(outcome, WebJson);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement envelope, CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session) { return Unauthorized(new { code = "SESSION_REQUIRED" }); }
        if (!ReadEnvelope(envelope, "support.create", out var meta, out var payload) ||
            !HasExactUniqueProperties(payload, TicketFields))
        { return BadRequest(new { code = "INVALID_SUPPORT_ENVELOPE", safeMessageKey = "support.invalid_envelope" }); }

        TicketDto? command;
        try { command = JsonSerializer.Deserialize<TicketDto>(payload.GetRawText(), WebJson); }
        catch (JsonException) { return BadRequest(new { code = "INVALID_SUPPORT_PAYLOAD", safeMessageKey = "support.invalid_payload" }); }
        if (command is null || command.ShopId == Guid.Empty || string.IsNullOrWhiteSpace(command.Subject) ||
            string.IsNullOrWhiteSpace(command.Summary) || command.RedactionProfileVersion < 1 ||
            command.AttachmentObjectIds is null || command.AttachmentObjectIds.Any(x => x == Guid.Empty))
        { return BadRequest(new { code = "INVALID_SUPPORT_PAYLOAD", safeMessageKey = "support.invalid_payload" }); }
        if (meta!.ExpectedVersion != 0)
        { return Conflict(new { code = "CONCURRENCY_CONFLICT", safeMessageKey = "operation.concurrency_conflict" }); }

        var outcome = await ledger.ExecuteOrReplayAsync<JsonElement, Guid>(
            $"admin:{session.AdminUserId:D}", command.ShopId, meta.ToLedgerMeta(), payload,
            async _ =>
            {
                var shop = await db.Shops.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.ShopId, cancellationToken)
                    ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SHOP_NOT_FOUND", "support.shop_not_found", "The requested shop does not exist.");
                var attachmentIds = command.AttachmentObjectIds;
                if (attachmentIds.Count > 20 || attachmentIds.Distinct().Count() != attachmentIds.Count)
                { throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SUPPORT_ATTACHMENTS_INVALID", "support.attachments_invalid", "Ticket attachments must be unique and limited to 20 objects."); }
                if (attachmentIds.Count > 0)
                {
                    var records = await db.EncryptedObjects.AsNoTracking().Where(x => attachmentIds.Contains(x.Id)).ToListAsync(cancellationToken);
                    if (records.Count != attachmentIds.Count || records.Any(x => x.ShopId != shop.Id || x.IsTombstoned || x.Purpose != EdgeRetails.Central.Domain.Storage.EncryptedObjectRecord.PurposeSupportAttachment))
                    { throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SUPPORT_ATTACHMENT_ACCESS_DENIED", "support.attachment_access_denied", "Every attachment must be active support ciphertext owned by this shop."); }
                }

                var now = DateTimeOffset.UtcNow;
                var ticket = SupportTicket.Create(Guid.NewGuid(), shop.Id, session.AdminUserId, command.Subject, command.Summary,
                    attachmentIds, command.RedactionProfileVersion, now);
                db.SupportTickets.Add(ticket);
                db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId, "support.ticket_created",
                    nameof(SupportTicket), ticket.Id.ToString("D"), serializer.SerializeCanonical(new { ticket.Id, ticket.ShopId, clientOperationId = meta.ClientOperationId }), now));
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAuditAsync("SUPPORT_TICKET_CREATED", "ADMIN", session.AdminUserId, session.SessionId,
                    meta.Reason, meta.ClientOperationId, meta.ClientOperationId, nameof(SupportTicket), ticket.Id.ToString("D"),
                    serializer.SerializeCanonical(new { ticket.Id, ticket.ShopId, ticket.AttachmentObjectIdsJson }), true,
                    meta.PayloadSha256, cancellationToken);
                return (ticket.Id, $"support/tickets/{ticket.Id:D}", ticket.RowVersion, 1);
            }, targetResource: $"support-shop:{command.ShopId:D}", cancellationToken: cancellationToken);
        return new JsonResult(outcome, WebJson);
    }

    [HttpPost("{ticketId:guid}/commands")]
    public async Task<IActionResult> Command(
        [FromRoute] Guid ticketId,
        [FromBody] JsonElement envelope,
        CancellationToken cancellationToken)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session) { return Unauthorized(new { code = "SESSION_REQUIRED" }); }
        if (ticketId == Guid.Empty || !ReadEnvelope(envelope, "support.reply", out var meta, out var payload) ||
            !HasOnlyUniqueProperties(payload, ActionFields) ||
            !TryGuid(payload, "ticketId", out var payloadTicketId) || payloadTicketId != ticketId ||
            !TryString(payload, "action", out var action))
        { return BadRequest(new { code = "INVALID_SUPPORT_COMMAND", safeMessageKey = "support.invalid_command" }); }

        var hasReplyText = TryString(payload, "replyText", out var replyText);
        var hasStatus = TryString(payload, "status", out var status);
        Guid assigneeId = default;
        var hasAssignee = payload.TryGetProperty("assigneeId", out _) && TryGuid(payload, "assigneeId", out assigneeId);
        if (payload.TryGetProperty("replyChannelId", out _))
        { return BadRequest(new { code = "SUPPORT_REPLY_CHANNEL_NOT_CONFIGURED", safeMessageKey = "support.reply_channel_not_configured" }); }
        if ((action == "REPLY" && (!hasReplyText || hasStatus || hasAssignee)) ||
            (action == "SET_STATUS" && (!hasStatus || hasReplyText || hasAssignee || status is not (SupportTicket.StatusOpen or SupportTicket.StatusInProgress or SupportTicket.StatusWaiting or SupportTicket.StatusClosed))) ||
            (action == "ASSIGN" && (!hasAssignee || hasReplyText || hasStatus)) ||
            action is not ("REPLY" or "SET_STATUS" or "ASSIGN"))
        { return BadRequest(new { code = "INVALID_SUPPORT_COMMAND", safeMessageKey = "support.invalid_command" }); }

        if (action == "ASSIGN" && !await db.AdminUsers.AsNoTracking().AnyAsync(x => x.Id == assigneeId && x.IsActive, cancellationToken))
        { return BadRequest(new { code = "SUPPORT_ASSIGNEE_NOT_ACTIVE", safeMessageKey = "support.assignee_not_active" }); }

        var existing = await db.SupportTickets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == ticketId, cancellationToken)
            ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SUPPORT_TICKET_NOT_FOUND", "support.ticket_not_found", "The requested support ticket does not exist.");
        var operationMeta = meta!;

        var outcome = await ledger.ExecuteOrReplayAsync<JsonElement, bool>(
            $"admin:{session.AdminUserId:D}", existing.ShopId, operationMeta.ToLedgerMeta(), payload,
            async _ =>
            {
                var ticket = await db.SupportTickets.SingleOrDefaultAsync(x => x.Id == ticketId, cancellationToken)
                    ?? throw new EdgeRetails.Central.Domain.Common.CentralDomainException("SUPPORT_TICKET_NOT_FOUND", "support.ticket_not_found", "The requested support ticket does not exist.");
                if (ticket.RowVersion != operationMeta.ExpectedVersion)
                { throw new EdgeRetails.Central.Domain.Common.CentralDomainException("CONCURRENCY_CONFLICT", "operation.concurrency_conflict", "The support ticket changed before the command was applied."); }
                var now = DateTimeOffset.UtcNow;
                if (action == "REPLY") { ticket.AddReply(session.AdminUserId.ToString("D"), replyText!, now); }
                else if (action == "SET_STATUS") { ticket.SetStatus(status!, session.AdminUserId.ToString("D"), now); }
                else { ticket.AssignTo(assigneeId, session.AdminUserId.ToString("D"), now); }

                db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(operationMeta.ClientOperationId,
                    action == "REPLY" ? "support.ticket_replied" : action == "SET_STATUS" ? "support.ticket_status_changed" : "support.ticket_assigned",
                    nameof(SupportTicket), ticket.Id.ToString("D"),
                    serializer.SerializeCanonical(new { ticket.Id, ticket.ShopId, ticket.Status, ticket.AssignedAdminUserId, clientOperationId = operationMeta.ClientOperationId }), now));
                await db.SaveChangesAsync(cancellationToken);
                await audit.RecordAuditAsync(action == "REPLY" ? "SUPPORT_TICKET_REPLIED" : action == "SET_STATUS" ? "SUPPORT_TICKET_STATUS_CHANGED" : "SUPPORT_TICKET_ASSIGNED",
                    "ADMIN", session.AdminUserId, session.SessionId, operationMeta.Reason, operationMeta.ClientOperationId, operationMeta.ClientOperationId,
                    nameof(SupportTicket), ticket.Id.ToString("D"),
                    serializer.SerializeCanonical(new { ticket.Id, ticket.ShopId, ticket.Status, ticket.AssignedAdminUserId }), true,
                    operationMeta.PayloadSha256, cancellationToken);
                return (true, ticket.Id.ToString("D"), ticket.RowVersion, 1);
            }, targetResource: $"support-ticket:{ticketId:D}", cancellationToken: cancellationToken);
        return new JsonResult(outcome, WebJson);
    }

    private static bool ReadEnvelope(JsonElement envelope, string operationType, out CanonicalDirectoryMeta? meta, out JsonElement payload)
    {
        meta = null;
        payload = default;
        if (!HasExactUniqueProperties(envelope, EnvelopeFields) ||
            !envelope.TryGetProperty("meta", out var metaElement) || metaElement.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("payload", out payload) || payload.ValueKind != JsonValueKind.Object ||
            !HasOnlyUniqueProperties(metaElement, MetaFields)) { return false; }
        try { meta = JsonSerializer.Deserialize<CanonicalDirectoryMeta>(metaElement.GetRawText(), WebJson); }
        catch (JsonException) { return false; }
        return meta is not null && meta.ClientOperationId != Guid.Empty && meta.OperationType == operationType &&
            meta.FingerprintVersion == 1 && meta.ExpectedVersion >= 0 && meta.PolicyVersion >= 0 &&
            !string.IsNullOrWhiteSpace(meta.Reason) && meta.PayloadSha256 is not null &&
            Regex.IsMatch(meta.PayloadSha256, "\\A[a-f0-9]{64}\\z");
    }

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
        { if (!fields.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name)) { return false; } }
        return true;
    }
}
