using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Licensing;
using System.Text.Json;

namespace EdgeRetails.Central.Application.Abstractions;

public interface IActivationService
{
    Task<Installation> RegisterInstallationAsync(RegisterInstallationCommand command, CancellationToken cancellationToken = default);
    Task<OutcomeDto> RegisterBootstrapInstallationAsync(CommandMetaDto meta, InstallRegistrationPayloadDto payload, CancellationToken cancellationToken = default);
    Task<OutcomeDto> RedeemBootstrapActivationAsync(CommandMetaDto meta, BootstrapActivationRedeemPayloadDto payload, CancellationToken cancellationToken = default);
    Task<ActivationIssuedDto> IssueActivationTokenAsync(IssueActivationTokenCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task<ActivationIssuedContractDto> IssueCanonicalActivationTokenAsync(JsonElement payload, CanonicalDirectoryMeta meta, Guid adminId, Guid? adminSessionId, CancellationToken cancellationToken = default);
    Task<int> ExpirePendingActivationDeliverySecretsAsync(int batchSize, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<BootstrapChallengeDto> GenerateBootstrapChallengeAsync(BootstrapChallengeRequestDto request, CancellationToken cancellationToken = default);
    Task<ActivationRedemption> RedeemActivationTokenAsync(RedeemActivationTokenCommand command, CancellationToken cancellationToken = default);
    Task ReviewActivationAsync(Guid redemptionId, ReviewActivationCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task<OutcomeDto> ReviewCanonicalActivationAsync(Guid redemptionId, CanonicalDirectoryMeta meta, CanonicalActivationDecisionDto decision, Guid adminId, Guid? adminSessionId, CancellationToken cancellationToken = default);
    Task<LicenseCertificate> IssueCertificateAsync(Guid installationId, Guid adminId, CancellationToken cancellationToken = default);
    Task<HardwareReview> ReportHardwareDiscrepancyAsync(Guid shopId, Guid installationId, string reportedFingerprint, string storedFingerprint, string reason, CancellationToken cancellationToken = default);
    Task DecideHardwareReviewAsync(Guid reviewId, DecideHardwareReviewCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task<PrimaryTransfer> InitiatePrimaryTransferAsync(InitiatePrimaryTransferCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task ApprovePrimaryTransferAsync(Guid transferId, Guid approverAdminId, Guid sensitiveApprovalId, CancellationToken cancellationToken = default);
    Task ApprovePrimaryTransferAsync(Guid transferId, Guid approverAdminId, Guid sensitiveApprovalId, ApprovePrimaryTransferCommand? command, CancellationToken cancellationToken = default);
    Task CompletePrimaryTransferAsync(Guid transferId, Guid adminId, CancellationToken cancellationToken = default);
    Task<SignedEntitlementDto> ReconcileInstallationAsync(ReconcileCommandDto command, CancellationToken cancellationToken = default);
}
