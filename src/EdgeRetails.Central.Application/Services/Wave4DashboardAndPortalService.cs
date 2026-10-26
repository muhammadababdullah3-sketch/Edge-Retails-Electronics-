using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Common;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EdgeRetails.Central.Application.Services;

public class Wave4DashboardAndPortalService
{
    private readonly ICentralDbContext _dbContext;
    private readonly IAdminAuthService _authService;

    public Wave4DashboardAndPortalService(
        ICentralDbContext dbContext,
        IAdminAuthService authService)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    // CONE-T20: Portal Session Context & Session Administration
    public async Task<PortalSessionContextDto> GetPortalSessionContextAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _authService.ValidateSessionAsync(sessionId, cancellationToken)
            ?? throw new CentralDomainException("SESSION_INVALID", "auth.session_invalid", "Session is invalid or expired.");

        var user = await _dbContext.AdminUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == session.AdminUserId, cancellationToken)
            ?? throw new CentralDomainException("USER_NOT_FOUND", "auth.user_not_found", "Admin user not found.");

        var activeSessionsCount = await _dbContext.AdminSessions
            .AsNoTracking()
            .CountAsync(s => s.AdminUserId == session.AdminUserId && !s.IsRevoked && s.ExpiresAtUtc > DateTimeOffset.UtcNow, cancellationToken);

        var domainSession = await _dbContext.AdminSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == session.SessionId, cancellationToken);

        return new PortalSessionContextDto
        {
            AdminUserId = user.Id,
            Username = user.Username,
            Capabilities = session.Capabilities.ToList(),
            CreatedAtUtc = domainSession?.CreatedAtUtc ?? DateTimeOffset.UtcNow,
            ExpiresAtUtc = session.ExpiresAtUtc,
            ActiveSessionsCount = activeSessionsCount
        };
    }

    public async Task<(IReadOnlyList<object> Items, string? NextCursor, long ProjectionVersion, int AuthorizedCount)> GetAdminSessionsPageAsync(
        Guid callerSessionId,
        string? filter,
        int pageSize,
        int offset,
        long? asOfVersion,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100 || offset is < 0 or > 100000 || filter?.Length > 200 || asOfVersion < 0)
        {
            throw new CentralDomainException("INVALID_QUERY", "query.invalid", "Session listing query is outside its supported bounds.");
        }

        var caller = await _authService.ValidateSessionAsync(callerSessionId, cancellationToken)
            ?? throw new CentralDomainException("SESSION_INVALID", "auth.session_invalid", "Session is invalid or expired.");
        var mayViewAllAdministrators = caller.Capabilities.Contains(AdminCapabilities.AdminManage, StringComparer.OrdinalIgnoreCase);
        var term = filter?.Trim() ?? string.Empty;
        var query = from session in _dbContext.AdminSessions.AsNoTracking()
                    join user in _dbContext.AdminUsers.AsNoTracking() on session.AdminUserId equals user.Id
                    where (mayViewAllAdministrators || session.AdminUserId == caller.AdminUserId) &&
                          (term == string.Empty || user.Username.ToLower().Contains(term.ToLower()))
                    select new
                    {
                        session.Id,
                        session.AdminUserId,
                        user.Username,
                        session.CreatedAtUtc,
                        session.ExpiresAtUtc,
                        session.AbsoluteExpiresAtUtc,
                        session.LastActivityAtUtc,
                        session.MfaSatisfied,
                        session.IsRevoked,
                        session.RevokedAtUtc
                    };

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var authorizedCount = 0;
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var item in query.OrderBy(x => x.Id).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            fingerprint.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
            fingerprint.AppendData(new byte[] { 10 });
            authorizedCount++;
        }
        var projectionVersion = (long)(BinaryPrimitives.ReadUInt64BigEndian(fingerprint.GetHashAndReset()) & 0x001fffffffffffffUL);
        var rows = await query.OrderBy(x => x.Id).Skip(offset).Take(pageSize).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var items = rows.Select(row => (object)new
        {
            sessionId = row.Id,
            adminDisplayName = MaskSessionUsername(row.Username),
            row.CreatedAtUtc,
            row.ExpiresAtUtc,
            row.AbsoluteExpiresAtUtc,
            row.LastActivityAtUtc,
            row.MfaSatisfied,
            row.IsRevoked,
            row.RevokedAtUtc,
            isCurrentSession = row.Id == callerSessionId
        }).ToList();
        var nextOffset = offset + rows.Count;
        var nextCursor = nextOffset < authorizedCount ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return (items, nextCursor, projectionVersion, authorizedCount);
    }

    private static string MaskSessionUsername(string username) => string.IsNullOrEmpty(username)
        ? "***"
        : char.IsHighSurrogate(username[0]) && username.Length > 1
            ? username[..2] + "***"
            : username[..1] + "***";

    public async Task<bool> RevokeSessionAsync(Guid currentSessionId, Guid targetSessionId, CancellationToken cancellationToken = default)
    {
        var currentSession = await _authService.ValidateSessionAsync(currentSessionId, cancellationToken)
            ?? throw new CentralDomainException("SESSION_INVALID", "auth.session_invalid", "Calling session is invalid or expired.");

        var now = DateTimeOffset.UtcNow;
        var revokedSessionIds = new List<Guid>();

        if (targetSessionId == Guid.Empty)
        {
            // Revoke all OTHER sessions of the user
            var otherSessions = await _dbContext.AdminSessions
                .Where(s => s.AdminUserId == currentSession.AdminUserId && s.Id != currentSessionId && !s.IsRevoked)
                .ToListAsync(cancellationToken);

            foreach (var s in otherSessions)
            {
                s.Revoke("Revoked via portal session administration", now);
                revokedSessionIds.Add(s.Id);
            }
        }
        else
        {
            var target = await _dbContext.AdminSessions
                .FirstOrDefaultAsync(s => s.Id == targetSessionId, cancellationToken)
                ?? throw new CentralDomainException("SESSION_NOT_FOUND", "auth.target_session_not_found", "Target session not found.");

            if (target.AdminUserId != currentSession.AdminUserId && !currentSession.Capabilities.Contains(AdminCapabilities.AdminManage))
            {
                throw new CentralDomainException("FORBIDDEN", "auth.unauthorized_capability", "Cannot revoke another admin's session without AdminManage capability.");
            }

            target.Revoke("Revoked via portal session administration", now);
            revokedSessionIds.Add(target.Id);
        }

        var actor = await _dbContext.AdminUsers.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == currentSession.AdminUserId, cancellationToken)
            ?? throw new CentralDomainException("USER_NOT_FOUND", "auth.user_not_found", "Admin user not found.");
        var auditTargetId = targetSessionId == Guid.Empty ? currentSessionId : targetSessionId;
        var auditDetails = JsonSerializer.Serialize(new
        {
            eventType = "ADMIN_SESSION_REVOKED",
            adminUserId = currentSession.AdminUserId,
            sessionId = currentSessionId,
            resourceId = auditTargetId,
            revokedSessionCount = revokedSessionIds.Count
        });
        _dbContext.CentralAuditRecords.Add(CentralAuditRecord.Create(
            actorId: currentSession.AdminUserId.ToString("D"),
            actorUsername: actor.Username,
            sessionId: currentSessionId,
            action: "ADMIN_SESSION_REVOKED",
            resource: "AdminSession",
            resourceId: auditTargetId.ToString("D"),
            reason: "Portal session administration",
            correlationId: Guid.NewGuid().ToString("D"),
            clientIp: null,
            userAgent: null,
            payloadSha256: null,
            detailsJson: auditDetails,
            isSuccess: true,
            timestampUtc: now));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    // CONE-T21: Authoritative Metrics & Customer360 (with LIFETIME vs TIMED separation)
    public async Task<DashboardMetricsDto> GetDashboardMetricsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var threshold30Days = now.AddDays(30);

        var activeShopsCount = await _dbContext.Shops
            .AsNoTracking()
            .CountAsync(s => s.Status == Shop.StatusActive, cancellationToken);

        // LIFETIME commercial base count (strictly ValidUntilUtc == null)
        var lifetimeBaseCount = await _dbContext.CommercialLicenses
            .AsNoTracking()
            .CountAsync(l => l.TermKind == CommercialTerm.Lifetime && l.Status == CommercialLicense.StatusActive, cancellationToken);

        // FIXED_TERM base count
        var fixedTermBaseCount = await _dbContext.CommercialLicenses
            .AsNoTracking()
            .CountAsync(l => l.TermKind == CommercialTerm.FixedTerm && l.Status == CommercialLicense.StatusActive, cancellationToken);

        // Expiring add-on entitlements (within 30 days, still active)
        var expiringAddonsCount = await _dbContext.CommercialEntitlements
            .AsNoTracking()
            .CountAsync(a => a.Status == CommercialEntitlement.StatusActive && a.ValidUntilUtc != null && a.ValidUntilUtc > now && a.ValidUntilUtc <= threshold30Days, cancellationToken);

        // Expiring fixed-term bases (LIFETIME base is excluded!)
        var expiringFixedTermBasesCount = await _dbContext.CommercialLicenses
            .AsNoTracking()
            .CountAsync(l => l.TermKind == CommercialTerm.FixedTerm && l.ValidUntilUtc != null && l.ValidUntilUtc > now && l.ValidUntilUtc <= threshold30Days, cancellationToken);

        var pendingOrdersCount = await _dbContext.Orders
            .AsNoTracking()
            .CountAsync(o => o.Status == Order.StatusPendingPayment, cancellationToken);

        var pendingEvidenceReviewsCount = await _dbContext.PaymentEvidences
            .AsNoTracking()
            .CountAsync(e => e.ReviewStatus == PaymentEvidence.ReviewPending, cancellationToken);

        var pendingHardwareReviewsCount = await _dbContext.HardwareReviews
            .AsNoTracking()
            .CountAsync(h => h.Status == HardwareReview.StatusPending, cancellationToken);

        var pendingApprovalsCount = await _dbContext.SensitiveApprovalRequests
            .AsNoTracking()
            .CountAsync(a => a.Status == SensitiveApprovalRequest.StatusPending, cancellationToken);

        var openSupportTicketsCount = await _dbContext.SupportTickets
            .AsNoTracking()
            .CountAsync(t => t.Status == "OPEN", cancellationToken);

        return new DashboardMetricsDto
        {
            ActiveShopsCount = activeShopsCount,
            LifetimeBaseCount = lifetimeBaseCount,
            FixedTermBaseCount = fixedTermBaseCount,
            ExpiringAddonsCount = expiringAddonsCount,
            ExpiringFixedTermBasesCount = expiringFixedTermBasesCount,
            PendingOrdersCount = pendingOrdersCount,
            PendingEvidenceReviewsCount = pendingEvidenceReviewsCount,
            PendingHardwareReviewsCount = pendingHardwareReviewsCount,
            PendingApprovalsCount = pendingApprovalsCount,
            OpenSupportTicketsCount = openSupportTicketsCount
        };
    }

    public async Task<Customer360Dto> GetCustomer360Async(Guid shopId, CancellationToken cancellationToken = default)
    {
        var shop = await _dbContext.Shops
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == shopId, cancellationToken)
            ?? throw new CentralDomainException("SHOP_NOT_FOUND", "shop.not_found", $"Shop '{shopId}' not found.");

        var commercialLicenses = await _dbContext.CommercialLicenses
            .AsNoTracking()
            .Where(l => l.ShopId == shopId)
            .OrderByDescending(l => l.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var activeLicenses = commercialLicenses
            .Where(l => l.Status == CommercialLicense.StatusActive)
            .ToList();
        var license = activeLicenses.FirstOrDefault();

        List<CommercialEntitlementSummaryDto> addons = new();
        if (activeLicenses.Count > 0)
        {
            var licenseIds = activeLicenses.Select(l => l.Id).ToList();
            var now = DateTimeOffset.UtcNow;
            var rawAddons = await _dbContext.CommercialEntitlements
                .AsNoTracking()
                .Where(a => licenseIds.Contains(a.LicenseId))
                .ToListAsync(cancellationToken);

            addons = rawAddons.Select(a => new CommercialEntitlementSummaryDto
            {
                Id = a.Id,
                Kind = a.Kind,
                Status = a.Status,
                ValidUntilUtc = a.ValidUntilUtc,
                RowVersion = a.RowVersion,
                IsExpiringSoon = a.Status == CommercialEntitlement.StatusActive && a.ValidUntilUtc.HasValue && a.ValidUntilUtc <= now.AddDays(30)
            }).ToList();
        }

        var installations = await _dbContext.Installations
            .AsNoTracking()
            .Where(i => i.ShopId == shopId)
            .OrderByDescending(i => i.RegisteredAtUtc)
            .Select(i => new InstallationSummaryDto
            {
                Id = i.Id,
                MachineFingerprint = i.MachineFingerprint,
                Hostname = i.Hostname,
                OsPlatform = i.OsPlatform,
                Status = i.Status,
                RegisteredAtUtc = i.RegisteredAtUtc
            })
            .ToListAsync(cancellationToken);

        var recentOrders = await _dbContext.Orders
            .AsNoTracking()
            .Where(o => o.ShopId == shopId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(10)
            .Select(o => new OrderSummaryDto
            {
                Id = o.Id,
                PurchaseTarget = o.PurchaseTarget,
                TermKind = o.TermKind,
                Status = o.Status,
                RowVersion = o.RowVersion,
                BaseLicenseId = o.BaseLicenseId,
                ExistingEntitlementId = o.ExistingEntitlementId,
                IssuedLicenseId = _dbContext.CommercialLicenses
                    .Where(license => license.InitialOrderId == o.Id)
                    .Select(license => (Guid?)license.Id)
                    .FirstOrDefault(),
                TotalAmount = o.TotalAmount,
                Currency = o.Currency,
                CreatedAtUtc = o.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var recentTickets = await _dbContext.SupportTickets
            .AsNoTracking()
            .Where(t => t.ShopId == shopId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(10)
            .Select(t => new SupportTicketSummaryDto
            {
                Id = t.Id,
                Subject = t.Subject,
                Status = t.Status,
                CreatedAtUtc = t.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        var policy = await _dbContext.ShopEnforcementPolicies
            .AsNoTracking()
            .Where(p => p.ShopId == shopId)
            .OrderByDescending(p => p.PolicyVersion)
            .FirstOrDefaultAsync(cancellationToken);

        return new Customer360Dto
        {
            ShopId = shop.Id,
            ShopCode = shop.ShopCode,
            ShopName = shop.ShopName,
            Status = shop.Status,
            CreatedAtUtc = shop.CreatedAtUtc,
            CommercialLicense = license != null ? new CommercialLicenseSummaryDto
            {
                Id = license.Id,
                TermKind = license.TermKind,
                Status = license.Status,
                ValidUntilUtc = license.ValidUntilUtc,
                AuthorityRevision = license.AuthorityRevision
            } : null,
            CommercialLicenses = commercialLicenses.Select(activeLicense => new CommercialLicenseSummaryDto
            {
                Id = activeLicense.Id,
                TermKind = activeLicense.TermKind,
                Status = activeLicense.Status,
                ValidUntilUtc = activeLicense.ValidUntilUtc,
                AuthorityRevision = activeLicense.AuthorityRevision
            }).ToList(),
            Addons = addons,
            Installations = installations,
            RecentOrders = recentOrders,
            RecentTickets = recentTickets,
            EnforcementPolicy = policy != null ? new PolicySummaryDto
            {
                EnforcementMode = policy.EnforcementMode,
                PolicyVersion = policy.PolicyVersion,
                ExtensionEndUtc = policy.ExtensionEndUtc
            } : null
        };
    }

    public async Task<List<QueueItemDto>> GetDashboardQueueAsync(string queueType, int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var skip = (pageNumber - 1) * pageSize;
        return await BuildDashboardQueueQuery(queueType)
            .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.EntityId)
            .Skip(skip).Take(pageSize).ToListAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<QueueItemDto> Items, string? NextCursor, long ProjectionVersion, long AuthorizedCount)> GetDashboardQueuePageAsync(
        string queueType,
        string? filter,
        int pageSize,
        int offset,
        long? asOfVersion,
        CancellationToken cancellationToken = default,
        bool includeSensitiveApprovalDetails = false,
        bool includePaymentReviewDetails = false)
    {
        if (pageSize is < 1 or > 100 || offset is < 0 or > 100000 || filter?.Length > 200 || asOfVersion < 0)
        {
            throw new CentralDomainException("INVALID_QUERY", "query.invalid", "Queue listing query is outside its supported bounds.");
        }

        var term = filter?.Trim() ?? string.Empty;
        var hasEntityIdFilter = Guid.TryParse(term, out var entityIdFilter);
        var query = BuildDashboardQueueQuery(queueType, includeSensitiveApprovalDetails, includePaymentReviewDetails).Where(item =>
            term == string.Empty || item.Title.Contains(term) || item.Status == term || hasEntityIdFilter && item.EntityId == entityIdFilter);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        long authorizedCount = 0;
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var item in query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.EntityId)
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            fingerprint.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
            fingerprint.AppendData(new byte[] { 10 });
            authorizedCount++;
        }

        var projectionVersion = (long)(BinaryPrimitives.ReadUInt64BigEndian(fingerprint.GetHashAndReset()) & 0x001fffffffffffffUL);
        var rows = await query.OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.EntityId)
            .Skip(offset).Take(pageSize).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var nextOffset = offset + rows.Count;
        var nextCursor = nextOffset < authorizedCount && nextOffset <= 100000
            ? nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return (rows, nextCursor, projectionVersion, authorizedCount);
    }

    private IQueryable<QueueItemDto> BuildDashboardQueueQuery(string queueType, bool includeSensitiveApprovalDetails = false, bool includePaymentReviewDetails = false)
    {
        return queueType.ToUpperInvariant() switch
        {
            "ACTIVATION_REVIEW" =>
                from redemption in _dbContext.ActivationRedemptions.AsNoTracking()
                join token in _dbContext.ActivationTokens.AsNoTracking() on redemption.TokenId equals token.Id
                join license in _dbContext.CommercialLicenses.AsNoTracking() on token.LicenseId equals license.Id
                where redemption.Status == ActivationRedemption.StatusPending
                select new QueueItemDto
                {
                    EntityId = redemption.Id,
                    QueueType = "ACTIVATION_REVIEW",
                    ShopId = license.ShopId,
                    ShopName = _dbContext.Shops.Where(s => s.Id == license.ShopId).Select(s => s.ShopName).FirstOrDefault(),
                    CustomerName = _dbContext.Shops.Where(s => s.Id == license.ShopId).Join(_dbContext.Customers, s => s.CustomerId, c => c.Id, (s, c) => c.LegalName).FirstOrDefault(),
                    Title = "Pending activation review",
                    Status = redemption.Status,
                    CreatedAtUtc = redemption.RedeemedAtUtc,
                    ReasonOrSummary = redemption.DecisionReasonCode,
                    RequiresDualApproval = false,
                    ResourceVersion = redemption.RowVersion,
                    ValidationSnapshotVersion = redemption.ValidationSnapshotVersion
                },
            "PAYMENT_REVIEW" => _dbContext.PaymentEvidences.AsNoTracking()
                .Where(e => e.ReviewStatus == PaymentEvidence.ReviewPending)
                .Select(e => new QueueItemDto
                {
                    EntityId = e.Id, QueueType = "PAYMENT_REVIEW", ShopId = e.ShopId,
                    ShopName = _dbContext.Shops.Where(s => s.Id == e.ShopId).Select(s => s.ShopName).FirstOrDefault(),
                    CustomerName = _dbContext.Shops.Where(s => s.Id == e.ShopId).Join(_dbContext.Customers, s => s.CustomerId, c => c.Id, (s, c) => c.LegalName).FirstOrDefault(),
                    OrderId = includePaymentReviewDetails ? e.OrderId : null,
                    ResourceVersion = includePaymentReviewDetails ? e.RowVersion : null,
                    Title = $"Payment Proof Review - {e.MimeType}", Status = e.ReviewStatus,
                    CreatedAtUtc = e.UploadedAtUtc, ReasonOrSummary = $"File length: {e.ByteLength} bytes",
                    RequiresDualApproval = false
                }),
            "HARDWARE_REVIEW" => _dbContext.HardwareReviews.AsNoTracking()
                .Where(h => h.Status == HardwareReview.StatusPending)
                .Select(h => new QueueItemDto
                {
                    EntityId = h.Id, QueueType = "HARDWARE_REVIEW", ShopId = h.ShopId,
                    ShopName = _dbContext.Shops.Where(s => s.Id == h.ShopId).Select(s => s.ShopName).FirstOrDefault(),
                    CustomerName = _dbContext.Shops.Where(s => s.Id == h.ShopId).Join(_dbContext.Customers, s => s.CustomerId, c => c.Id, (s, c) => c.LegalName).FirstOrDefault(),
                    Title = $"Hardware Change Review - Machine {h.ReportedMachineFingerprint}", Status = h.Status,
                    CreatedAtUtc = h.CreatedAtUtc, ReasonOrSummary = h.DiscrepancyReason,
                    RequiresDualApproval = true,
                    ResourceVersion = h.RowVersion,
                    InstallationId = h.InstallationId,
                    PreviousFingerprintId = h.StoredMachineFingerprintId,
                    CurrentFingerprintId = h.ReportedMachineFingerprintId
                }),
            "PRIMARY_TRANSFER" => _dbContext.PrimaryTransfers.AsNoTracking()
                .Where(t => t.Status == PrimaryTransfer.StatusPendingApproval || t.Status == PrimaryTransfer.StatusApproved)
                .Select(t => new QueueItemDto
                {
                    EntityId = t.Id, QueueType = "PRIMARY_TRANSFER", ShopId = t.ShopId,
                    ShopName = _dbContext.Shops.Where(s => s.Id == t.ShopId).Select(s => s.ShopName).FirstOrDefault(),
                    CustomerName = _dbContext.Shops.Where(s => s.Id == t.ShopId).Join(_dbContext.Customers, s => s.CustomerId, c => c.Id, (s, c) => c.LegalName).FirstOrDefault(),
                    Title = $"Primary transfer {t.SourceInstallationId:D} to {t.TargetInstallationId:D}",
                    Status = t.Status, CreatedAtUtc = t.CreatedAtUtc, ReasonOrSummary = t.TransferReason,
                    RequiresDualApproval = true, ResourceVersion = t.RowVersion,
                    OldInstallationId = t.SourceInstallationId, NewInstallationId = t.TargetInstallationId
                }),
            "DUAL_APPROVAL" => _dbContext.SensitiveApprovalRequests.AsNoTracking()
                .Where(a => a.Status == SensitiveApprovalRequest.StatusPending)
                .Select(a => new QueueItemDto
                {
                    EntityId = a.Id, QueueType = "DUAL_APPROVAL", ShopId = Guid.Empty,
                    Title = $"Sensitive Action: {a.OperationType}", Status = a.Status,
                    CreatedAtUtc = a.RequestedAtUtc, ReasonOrSummary = includeSensitiveApprovalDetails ? a.RequestReason : a.ActionScope,
                    RequiresDualApproval = true,
                    ResourceVersion = a.RowVersion,
                    ApprovalRequesterAdminId = includeSensitiveApprovalDetails ? a.RequesterAdminId : null,
                    ApprovalTargetActionType = includeSensitiveApprovalDetails ? a.OperationType : null,
                    ApprovalTargetId = includeSensitiveApprovalDetails ? a.TargetEntityId : null,
                    ApprovalTargetPayloadSha256 = includeSensitiveApprovalDetails ? a.TargetPayloadSha256 : null,
                    ApprovalExpiresAtUtc = includeSensitiveApprovalDetails ? a.ExpiresAtUtc : null
                }),
            "SUPPORT_INBOX" => _dbContext.SupportTickets.AsNoTracking()
                .Where(t => t.Status == "OPEN")
                .Select(t => new QueueItemDto
                {
                    EntityId = t.Id, QueueType = "SUPPORT_INBOX", ShopId = t.ShopId,
                    ShopName = _dbContext.Shops.Where(s => s.Id == t.ShopId).Select(s => s.ShopName).FirstOrDefault(),
                    CustomerName = _dbContext.Shops.Where(s => s.Id == t.ShopId).Join(_dbContext.Customers, s => s.CustomerId, c => c.Id, (s, c) => c.LegalName).FirstOrDefault(),
                    Title = t.Subject, Status = t.Status, CreatedAtUtc = t.CreatedAtUtc,
                    ReasonOrSummary = t.Summary, RequiresDualApproval = false
                }),
            _ => throw new CentralDomainException("INVALID_QUEUE_TYPE", "dashboard.invalid_queue", $"Unknown queue type '{queueType}'.")
        };
    }

    // CONE-T22: Action Consequence Preview
    public async Task<ActionPreviewResponseDto> PreviewActionAsync(ActionPreviewRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var action = request.ActionType?.Trim().ToUpperInvariant();
        if (action is not ("REVOKE_LICENSE" or "TRANSFER_PRIMARY" or "REPLACE_HARDWARE" or "RENEW_ADDON" or "WATERMARK_POLICY"))
        {
            throw new CentralDomainException("UNSUPPORTED_ACTION", "preview.unsupported_action", "The action has no supported consequence preview.");
        }
        if (request.ShopId == Guid.Empty || request.TargetEntityId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_PREVIEW_TARGET", "preview.target_required", "Persisted shop and target identities are required.");
        }

        var parameters = ParsePreviewParameters(request.ProposedParametersJson);
        ValidatePreviewIdentity(parameters, "shopId", request.ShopId);
        ValidatePreviewIdentity(parameters, "targetEntityId", request.TargetEntityId);
        var shop = await _dbContext.Shops.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.ShopId, cancellationToken)
            ?? throw new CentralDomainException("SHOP_NOT_FOUND", "shop.not_found", "The preview shop does not exist.");
        var response = new ActionPreviewResponseDto { ActionType = action };

        switch (action)
        {
            case "REVOKE_LICENSE":
            {
                var license = await PreviewLicenseAsync(request.TargetEntityId, shop.Id, cancellationToken);
                ValidatePreviewVersion(parameters, "expectedVersion", license.RowVersion);
                ValidatePreviewVersion(parameters, "authorityRevision", license.AuthorityRevision);
                await ValidatePreviewPolicyVersionAsync(parameters, license, cancellationToken);
                if (license.Status == CommercialLicense.StatusRevoked)
                {
                    throw new CentralDomainException("ALREADY_REVOKED", "license.already_revoked", "The license is already revoked.");
                }
                response.AffectedInstallationsCount = await CountPreviewInstallationsAsync(license.Id, cancellationToken);
                response.IsDestructive = true;
                response.RequiresStepUp = true;
                response.RequiresDualApproval = true;
                response.ConsequenceSummary = $"Revoking license {license.Id:D} for shop '{shop.ShopName}' changes its current {license.Status} state to REVOKED and advances authority revision {license.AuthorityRevision}. {response.AffectedInstallationsCount} installation(s) are bound to this license. {PreviewTerm(license)}";
                response.Warnings.Add("Offline machines apply updated signed authority when it is received; their current signed offline limits remain in force. This preview does not contact or change an installation.");
                response.Warnings.Add("Renewal or an add-on purchase does not reverse base-license revocation. The mutation revalidates current state and a separate approver bound to the exact action.");
                break;
            }

            case "TRANSFER_PRIMARY":
            {
                var proposedDecision = PreviewString(parameters, "decision", "APPROVE");
                if (proposedDecision is not ("REQUEST" or "APPROVE" or "REJECT" or "CANCEL" or "COMPLETE"))
                {
                    throw InvalidPreviewParameters();
                }
                var transfer = await _dbContext.PrimaryTransfers.AsNoTracking().SingleOrDefaultAsync(t => t.Id == request.TargetEntityId, cancellationToken)
                    ?? throw new CentralDomainException("TRANSFER_NOT_FOUND", "transfer.not_found", "The transfer case does not exist.");
                ValidatePreviewShop(transfer.ShopId, shop.Id);
                ValidatePreviewVersion(parameters, "expectedVersion", transfer.RowVersion);
                var completionPreview = proposedDecision == "COMPLETE";
                if (completionPreview
                    ? transfer.Status != PrimaryTransfer.StatusApproved || transfer.ContractDecision != "APPROVE" ||
                      transfer.RecoveryType is not ("STANDARD_TRANSFER" or "LOST_HARDWARE_RECOVERY") || transfer.VerificationEvidenceId is not Guid evidenceId || evidenceId == Guid.Empty
                    : transfer.Status != PrimaryTransfer.StatusPendingApproval)
                {
                    throw new CentralDomainException("INVALID_STATE_TRANSITION", completionPreview ? "transfer.not_ready" : "transfer.not_pending",
                        completionPreview ? "Transfer completion preview requires a persisted, evidence-backed APPROVE decision." : "A transfer decision can only be previewed while the transfer is pending approval.");
                }
                if (!completionPreview && proposedDecision == "REQUEST" && transfer.ContractDecision is not null)
                {
                    throw new CentralDomainException("INVALID_STATE_TRANSITION", "transfer.request_already_recorded", "The transfer request has already been recorded.");
                }
                if (completionPreview)
                {
                    var evidence = await _dbContext.EncryptedObjects.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == transfer.VerificationEvidenceId, cancellationToken)
                        ?? throw new CentralDomainException("EVIDENCE_NOT_FOUND", "support.evidence_not_found", "Primary transfer verification evidence was not found.");
                    if (evidence.ShopId != shop.Id || evidence.Purpose != EncryptedObjectRecord.PurposeSupportAttachment || evidence.IsTombstoned)
                    {
                        throw new CentralDomainException("EVIDENCE_SCOPE_INVALID", "support.evidence_scope_invalid", "Primary transfer evidence must be an active support attachment for the same shop.");
                    }
                }
                var source = await _dbContext.Installations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == transfer.SourceInstallationId, cancellationToken)
                    ?? throw new CentralDomainException("SOURCE_NOT_FOUND", "transfer.source_not_found", "The transfer source does not exist.");
                var target = await _dbContext.Installations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == transfer.TargetInstallationId, cancellationToken)
                    ?? throw new CentralDomainException("TARGET_NOT_FOUND", "transfer.target_not_found", "The transfer target does not exist.");
                ValidatePreviewShop(source.ShopId, shop.Id);
                ValidatePreviewShop(target.ShopId, shop.Id);
                if (source.LicenseId != target.LicenseId || source.Id == target.Id)
                {
                    throw new CentralDomainException("LICENSE_MISMATCH", "transfer.license_mismatch", "Distinct transfer installations must belong to the same license.");
                }
                if (source.Status is Installation.StatusRevoked or Installation.StatusReplaced || target.Status is Installation.StatusRevoked or Installation.StatusReplaced)
                {
                    throw new CentralDomainException("INVALID_STATE_TRANSITION", "transfer.installation_unavailable", "A retired or revoked installation cannot participate in this transfer.");
                }
                var license = await PreviewLicenseAsync(source.LicenseId, shop.Id, cancellationToken);
                ValidatePreviewVersion(parameters, "authorityRevision", license.AuthorityRevision);
                await ValidatePreviewPolicyVersionAsync(parameters, license, cancellationToken);
                if (completionPreview && (license.Status != CommercialLicense.StatusActive || license.ValidFromUtc > DateTimeOffset.UtcNow ||
                    (license.ValidUntilUtc.HasValue && license.ValidUntilUtc.Value <= DateTimeOffset.UtcNow)))
                {
                    throw new CentralDomainException("LICENSE_NOT_ELIGIBLE", "transfer.not_eligible", "Primary transfer completion preview requires a currently active, commercially valid license.");
                }
                response.AffectedInstallationsCount = 2;
                response.IsDestructive = true;
                response.RequiresStepUp = proposedDecision is "APPROVE" or "COMPLETE";
                response.RequiresDualApproval = proposedDecision == "APPROVE";
                response.ConsequenceSummary = completionPreview
                    ? $"Completing approved transfer case {transfer.Id:D} at version {transfer.RowVersion} retires source installation {source.Id:D}, revokes its active certificates, and activates target installation {target.Id:D} for the same shop and currently valid license."
                    : proposedDecision == "APPROVE"
                    ? $"Approving transfer case {transfer.Id:D} at version {transfer.RowVersion} authorizes later completion, which retires source installation {source.Id:D}, revokes its active certificates, and activates target installation {target.Id:D} for the same shop and license."
                    : $"Recording {proposedDecision} for transfer case {transfer.Id:D} at version {transfer.RowVersion} changes the persisted transfer decision; it does not complete a transfer or issue a certificate.";
                response.Warnings.Add("The actual command revalidates current commercial eligibility, installation identities and versions. Preview grants no certificate or transaction authority.");
                if (completionPreview)
                {
                    response.Warnings.Add("Completion is a separate mutation after the persisted independent approval; a fresh execution-session MFA grant is still required for this exact completion.");
                }
                break;
            }

            case "REPLACE_HARDWARE":
            {
                var proposedDecision = PreviewString(parameters, "decision", "APPROVE_REBIND");
                if (proposedDecision is not ("APPROVE_REBIND" or "RECOMMEND_TRANSFER" or "REQUEST_INFORMATION" or "REJECT"))
                {
                    throw InvalidPreviewParameters();
                }
                var review = await _dbContext.HardwareReviews.AsNoTracking().SingleOrDefaultAsync(h => h.Id == request.TargetEntityId, cancellationToken)
                    ?? throw new CentralDomainException("HARDWARE_REVIEW_NOT_FOUND", "hw.review_not_found", "The hardware review does not exist.");
                ValidatePreviewShop(review.ShopId, shop.Id);
                ValidatePreviewVersion(parameters, "expectedVersion", review.RowVersion);
                if (review.Status != HardwareReview.StatusPending)
                {
                    throw new CentralDomainException("NOT_PENDING", "hw.not_pending", "A decided hardware review cannot be previewed as pending.");
                }
                var installation = await _dbContext.Installations.AsNoTracking().SingleOrDefaultAsync(i => i.Id == review.InstallationId, cancellationToken)
                    ?? throw new CentralDomainException("INSTALLATION_NOT_FOUND", "install.not_found", "The reviewed installation does not exist.");
                ValidatePreviewShop(installation.ShopId, shop.Id);
                var license = await PreviewLicenseAsync(installation.LicenseId, shop.Id, cancellationToken);
                ValidatePreviewVersion(parameters, "authorityRevision", license.AuthorityRevision);
                response.AffectedInstallationsCount = 1;
                response.IsDestructive = true;
                var requiresIndependentApproval = proposedDecision == "APPROVE_REBIND";
                response.RequiresStepUp = requiresIndependentApproval;
                response.RequiresDualApproval = requiresIndependentApproval;
                response.ConsequenceSummary = proposedDecision == "APPROVE_REBIND"
                    ? $"Approving hardware rebind for review {review.Id:D} at version {review.RowVersion} permits installation {installation.Id:D} to reconcile with the reported fingerprint after independent approval."
                    : $"Recording {proposedDecision} for hardware review {review.Id:D} at version {review.RowVersion} does not change the installation's trusted fingerprint.";
                response.Warnings.Add("The stored and reported fingerprint, current commercial authority, approval and certificate eligibility must be revalidated by the actual decision. Preview alone does not change hardware trust.");
                break;
            }

            case "RENEW_ADDON":
            {
                var entitlement = await _dbContext.CommercialEntitlements.AsNoTracking().SingleOrDefaultAsync(e => e.Id == request.TargetEntityId, cancellationToken)
                    ?? throw new CentralDomainException("ENTITLEMENT_NOT_FOUND", "entitlement.not_found", "The add-on entitlement does not exist.");
                var license = await PreviewLicenseAsync(entitlement.LicenseId, shop.Id, cancellationToken);
                ValidatePreviewIdentity(parameters, "licenseId", license.Id);
                ValidatePreviewIdentity(parameters, "entitlementId", entitlement.Id);
                ValidatePreviewVersion(parameters, "expectedVersion", entitlement.RowVersion);
                ValidatePreviewVersion(parameters, "expectedEntitlementVersion", entitlement.RowVersion);
                ValidatePreviewVersion(parameters, "authorityRevision", license.AuthorityRevision);
                if (license.Status == CommercialLicense.StatusRevoked)
                {
                    throw new CentralDomainException("REVOKED_LICENSE", "license.revoked", "An add-on renewal cannot restore a revoked base license.");
                }
                var proposedUntil = PreviewDate(parameters, "newValidUntilUtc");
                if (proposedUntil.HasValue && entitlement.ValidUntilUtc.HasValue && proposedUntil <= entitlement.ValidUntilUtc)
                {
                    throw new CentralDomainException("INVALID_RENEWAL_DATE", "entitlement.invalid_renewal_date", "The proposed add-on expiry must follow its current expiry.");
                }
                var approvedOrderId = PreviewGuid(parameters, "approvedOrderId");
                if (approvedOrderId.HasValue)
                {
                    var order = await _dbContext.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == approvedOrderId.Value, cancellationToken)
                        ?? throw new CentralDomainException("ORDER_NOT_FOUND", "entitlement.order_not_found", "The proposed renewal order does not exist.");
                    if (order.ShopId != shop.Id || order.PurchaseTarget != entitlement.Kind || order.Status != Order.StatusPaidApproved)
                    {
                        throw new CentralDomainException("INVALID_ADDON_ORDER", "entitlement.invalid_order", "Renewal requires an approved order for this shop and this add-on kind.");
                    }
                }
                response.IsDestructive = false;
                response.AffectedInstallationsCount = await CountPreviewInstallationsAsync(license.Id, cancellationToken);
                response.RequiresStepUp = false;
                response.RequiresDualApproval = false;
                response.ConsequenceSummary = $"Renewal targets {entitlement.Kind} entitlement {entitlement.Id:D}, currently {entitlement.Status} at version {entitlement.RowVersion}, with expiry {PreviewExpiry(entitlement.ValidUntilUtc)}. " +
                    (proposedUntil.HasValue ? $"The proposed new expiry is {proposedUntil.Value:O}. " : "The mutation must supply the approved order and exact new add-on expiry. ") +
                    (license.TermKind == CommercialTerm.Lifetime ? "The LIFETIME base license remains completely unaffected: it has no scheduled commercial expiry." : $"The FIXED_TERM base license expiry remains {PreviewExpiry(license.ValidUntilUtc)}; renewing this add-on does not renew the base.");
                response.Warnings.Add("Only this persisted add-on is renewed. The actual command rechecks the approved order, shop, kind, current entitlement and idempotency identity.");
                break;
            }

            case "WATERMARK_POLICY":
            {
                var license = await PreviewLicenseAsync(request.TargetEntityId, shop.Id, cancellationToken);
                ValidatePreviewVersion(parameters, "expectedVersion", license.RowVersion);
                ValidatePreviewVersion(parameters, "authorityRevision", license.AuthorityRevision);
                var current = await _dbContext.ShopEnforcementPolicies.AsNoTracking()
                    .Where(p => p.ShopId == shop.Id && p.LicenseId == license.Id)
                    .OrderByDescending(p => p.PolicyVersion).FirstOrDefaultAsync(cancellationToken);
                ValidatePreviewVersion(parameters, "policyVersion", current?.PolicyVersion ?? 0);
                PolicyDto? proposed = null;
                if (parameters.HasValue && parameters.Value.TryGetProperty("policy", out var policyElement))
                {
                    try { proposed = policyElement.Deserialize<PolicyDto>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
                    catch (JsonException) { throw InvalidPreviewParameters(); }
                    if (proposed is null) { throw InvalidPreviewParameters(); }
                    ValidatePreviewShop(proposed.ShopId, shop.Id);
                    if (proposed.LicenseId != license.Id) { throw new CentralDomainException("TARGET_MISMATCH", "preview.target_mismatch", "Proposed policy targets a different license."); }
                    if (proposed.PolicyVersion <= (current?.PolicyVersion ?? 0)) { throw new CentralDomainException("NON_MONOTONIC_VERSION", "policy.non_monotonic_version", "The proposed policy version must follow the persisted policy."); }
                }
                if (current is null && proposed is null)
                {
                    throw new CentralDomainException("POLICY_NOT_CONFIGURED", "policy.not_configured", "No persisted or explicit proposed enforcement policy is available for preview.");
                }
                var mode = proposed?.EnforcementMode ?? current!.EnforcementMode;
                var end = proposed is null ? current!.ExtensionEndUtc : proposed.ExtensionEndUtc;
                var after = proposed?.AfterExtensionPolicyId ?? current!.AfterExtensionPolicyId;
                var lease = proposed?.LeaseDurationSeconds ?? current!.LeaseDurationSeconds;
                var grace = proposed?.OfflineGraceSeconds ?? current!.OfflineGraceSeconds;
                var rollback = proposed?.RollbackToleranceSeconds ?? current!.RollbackToleranceSeconds;
                var capability = proposed?.CapabilityPolicyId ?? current!.CapabilityPolicyId;
                if (mode is not (ShopEnforcementPolicy.FullStop or ShopEnforcementPolicy.ConfigurableExtension) || lease <= 0 || grace < 86400 || rollback < 0 || capability == Guid.Empty ||
                    mode == ShopEnforcementPolicy.ConfigurableExtension && (!end.HasValue || after == Guid.Empty))
                {
                    throw new CentralDomainException("POLICY_NOT_CONFIGURED", "policy.configuration_required", "The policy requires an explicit mode, valid lease/grace values, capability policy and extension/after-extension settings.");
                }
                response.AffectedInstallationsCount = await CountPreviewInstallationsAsync(license.Id, cancellationToken);
                response.IsDestructive = mode == ShopEnforcementPolicy.FullStop;
                response.RequiresStepUp = true;
                response.RequiresDualApproval = RequiresConfiguredPreviewApproval("license.policy", "WATERMARK_POLICY");
                response.ConsequenceSummary = $"{(proposed is null ? "Current" : "Proposed")} policy v{proposed?.PolicyVersion ?? current!.PolicyVersion} for license {license.Id:D} uses {mode}. " +
                    (mode == ShopEnforcementPolicy.FullStop ? "FULL_STOP halts sale transactions when the signed policy's applicable commercial/security condition is reached. " : $"CONFIGURABLE_EXTENSION permits continued operations with a subtle corner watermark until {end:O}; afterwards the recorded policy {after:D} applies. ") +
                    $"Signed lease duration is {lease} seconds and offline grace is {grace} seconds. {PreviewTerm(license)}";
                response.Warnings.Add("A preview does not sign or apply policy. Actual configuration must revalidate the persisted license, monotonic policy version, configured controls and protected signing authority.");
                if (license.TermKind == CommercialTerm.Lifetime) { response.Warnings.Add("Support or premium expiry alone cannot trigger a base-expiry watermark or full stop for this LIFETIME base."); }
                break;
            }
        }

        return response;
    }

    private async Task<CommercialLicense> PreviewLicenseAsync(Guid licenseId, Guid shopId, CancellationToken cancellationToken)
    {
        var license = await _dbContext.CommercialLicenses.AsNoTracking().SingleOrDefaultAsync(l => l.Id == licenseId, cancellationToken)
            ?? throw new CentralDomainException("LICENSE_NOT_FOUND", "license.not_found", "The preview license does not exist.");
        ValidatePreviewShop(license.ShopId, shopId);
        return license;
    }

    private Task<int> CountPreviewInstallationsAsync(Guid licenseId, CancellationToken cancellationToken) => _dbContext.Installations.AsNoTracking()
        .CountAsync(i => i.LicenseId == licenseId && (i.Status == Installation.StatusActive || i.Status == Installation.StatusRegistered || i.Status == Installation.StatusPendingApproval), cancellationToken);

    private async Task ValidatePreviewPolicyVersionAsync(JsonElement? parameters, CommercialLicense license, CancellationToken cancellationToken)
    {
        var current = await _dbContext.ShopEnforcementPolicies.AsNoTracking().Where(p => p.ShopId == license.ShopId && p.LicenseId == license.Id)
            .OrderByDescending(p => p.PolicyVersion).Select(p => (int?)p.PolicyVersion).FirstOrDefaultAsync(cancellationToken);
        ValidatePreviewVersion(parameters, "policyVersion", current ?? 0);
        ValidatePreviewIdentity(parameters, "licenseId", license.Id);
    }

    private static void ValidatePreviewShop(Guid actual, Guid requested)
    {
        if (actual != requested) { throw new CentralDomainException("TARGET_SHOP_MISMATCH", "preview.shop_mismatch", "The persisted target does not belong to this shop."); }
    }

    private static JsonElement? ParsePreviewParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw InvalidPreviewParameters(); }
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw InvalidPreviewParameters(); }
    }

    private static JsonElement? PreviewParameter(JsonElement? parameters, string name)
    {
        if (!parameters.HasValue) { return null; }
        JsonElement? value = null;
        foreach (var container in new[] { parameters.Value }.Concat(parameters.Value.EnumerateObject()
            .Where(p => p.Name is "commandMeta" or "meta" or "payload").Select(p => p.Value).Where(p => p.ValueKind == JsonValueKind.Object)))
        {
            foreach (var property in container.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                if (value.HasValue && value.Value.GetRawText() != property.Value.GetRawText()) { throw InvalidPreviewParameters(); }
                value = property.Value;
            }
        }
        return value;
    }

    private static Guid? PreviewGuid(JsonElement? parameters, string name)
    {
        var value = PreviewParameter(parameters, name);
        if (!value.HasValue) { return null; }
        if (value.Value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.Value.GetString(), out var id) || id == Guid.Empty) { throw InvalidPreviewParameters(); }
        return id;
    }

    private static string PreviewString(JsonElement? parameters, string name, string fallback)
    {
        var value = PreviewParameter(parameters, name);
        if (!value.HasValue) { return fallback; }
        if (value.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.Value.GetString()))
        {
            throw InvalidPreviewParameters();
        }
        return value.Value.GetString()!.Trim().ToUpperInvariant();
    }

    private static DateTimeOffset? PreviewDate(JsonElement? parameters, string name)
    {
        var value = PreviewParameter(parameters, name);
        if (!value.HasValue) { return null; }
        if (value.Value.ValueKind != JsonValueKind.String || !value.Value.TryGetDateTimeOffset(out var date)) { throw InvalidPreviewParameters(); }
        return date;
    }

    private static void ValidatePreviewIdentity(JsonElement? parameters, string name, Guid expected)
    {
        var actual = PreviewGuid(parameters, name);
        if (actual.HasValue && actual != expected) { throw new CentralDomainException("TARGET_MISMATCH", "preview.target_mismatch", "Proposed parameters target a different persisted identity."); }
    }

    private static void ValidatePreviewVersion(JsonElement? parameters, string name, int expected)
    {
        var value = PreviewParameter(parameters, name);
        if (value.HasValue && (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetInt32(out var actual))) { throw InvalidPreviewParameters(); }
        if (value.HasValue && value.Value.GetInt32() != expected) { throw new CentralDomainException("CONCURRENCY_CONFLICT", "preview.stale_revision", "Persisted state changed; refresh the target and preview again."); }
    }

    private static bool RequiresConfiguredPreviewApproval(params string[] actions)
    {
        var matrix = Environment.GetEnvironmentVariable("CENTRAL_DUAL_APPROVAL_ACTIONS");
        if (string.IsNullOrWhiteSpace(matrix)) { throw new CentralDomainException("SECURITY_POLICY_REQUIRED", "security.policy_required", "An explicit independent-approval matrix is required."); }
        var configured = matrix.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return actions.Any(a => configured.Contains(a, StringComparer.OrdinalIgnoreCase));
    }

    private static string PreviewTerm(CommercialLicense license) => license.TermKind == CommercialTerm.Lifetime
        ? "This is a LIFETIME base with no scheduled commercial expiry; independent security revocation remains applicable."
        : $"This is a FIXED_TERM base with commercial expiry {PreviewExpiry(license.ValidUntilUtc)}.";

    private static string PreviewExpiry(DateTimeOffset? value) => value?.ToString("O") ?? "not configured";
    private static CentralDomainException InvalidPreviewParameters() => new("INVALID_PREVIEW_PARAMETERS", "preview.invalid_parameters", "Proposed parameters must contain unambiguous valid action values.");

    // CONE-T23: PII Unmasking with Immutable Audit Record
    public async Task<UnmaskPiiResponseDto> UnmaskPiiAsync(
        UnmaskPiiRequestDto request,
        Guid adminUserId,
        string adminUsername,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new CentralDomainException("REASON_REQUIRED", "audit.reason_required", "Explicit operational reason is required to unmask customer PII.");
        }

        var session = await _authService.ValidateSessionAsync(sessionId, cancellationToken);
        if (session is null || session.AdminUserId != adminUserId)
        {
            throw new CentralDomainException("SESSION_INVALID", "auth.session_invalid", "A validated session for the acting administrator is required.");
        }
        if (!session.Capabilities.Contains("customer.view_sensitive") || !session.Capabilities.Contains(AdminCapabilities.AuditRead))
        {
            throw new CentralDomainException("UNAUTHORIZED_CAPABILITY", "auth.unauthorized_capability", "Customer-sensitive and audit-read capabilities are required for this audited reveal.");
        }
        var actor = await _dbContext.AdminUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == session.AdminUserId, cancellationToken)
            ?? throw new CentralDomainException("USER_NOT_FOUND", "auth.user_not_found", "The authenticated administrator no longer exists.");

        var auditRecord = await _dbContext.CentralAuditRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.AuditRecordId, cancellationToken)
            ?? throw new CentralDomainException("AUDIT_RECORD_NOT_FOUND", "audit.record_not_found", "Audit record not found.");

        Customer? customer = null;
        if (Guid.TryParse(auditRecord.ResourceId, out var resourceId))
        {
            if (string.Equals(auditRecord.Resource, nameof(Customer), StringComparison.OrdinalIgnoreCase))
            {
                customer = await _dbContext.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == resourceId, cancellationToken);
            }
            else if (string.Equals(auditRecord.Resource, nameof(Shop), StringComparison.OrdinalIgnoreCase))
            {
                var customerId = await _dbContext.Shops.AsNoTracking().Where(s => s.Id == resourceId).Select(s => (Guid?)s.CustomerId).SingleOrDefaultAsync(cancellationToken);
                if (customerId.HasValue)
                {
                    customer = await _dbContext.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == customerId.Value, cancellationToken);
                }
            }
        }
        if (customer is null)
        {
            throw new CentralDomainException("PII_UNAVAILABLE", "audit.pii_unavailable", "This audit resource has no supported persisted customer-contact relationship.");
        }

        var now = DateTimeOffset.UtcNow;

        // Log append-only audit unmasking event
        var unmaskAudit = CentralAuditRecord.Create(
            actorId: actor.Id.ToString(),
            actorUsername: actor.Username,
            sessionId: sessionId,
            action: "AUDIT_PII_UNMASKED",
            resource: "CentralAuditRecord",
            resourceId: request.AuditRecordId.ToString(),
            reason: request.Reason,
            correlationId: Guid.NewGuid().ToString(),
            clientIp: null,
            userAgent: null,
            payloadSha256: null,
            detailsJson: JsonSerializer.Serialize(new { customerId = customer.Id, sourceResource = auditRecord.Resource, sourceResourceId = auditRecord.ResourceId }),
            isSuccess: true,
            timestampUtc: now);

        _dbContext.CentralAuditRecords.Add(unmaskAudit);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Reveal only existing approved customer fields. Audit metadata is a
        // relationship to the data, never fabricated contact data or assurance.
        var unmaskedData = JsonSerializer.Serialize(new
        {
            customerId = customer.Id,
            legalName = customer.LegalName,
            contactReference = customer.ContactReference,
            contactEmail = customer.ContactEmail,
            contactPhone = customer.ContactPhone
        });

        return new UnmaskPiiResponseDto
        {
            AuditRecordId = request.AuditRecordId,
            UnmaskedData = unmaskedData,
            AccessLoggedAtUtc = now
        };
    }

    // CONE-T23: Dual-Approval Decision Enforcement (Self-Approval Prohibited)
    public async Task<bool> DecideSensitiveApprovalAsync(
        Guid approvalId,
        Guid decidingAdminId,
        SensitiveApprovalDecisionDto dto,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var approval = await _dbContext.SensitiveApprovalRequests
            .FirstOrDefaultAsync(a => a.Id == approvalId, cancellationToken)
            ?? throw new CentralDomainException("APPROVAL_NOT_FOUND", "approval.not_found", $"Sensitive approval request '{approvalId}' not found.");

        if (approval.Status != SensitiveApprovalRequest.StatusPending)
        {
            throw new CentralDomainException("APPROVAL_ALREADY_DECIDED", "approval.already_decided", "This approval request has already been resolved.");
        }

        // Strict Self-Approval Guard
        if (approval.RequesterAdminId == decidingAdminId)
        {
            throw new CentralDomainException("SELF_APPROVAL_PROHIBITED", "approval.self_approval_prohibited", "A proposer cannot approve their own sensitive action request.");
        }

        var now = DateTimeOffset.UtcNow;
        if (string.Equals(dto.Decision, "APPROVE", StringComparison.OrdinalIgnoreCase))
        {
            var payloadSha = !string.IsNullOrWhiteSpace(dto.TargetPayloadSha256)
                ? dto.TargetPayloadSha256
                : approval.TargetPayloadSha256;

            approval.Approve(decidingAdminId, payloadSha, now);
        }
        else
        {
            approval.Reject(decidingAdminId, now);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
