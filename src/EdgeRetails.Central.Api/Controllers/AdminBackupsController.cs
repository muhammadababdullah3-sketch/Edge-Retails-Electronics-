using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/admin/v1/backups")]
public sealed class AdminBackupsController(ICentralDbContext db) : ControllerBase
{
    private const string RequiredCapability = "backup.download";
    private static readonly string[] AllowedQueryFields = ["cursor", "pageSize", "asOfVersion"];

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = 25,
        [FromQuery] long? asOfVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext.Items["AdminSession"] is not AdminSessionDto session)
        {
            return Unauthorized(new { code = "SESSION_REQUIRED", safeMessageKey = "auth.session_required" });
        }

        if (!session.Capabilities.Contains(RequiredCapability, StringComparer.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { code = "FORBIDDEN", safeMessageKey = "auth.unauthorized_capability" });
        }

        var offset = 0;
        if (pageSize is < 1 or > 100 || asOfVersion < 0 ||
            cursor is not null &&
            (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
             offset > 100000 || offset.ToString(CultureInfo.InvariantCulture) != cursor) ||
            Request.Query.Any(pair =>
                !AllowedQueryFields.Contains(pair.Key, StringComparer.Ordinal) || pair.Value.Count != 1))
        {
            return BadRequest(new { code = "INVALID_QUERY", safeMessageKey = "query.invalid" });
        }

        // Keep the page and its version fingerprint on one database snapshot. The
        // projection intentionally excludes storage paths and opaque custody data.
        var query = db.EncryptedObjects.AsNoTracking()
            .Where(x => x.Purpose == EncryptedObjectRecord.PurposeEncryptedBackup)
            .Select(x => new
            {
                x.Id,
                x.ShopId,
                x.ContentLength,
                x.CiphertextSha256,
                x.CreatedAtUtc,
                x.RetentionUntilUtc,
                x.IsUnderLegalHold,
                x.IsTombstoned,
                x.TombstonedAtUtc,
                x.RowVersion
            });

        await using var transaction = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);

        var count = 0;
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var row in query.OrderBy(x => x.Id).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            fingerprint.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
            fingerprint.AppendData(new byte[] { 10 });
            count++;
        }

        var version = (long)(BinaryPrimitives.ReadUInt64BigEndian(fingerprint.GetHashAndReset()) & 0x001fffffffffffffUL);
        if (asOfVersion is not null && asOfVersion != version)
        {
            return Conflict(new { code = "PROJECTION_VERSION_CHANGED", safeMessageKey = "query.projection_changed" });
        }

        var rows = await query.OrderBy(x => x.Id).Skip(offset).Take(pageSize).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var nextOffset = offset + rows.Count;
        var nextCursor = nextOffset < count && nextOffset <= 100000
            ? nextOffset.ToString(CultureInfo.InvariantCulture)
            : null;
        var items = rows.Select(x => new
        {
            x.Id,
            x.ShopId,
            x.ContentLength,
            x.CiphertextSha256,
            x.CreatedAtUtc,
            x.RetentionUntilUtc,
            x.IsUnderLegalHold,
            x.IsTombstoned,
            x.TombstonedAtUtc,
            resourceVersion = x.RowVersion
        });

        return Ok(new
        {
            items,
            nextCursor,
            asOfVersion = version,
            authorizedCount = count,
            projectionRevision = version,
            isTruncated = nextCursor is null && nextOffset < count
        });
    }
}
