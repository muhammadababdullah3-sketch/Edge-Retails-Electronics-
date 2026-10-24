using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Api.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Controllers;

[ApiController]
[Route("api/commercial/v1")]
public class CommercialController : ControllerBase
{
    private readonly ICommercialService _commercialService;
    private readonly IAdminAuthService _authService;
    private readonly ICentralDbContext _dbContext;

    public CommercialController(
        ICommercialService commercialService,
        IAdminAuthService authService,
        ICentralDbContext dbContext)
    {
        _commercialService = commercialService ?? throw new ArgumentNullException(nameof(commercialService));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    [HttpPost("customers")]

    public async Task<IActionResult> CreateCustomer([FromBody] CreateCustomerCommand command, CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var customer = await _commercialService.CreateCustomerAsync(command, adminId, cancellationToken);
        return Ok(customer);
    }

    [HttpPost("shops")]

    public async Task<IActionResult> RegisterShop([FromBody] RegisterShopCommand command, CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var shop = await _commercialService.RegisterShopAsync(command, adminId, cancellationToken);
        return Ok(shop);
    }

    [HttpPost("catalog-items")]
    public async Task<IActionResult> CreateCatalogItem([FromBody] CreateCatalogItemRequest request, CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var item = await _commercialService.CreateCatalogItemAsync(
            request.Code,
            request.Name,
            request.PurchaseTarget,
            request.TermKind,
            request.BillingPeriodId,
            request.UnitPrice,
            request.Currency,
            adminId,
            cancellationToken);

        return Ok(item);
    }

    [HttpGet("catalog-items")]
    public async Task<IActionResult> GetCatalogItems(CancellationToken cancellationToken)
    {
        var items = await _commercialService.GetCatalogItemsAsync(cancellationToken);
        return Ok(items);
    }

    [HttpPost("pricing-snapshots")]
    public async Task<IActionResult> CreatePriceSnapshot([FromBody] CreatePriceSnapshotCommand command, CancellationToken cancellationToken)
    {
        var snapshot = await _commercialService.CreatePriceSnapshotAsync(command, cancellationToken);
        return Ok(snapshot);
    }

    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await _commercialService.CreateOrderAsync(command, cancellationToken);
        return Ok(order);
    }

    [HttpPost("uploads/sessions")]
    public async Task<IActionResult> InitUploadSession([FromBody] InitUploadSessionCommand command, CancellationToken cancellationToken)
    {
        var uploaderId = await GetAdminUserIdAsync(cancellationToken);
        if (uploaderId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var session = await _commercialService.InitUploadSessionAsync(command, uploaderId, cancellationToken);
        return Ok(session);
    }

    [HttpPost("uploads/sessions/{sessionId:guid}/chunks")]
    public async Task<IActionResult> AppendUploadChunk(
        [FromRoute] Guid sessionId,
        CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        await Request.Body.CopyToAsync(memoryStream, cancellationToken);
        var chunkData = memoryStream.ToArray();

        await _commercialService.AppendUploadChunkAsync(sessionId, chunkData, cancellationToken);
        return Ok(new { success = true, bytesAppended = chunkData.Length });
    }

    [HttpPost("uploads/sessions/{sessionId:guid}/finalize")]
    public async Task<IActionResult> FinalizeUploadSession(
        [FromRoute] Guid sessionId,
        [FromQuery] Guid orderId,
        [FromBody] FinalizeUploadSessionCommand command,
        CancellationToken cancellationToken)
    {
        var evidence = await _commercialService.FinalizeUploadSessionAsync(sessionId, command, orderId, cancellationToken);
        return Ok(evidence);
    }

    [HttpPost("payments/review")]
    public async Task<IActionResult> ReviewPayment([FromBody] ReviewPaymentCommand command, CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        if (!ManualCommercialCommandGuard.IsValid(command.CommandMeta, "payment.review"))
        {
            return BadRequest(new { code = "INVALID_OPERATION_META", safeMessageKey = "operation.invalid_meta" });
        }

        var review = await _commercialService.ReviewPaymentAsync(command, adminId, cancellationToken);
        return Ok(review);
    }

    [HttpPost("licenses/issue")]
    [HttpPost("/api/admin/v1/licenses/issue")]
    public async Task<IActionResult> IssueLicense([FromBody] IssueLicenseCommand command, CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        if (!ManualCommercialCommandGuard.IsValid(command.CommandMeta, "license.issue"))
        {
            return BadRequest(new { code = "INVALID_OPERATION_META", safeMessageKey = "operation.invalid_meta" });
        }

        var license = await _commercialService.IssueLicenseAsync(command, adminId, cancellationToken);
        return Ok(license);
    }

    [HttpPost("licenses/{licenseId:guid}/renew")]
    [HttpPost("/api/admin/v1/licenses/{licenseId:guid}/renew")]
    public async Task<IActionResult> RenewLicense(
        [FromRoute] Guid licenseId,
        [FromBody] RenewLicenseCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var license = await _commercialService.RenewLicenseAsync(licenseId, command, adminId, cancellationToken);
        return Ok(license);
    }

    [HttpPost("licenses/{licenseId:guid}/extend")]
    [HttpPost("/api/admin/v1/licenses/{licenseId:guid}/extend")]
    public async Task<IActionResult> ExtendLicense(
        [FromRoute] Guid licenseId,
        [FromBody] ExtendLicenseCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var license = await _commercialService.ExtendLicenseAsync(licenseId, command, adminId, cancellationToken);
        return Ok(license);
    }

    [HttpPost("licenses/{licenseId:guid}/addons/renew")]
    public async Task<IActionResult> RenewAddon(
        [FromRoute] Guid licenseId,
        [FromBody] RenewAddonCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }

        var entitlement = await _commercialService.RenewAddonAsync(licenseId, command, adminId, cancellationToken);
        return Ok(entitlement);
    }

