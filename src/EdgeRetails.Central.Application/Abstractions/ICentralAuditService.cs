using EdgeRetails.Central.Application.DTOs;

namespace EdgeRetails.Central.Application.Abstractions;

public interface ICentralAuditService
{
    Task RecordAuditAsync(
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
        CancellationToken cancellationToken = default);

    Task<AuditPageDto> QueryAuditRecordsAsync(
        AuditQueryDto query,
        CancellationToken cancellationToken = default);

    string MaskPii(string jsonOrText);
}
