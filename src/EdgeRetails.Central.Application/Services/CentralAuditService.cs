using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Buffers.Binary;
using System.Security.Cryptography;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

public class CentralAuditService : ICentralAuditService
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("EdgeRetails.Central.Audit", "1.0");
    private static readonly System.Diagnostics.Metrics.Counter<long> Recorded = Meter.CreateCounter<long>("central.audit.records");
    private static readonly HashSet<string> SafeKeys = new(StringComparer.OrdinalIgnoreCase)
    { "action", "adminUserId", "roleId", "sessionId", "resourceId", "eventType", "operationId", "isSuccess", "reasonCode", "code", "keyId", "keyVersion" };
    private readonly ICentralDbContext _dbContext;

    public CentralAuditService(ICentralDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task RecordAuditAsync(
        string eventType,
        string actorScope,
        Guid? adminUserId,
        Guid? sessionId,
        string? reason,
        Guid? correlationId,
        Guid? clientOperationId,
        string entityName,
        string entityId,
        string detailsJson,
        bool isSuccess = true,
        string? payloadSha256 = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var maskedDetails = MaskPii(detailsJson);

        var record = CentralAuditRecord.Create(
            actorId: adminUserId?.ToString() ?? actorScope,
            actorUsername: actorScope,
            sessionId: sessionId,
            action: eventType,
            resource: entityName,
            resourceId: entityId,
            reason: reason is null ? null : "[REDACTED_FREE_TEXT]",
            correlationId: correlationId?.ToString() ?? Guid.NewGuid().ToString(),
            clientIp: null,
            userAgent: null,
            payloadSha256: payloadSha256,
            detailsJson: maskedDetails,
            isSuccess: isSuccess,
            timestampUtc: now);

        _dbContext.CentralAuditRecords.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken);
        Recorded.Add(1);
    }

    public async Task<AuditPageDto> QueryAuditRecordsAsync(
        AuditQueryDto query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = 0;
        if (query.PageSize is < 1 or > 100 || query.Filter?.Length > 200 || query.AsOfVersion < 0 ||
            query.Cursor is not null && (!int.TryParse(query.Cursor, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out offset) || offset > 100000))
        {
            throw new CentralDomainException("INVALID_QUERY", "query.invalid", "Audit query is outside its supported bounds.");
        }

        var term = query.Filter?.Trim().ToLowerInvariant() ?? string.Empty;
        var queryable = _dbContext.CentralAuditRecords.AsNoTracking().AsQueryable();
        if (term.Length > 0)
        {
            queryable = queryable.Where(a => a.Action.ToLower().Contains(term) ||
                a.ActorId.ToLower().Contains(term) || (a.ActorUsername != null && a.ActorUsername.ToLower().Contains(term)) ||
                a.Resource.ToLower().Contains(term) || (a.ResourceId != null && a.ResourceId.ToLower().Contains(term)) ||
                a.CorrelationId.ToLower().Contains(term));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var authorizedCount = 0;
        await foreach (var row in queryable.OrderByDescending(a => a.TimestampUtc).ThenByDescending(a => a.Id)
            .Select(a => new AuditRecordDto
            {
                Id = a.Id,
                ActorId = a.ActorId,
                ActorUsername = a.ActorUsername ?? string.Empty,
                SessionId = a.SessionId,
                Action = a.Action,
                Resource = a.Resource,
                ResourceId = a.ResourceId,
                Reason = a.Reason,
                CorrelationId = a.CorrelationId,
                DetailsJson = a.DetailsJson,
                IsSuccess = a.IsSuccess,
                TimestampUtc = a.TimestampUtc
            }).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            fingerprint.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
            fingerprint.AppendData(new byte[] { 10 });
            authorizedCount++;
        }

        var projectionVersion = (long)(BinaryPrimitives.ReadUInt64BigEndian(fingerprint.GetHashAndReset()) & 0x001fffffffffffffUL);
        var items = await queryable.OrderByDescending(a => a.TimestampUtc).ThenByDescending(a => a.Id)
            .Skip(offset).Take(query.PageSize)
            .Select(a => new AuditRecordDto
            {
                Id = a.Id,
                ActorId = a.ActorId,
                ActorUsername = a.ActorUsername ?? string.Empty,
                SessionId = a.SessionId,
                Action = a.Action,
                Resource = a.Resource,
                ResourceId = a.ResourceId,
                Reason = a.Reason,
                CorrelationId = a.CorrelationId,
                DetailsJson = a.DetailsJson,
                IsSuccess = a.IsSuccess,
                TimestampUtc = a.TimestampUtc
            }).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var nextOffset = offset + items.Count;
        return new AuditPageDto
        {
            Items = items,
            NextCursor = nextOffset <= 100000 && nextOffset < authorizedCount
                ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null,
            AsOfVersion = projectionVersion,
            AuthorizedCount = authorizedCount,
            ProjectionRevision = projectionVersion
        };
    }

    public string MaskPii(string jsonOrText)
    {
        if (string.IsNullOrWhiteSpace(jsonOrText))
        {
            return "{}";
        }

        try
        {
            var root = JsonNode.Parse(jsonOrText);
            Redact(root);
            return root?.ToJsonString() ?? "null";
        }
        catch (JsonException) { return "[REDACTED_NON_JSON]"; }
    }
    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(k => k.Key).ToArray())
            {
                if (obj[key] is JsonObject || obj[key] is JsonArray)
                {
                    Redact(obj[key]);
                }
                else if (!SafeKeys.Contains(key))
                {
                    obj[key] = "[REDACTED]";
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject || array[i] is JsonArray)
                {
                    Redact(array[i]);
                }
                else
                {
                    array[i] = "[REDACTED]";
                }
            }
        }
    }
}
