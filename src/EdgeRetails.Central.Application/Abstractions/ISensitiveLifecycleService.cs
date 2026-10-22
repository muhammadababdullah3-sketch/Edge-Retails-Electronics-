using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Licensing;
using System.Text.Json;

namespace EdgeRetails.Central.Application.Abstractions;

public interface ISensitiveLifecycleService
{
    Task<SensitiveApprovalRequest> RequestApprovalAsync(
        string operationType,
        Guid targetEntityId,
        string targetPayloadSha256,
        string actionScope,
        Guid requesterAdminId,
        Guid stepUpSessionId,
        TimeSpan validity,
        CancellationToken cancellationToken = default);

    Task DecideApprovalAsync(
        Guid requestId,
        DecideSensitiveApprovalCommand command,
        Guid approverAdminId,
        CancellationToken cancellationToken = default);

    Task PauseLicenseAsync(Guid licenseId, LicenseLifecycleCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task ResumeLicenseAsync(Guid licenseId, LicenseLifecycleCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task SuspendLicenseAsync(Guid licenseId, LicenseLifecycleCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task ReinstateLicenseAsync(Guid licenseId, LicenseLifecycleCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task RevokeLicenseAsync(Guid licenseId, LicenseLifecycleCommand command, Guid adminId, CancellationToken cancellationToken = default);

    Task<OutcomeDto> ExecuteCanonicalLicenseLifecycleAsync(
        string operationType,
        Guid licenseId,
        Guid capabilityPolicyId,
        DateTimeOffset effectiveAtUtc,
        JsonElement payload,
        CanonicalDirectoryMeta meta,
        Guid adminId,
        Guid? adminSessionId,
        CancellationToken cancellationToken = default);

    Task<OutcomeDto> ConfigureCanonicalPolicyAsync(
        Guid routeLicenseId,
        PolicyDto policy,
        JsonElement payload,
        CanonicalDirectoryMeta meta,
        Guid adminId,
        Guid? adminSessionId,
        CancellationToken cancellationToken = default);

    Task<ShopEnforcementPolicy> ConfigurePolicyAsync(ConfigurePolicyCommand command, Guid adminId, CancellationToken cancellationToken = default);
}
