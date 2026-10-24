using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1")]
public sealed class DirectoryController(ICentralDbContext db) : ControllerBase
{
    [HttpGet("customers/{id:guid}")]
    public async Task<IActionResult> Customer(Guid id, CancellationToken ct)
    {
        var row = await db.Customers.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, DisplayName = x.LegalName, x.Status, x.RowVersion }).SingleOrDefaultAsync(ct);
        if (row is null) { return NotFound(new { code = "CUSTOMER_NOT_FOUND" }); }
        return Ok(Page(new[] { new { row.Id, displayName = Mask(row.DisplayName), row.Status, resourceVersion = row.RowVersion } }, null, row.RowVersion, 1));
    }

    [HttpGet("shops/{id:guid}")]
    public async Task<IActionResult> Shop(Guid id, CancellationToken ct)
    {
        var row = await db.Shops.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.CustomerId, x.ShopName, x.Status, InstallationCapacity = x.PrimaryCapacity, ResourceVersion = x.RowVersion }).SingleOrDefaultAsync(ct);
        if (row is null) { return NotFound(new { code = "SHOP_NOT_FOUND" }); }
        return Ok(Page(new[] { row }, null, row.ResourceVersion, 1));
    }

    [HttpGet("licenses/{id:guid}")]
    public async Task<IActionResult> License(Guid id, CancellationToken ct)
    {
        // Keep this contract projection PII-free and return the same single-item
        // Page shape used by the canonical customer and shop detail routes.
        var row = await db.CommercialLicenses.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.ShopId,
                x.CustomerId,
                x.InitialOrderId,
                x.TermKind,
                x.ValidFromUtc,
                x.ValidUntilUtc,
                x.Status,
                x.AuthorityRevision,
                x.PrimaryCapacity,
                ResourceVersion = x.RowVersion
            })
            .SingleOrDefaultAsync(ct);
        if (row is null) { return NotFound(new { code = "LICENSE_NOT_FOUND" }); }
        var policy = await db.ShopEnforcementPolicies.AsNoTracking()
            .Where(x => x.ShopId == row.ShopId && x.LicenseId == row.Id)
            .OrderByDescending(x => x.PolicyVersion)
            .ThenByDescending(x => x.CreatedAtUtc)
            .Select(x => new { x.CapabilityPolicyId, x.PolicyVersion })
            .FirstOrDefaultAsync(ct);
        return Ok(Page(new[]
        {
            new
            {
                row.Id,
                row.ShopId,
                row.CustomerId,
                row.InitialOrderId,
                row.TermKind,
                row.ValidFromUtc,
                row.ValidUntilUtc,
                row.Status,
                row.AuthorityRevision,
                row.PrimaryCapacity,
                row.ResourceVersion,
                CapabilityPolicyId = policy?.CapabilityPolicyId,
                PolicyVersion = policy?.PolicyVersion
            }
        }, null, row.ResourceVersion, 1));
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? filter = null, [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = 20, [FromQuery] long? asOfVersion = null, CancellationToken ct = default)
    {
        var offset = 0;
        if (pageSize is < 1 or > 100 || filter?.Length > 200 || asOfVersion < 0 ||
            cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > 100000) ||
            Request.Query.Keys.Any(k => k is not ("filter" or "cursor" or "pageSize" or "asOfVersion")))
        { return BadRequest(new { code = "INVALID_QUERY" }); }

        var term = filter?.Trim() ?? "";
        // Search returns business shop identity and a masked customer label. It
        // never projects contact fields, installation secrets or commercial tokens.
        var query = from shop in db.Shops.AsNoTracking()
                    join customer in db.Customers.AsNoTracking() on shop.CustomerId equals customer.Id
                    where term == "" || shop.ShopName.Contains(term) || shop.ShopCode.Contains(term) || customer.LegalName.Contains(term)
                    select new { shop.Id, shop.CustomerId, shop.ShopName, shop.ShopCode, shop.Status,
                        CustomerName = customer.LegalName, Version = Math.Max(shop.RowVersion, customer.RowVersion) };
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var count = 0;
        // A maximum row version misses inserts and replacements. Fingerprint
        // the complete authorized projection in this same repeatable snapshot;
        // stream it so response pagination never requires retaining all rows.
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var item in query.OrderBy(x => x.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            fingerprint.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
            fingerprint.AppendData(new byte[] { 10 });
            count++;
        }
        var version = (long)(BinaryPrimitives.ReadUInt64BigEndian(fingerprint.GetHashAndReset()) & 0x001fffffffffffffUL);
        if (asOfVersion is not null && asOfVersion != version)
        { return Conflict(new { code = "PROJECTION_VERSION_CHANGED" }); }
        var rows = await query.OrderBy(x => x.Id).Skip(offset).Take(pageSize).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        var next = offset + rows.Count < count ? (offset + rows.Count).ToString(CultureInfo.InvariantCulture) : null;
        return Ok(Page(rows.Select(x => new { shopId = x.Id, x.CustomerId, x.ShopName, x.ShopCode, x.Status,
            customerDisplayName = Mask(x.CustomerName), resourceVersion = x.Version }), next, version, count));
    }

    private static object Page<T>(IEnumerable<T> items, string? nextCursor, long version, int count) =>
        new { items, nextCursor, asOfVersion = version, authorizedCount = count, projectionRevision = version };

    private static string Mask(string name) => string.IsNullOrEmpty(name) ? "***" : char.IsHighSurrogate(name[0]) && name.Length > 1 ? name[..2] + "***" : name[..1] + "***";
}
