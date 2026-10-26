using System.Text.Json;
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
[Route("api/admin/v1")]
public sealed class DirectoryCommandsController(ICentralDbContext db, IOperationLedgerService ledger,
    ICentralAuditService audit, ICanonicalJsonSerializer serializer) : ControllerBase
{
    [HttpPost("customers")]
    public Task<IActionResult> CreateCustomer([FromBody] CanonicalDirectoryCommand command, CancellationToken ct) => Execute(command, false, ct);

    [HttpPost("shops")]
    public Task<IActionResult> RegisterShop([FromBody] CanonicalDirectoryCommand command, CancellationToken ct) => Execute(command, true, ct);

    private async Task<IActionResult> Execute(CanonicalDirectoryCommand command, bool shop, CancellationToken ct)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session) { return Unauthorized(); }
        var operation = shop ? "shop.register" : "customer.create";
        var meta = command.Meta;
        if (meta is null || meta.ClientOperationId == Guid.Empty || meta.OperationType != operation || meta.FingerprintVersion != 1 ||
            meta.ExpectedVersion != 0 || meta.PolicyVersion < 0 || string.IsNullOrWhiteSpace(meta.Reason) ||
            meta.PayloadSha256 is null || !Regex.IsMatch(meta.PayloadSha256, "\\A[a-f0-9]{64}\\z"))
        { return BadRequest(new { code = "INVALID_OPERATION_META" }); }
        var payload = command.Payload;
        var fields = shop ? new[] { "customerId", "shopName", "contactReference", "installationCapacity" } : new[] { "displayName", "contactReference" };
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any(p => !fields.Contains(p.Name)) ||
            payload.EnumerateObject().Select(p => p.Name).Distinct().Count() != payload.EnumerateObject().Count() ||
            !StringField(payload, shop ? "shopName" : "displayName", out var name) ||
            payload.TryGetProperty("contactReference", out _) && !StringField(payload, "contactReference", out _))
        { return BadRequest(new { code = "INVALID_DIRECTORY_PAYLOAD" }); }
        if (name!.Length > 256) { return BadRequest(new { code = "DIRECTORY_NAME_TOO_LONG" }); }
        Guid customerId = default;
        if (shop && (!payload.TryGetProperty("customerId", out var customer) || customer.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(customer.GetString(), out customerId) || customerId == Guid.Empty ||
            !payload.TryGetProperty("installationCapacity", out var capacity) || capacity.ValueKind != JsonValueKind.Number || !capacity.TryGetInt32(out var slots) || slots != 1))
        { return BadRequest(new { code = "INVALID_DIRECTORY_PAYLOAD" }); }
        var reference = payload.TryGetProperty("contactReference", out var contact) ? contact.GetString() : null;
        var actor = $"admin:{session.AdminUserId}";
        var outcome = await ledger.ExecuteOrReplayAsync<JsonElement, Guid>(actor, null, meta.ToLedgerMeta(), payload,
            async _ =>
            {
                var now = DateTimeOffset.UtcNow;
                Guid id;
                if (shop)
                {
                    if (!await db.Customers.AnyAsync(x => x.Id == customerId, ct))
                    { throw new CentralDomainException("CUSTOMER_NOT_FOUND", "customer.not_found", "Customer not found."); }
                    var entity = Shop.RegisterFromContract(customerId, name!, reference, now);
                    db.Shops.Add(entity); id = entity.Id;
                }
                else
                {
                    var entity = Customer.CreateFromContract(name!, reference, now);
                    db.Customers.Add(entity); id = entity.Id;
                }
                var type = shop ? nameof(Shop) : nameof(Customer);
                db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(meta.ClientOperationId,
                    shop ? "commercial.shop_registered" : "commercial.customer_created", type, id.ToString("D"),
                    serializer.SerializeCanonical(new { id }), now));
                await db.SaveChangesAsync(ct);
                await audit.RecordAuditAsync(shop ? "COMMERCIAL_SHOP_REGISTERED" : "COMMERCIAL_CUSTOMER_CREATED", "ADMIN",
                    session.AdminUserId, session.SessionId, meta.Reason, meta.ClientOperationId, meta.ClientOperationId,
                    type, id.ToString("D"), serializer.SerializeCanonical(new { id }), true, meta.PayloadSha256, ct);
                return (id, $"{(shop ? "shops" : "customers")}/{id:D}", 1, 0);
            }, targetResource: null, cancellationToken: ct);
        return new JsonResult(outcome, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static bool StringField(JsonElement payload, string name, out string? value)
    {
        value = payload.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        return !string.IsNullOrWhiteSpace(value);
    }
}
