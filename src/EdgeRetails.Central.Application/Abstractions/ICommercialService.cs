using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Licensing;

namespace EdgeRetails.Central.Application.Abstractions;

public interface ICommercialService
{
    Task<Customer> CreateCustomerAsync(CreateCustomerCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task<Shop> RegisterShopAsync(RegisterShopCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task<PlanCatalogItem> CreateCatalogItemAsync(string code, string name, string purchaseTarget, string termKind, string? billingPeriodId, decimal unitPrice, string currency, Guid adminId, CancellationToken cancellationToken = default);
    Task<List<PlanCatalogItem>> GetCatalogItemsAsync(CancellationToken cancellationToken = default);
    Task<PricingSnapshot> CreatePriceSnapshotAsync(CreatePriceSnapshotCommand command, CancellationToken cancellationToken = default);
    Task<Order> CreateOrderAsync(CreateOrderCommand command, CancellationToken cancellationToken = default);
    Task<UploadSession> InitUploadSessionAsync(InitUploadSessionCommand command, Guid uploaderId, CancellationToken cancellationToken = default);
    Task AppendUploadChunkAsync(Guid sessionId, byte[] chunkData, CancellationToken cancellationToken = default);
    Task<PaymentEvidence> FinalizeUploadSessionAsync(Guid sessionId, FinalizeUploadSessionCommand command, Guid orderId, CancellationToken cancellationToken = default);
    Task<PaymentReviewRecord> ReviewPaymentAsync(ReviewPaymentCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task<CommercialLicense> IssueLicenseAsync(IssueLicenseCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task<CommercialLicense> RenewLicenseAsync(Guid licenseId, RenewLicenseCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task<CommercialLicense> ExtendLicenseAsync(Guid licenseId, ExtendLicenseCommand command, Guid adminId, CancellationToken cancellationToken = default);
    Task<CommercialEntitlement> RenewAddonAsync(Guid licenseId, RenewAddonCommand command, Guid adminId, CancellationToken cancellationToken = default);
}