    [HttpPost("/api/admin/v1/licenses/{licenseId:guid}/addons/{entitlementId:guid}/renew")]
    public async Task<IActionResult> RenewTargetedAddon(
        [FromRoute] Guid licenseId,
        [FromRoute] Guid entitlementId,
        [FromBody] RenewAddonCommand command,
        CancellationToken cancellationToken)
    {
        var adminId = await GetAdminUserIdAsync(cancellationToken);
        if (adminId == Guid.Empty)
        {
            return Unauthorized(new { code = "ADMIN_AUTH_REQUIRED", safeMessageKey = "auth.admin_required" });
        }
        var entitlement = await _dbContext.CommercialEntitlements.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == entitlementId && e.LicenseId == licenseId, cancellationToken);
        if (entitlement is null)
        {
            return NotFound(new { code = "ENTITLEMENT_NOT_FOUND", safeMessageKey = "entitlement.not_found" });
        }
        var license = await _dbContext.CommercialLicenses.AsNoTracking().SingleOrDefaultAsync(l => l.Id == licenseId, cancellationToken);
        var order = await _dbContext.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == command.ApprovedOrderId, cancellationToken);
        if (license is null || order is null || order.ShopId != license.ShopId || order.PurchaseTarget != entitlement.Kind)
        {
            return BadRequest(new { code = "TARGET_MISMATCH", safeMessageKey = "entitlement.order_target_mismatch" });
        }
        var hasRecordedOperation = command.CommandMeta is not null && await _dbContext.CentralOperationRecords.AsNoTracking()
            .AnyAsync(r => r.ClientOperationId == command.CommandMeta.ClientOperationId, cancellationToken);
        if (command.CommandMeta is null || !hasRecordedOperation && command.CommandMeta.ExpectedVersion != entitlement.RowVersion)
        {
            return Conflict(new { code = "CONCURRENCY_CONFLICT", safeMessageKey = "entitlement.stale_version" });
        }
        var result = await _commercialService.RenewAddonAsync(licenseId, command, adminId, cancellationToken);
        return Ok(result);
    }

    private async Task<Guid> GetAdminUserIdAsync(CancellationToken cancellationToken)
    {
        var sessionId = ExtractSessionId();
        if (sessionId != Guid.Empty)
        {
            var session = await _authService.ValidateSessionAsync(sessionId, cancellationToken);
            if (session != null)
            {
                return session.AdminUserId;
            }
        }

        return Guid.Empty;
    }

    private Guid ExtractSessionId()
    {
        var credentials = CentralSessionCredentials.Parse(Request);
        return credentials.IsValid ? credentials.SessionId : Guid.Empty;
    }
}

public class CreateCatalogItemRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PurchaseTarget { get; set; } = string.Empty;
    public string TermKind { get; set; } = string.Empty;
    public string? BillingPeriodId { get; set; }
    public decimal UnitPrice { get; set; }
    public string Currency { get; set; } = "USD";
}
