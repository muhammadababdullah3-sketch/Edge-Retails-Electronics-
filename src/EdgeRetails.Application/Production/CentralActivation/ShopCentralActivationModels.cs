namespace EdgeRetails.Application.Production.CentralActivation;

public enum ShopActivationDeliveryState
{
    Unconfigured,
    NoRequest,
    PendingAdminApproval,
    Rejected,
    ApprovedAwaitingCertificate,
    Ready,
    Failed
}

public sealed record ShopActivationSubmissionResult(
    bool IsSuccess,
    Guid? RedemptionId,
    Guid InstallationId,
    string? ErrorCode,
    string? Message);

public sealed record ShopActivationDeliveryStatus(
    ShopActivationDeliveryState State,
    Guid? RedemptionId,
    Guid? CertificateId,
    string? SignedLicenseJson,
    string? Message);

public interface IShopCentralActivationService
{
    bool IsConfigured { get; }

    Task<ShopActivationSubmissionResult> SubmitActivationTokenAsync(
        string activationToken,
        CancellationToken cancellationToken = default);

    Task<ShopActivationDeliveryStatus> PollDeliveryAsync(
        CancellationToken cancellationToken = default);
}
