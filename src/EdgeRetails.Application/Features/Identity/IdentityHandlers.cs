using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Identity;

namespace EdgeRetails.Application.Features.Identity;

public static class PermissionKeys
{
    public const string DashboardView = "dashboard.view";
    public const string SalesCreate = "sales.create";
    public const string SalesView = "sales.view";
    public const string SalesPosUse = "sales.pos.use";
    public const string SalesDraftCreate = "sales.draft.create";
    public const string SalesDraftResume = "sales.draft.resume";
    public const string SalesDraftCancel = "sales.draft.cancel";
    public const string SalesPriceCheck = "sales.price_check";
    public const string SalesPriceOverride = "sales.price_override";
    public const string SalesPriceOverrideBelowCost = "sales.price_override_below_cost";
    public const string ThakaManage = "thaka.manage";
    public const string PurchasingManage = "purchasing.manage";
    public const string InventoryManage = "inventory.manage";
    public const string ExpensesManage = "expenses.manage";
    public const string CustomersManage = "customers.manage";
    public const string SuppliersManage = "suppliers.manage";
    public const string SupplierAccountView = "supplier.account.view";
    public const string SupplierPaymentCreate = "supplier.payment.create";
    public const string SupplierPaymentReverse = "supplier.payment.reverse";
    public const string SupplierAdvanceCreate = "supplier.advance.create";
    public const string SupplierRefundCreate = "supplier.refund.create";
    public const string SupplierRefundReverse = "supplier.refund.reverse";
    public const string SupplierAccountAdjust = "supplier.account.adjust";
    public const string SupplierWarrantyView = "supplier.warranty.view";
    public const string WarrantyView = "warranty.view";
    public const string WarrantyClaimCreate = "warranty.claim.create";
    public const string WarrantyClaimUpdate = "warranty.claim.update";
    public const string WarrantyClaimSendSupplier = "warranty.claim.send_supplier";
    public const string WarrantyClaimResolve = "warranty.claim.resolve";
    public const string WarrantyClaimReplace = "warranty.claim.replace";
    public const string WarrantyClaimHandover = "warranty.claim.handover";
    public const string WarrantyShopStockManage = "warranty.shop_stock.manage";
    public const string WarrantyShopStockCredit = "warranty.shop_stock.credit";
    public const string ReportsView = "reports.view";
    public const string SettingsManage = "settings.manage";
}

public sealed record LoginAccountDto(
    Guid UserId,
    string DisplayName,
    string RoleName,
    string Initials,
    bool IsPrimary);
public sealed record AuthenticateUserCommand(
    Guid UserId,
    string Pin,
    Guid ClientSessionId);

public sealed record AuthenticatedUserDto(
    Guid UserId,
    Guid SessionId,
    string DisplayName,
    string RoleName,
    string Initials,
    IReadOnlySet<string> PermissionKeys);

public sealed class GetLoginAccountsHandler
{
    private readonly IIdentityReadRepository _identity;

    public GetLoginAccountsHandler(IIdentityReadRepository identity)
    {
        _identity = identity;
    }

    public async Task<IReadOnlyList<LoginAccountDto>> HandleAsync(
        CancellationToken cancellationToken)
    {
        var users = await _identity.GetActiveUsersAsync(cancellationToken);
        var result = new List<LoginAccountDto>(users.Count);
        foreach (var user in users)
        {
            var role = await _identity.GetRoleAsync(
                user.RoleId,
                cancellationToken);
            if (role is null || !role.IsActive)
            {
                continue;
            }

            result.Add(new LoginAccountDto(
                user.Id,
                user.DisplayName,
                role.Name,
                BuildInitials(user.DisplayName),
                string.Equals(
                    role.Name,
                    "Owner",
                    StringComparison.OrdinalIgnoreCase)));
        }

        return result
            .OrderByDescending(x => x.IsPrimary)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string BuildInitials(string displayName)
    {
        var parts = displayName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "U",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => string.Concat(
                parts[0][..1],
                parts[^1][..1]).ToUpperInvariant()
        };
    }
}

public sealed class AuthenticateUserHandler
{
    private readonly IIdentityReadRepository _identity;
    private readonly IIdentitySessionRepository _sessions;
    private readonly IPinCredentialService _pinCredentials;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public AuthenticateUserHandler(
        IIdentityReadRepository identity,
        IIdentitySessionRepository sessions,
        IPinCredentialService pinCredentials,
        IBusinessAuditWriter audit,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _identity = identity;
        _sessions = sessions;
        _pinCredentials = pinCredentials;
        _audit = audit;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }
    public async Task<Result<AuthenticatedUserDto>> HandleAsync(
        AuthenticateUserCommand command,
        CancellationToken cancellationToken)
    {
        if (command.UserId == Guid.Empty ||
            command.ClientSessionId == Guid.Empty ||
            command.Pin.Length != 4 ||
            command.Pin.Any(ch => !char.IsAsciiDigit(ch)))
        {
            return Result<AuthenticatedUserDto>.Failure(
                "identity.invalid_login",
                "Invalid user or PIN.");
        }

        var user = await _identity.GetUserAsync(
            command.UserId,
            cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
        {
            return Result<AuthenticatedUserDto>.Failure(
                "identity.invalid_login",
                "Invalid user or PIN.");
        }

        var role = await _identity.GetRoleAsync(
            user.RoleId,
            cancellationToken);
        if (role is null || !role.IsActive)
        {
            return Result<AuthenticatedUserDto>.Failure(
                "identity.role_inactive",
                "This account role is not active.");
        }
        var credential = new PinCredential(
            user.PinHash,
            user.PinSalt,
            user.PinIterations,
            user.PinAlgorithm);
        if (!_pinCredentials.Verify(command.Pin, credential))
        {
            return Result<AuthenticatedUserDto>.Failure(
                "identity.invalid_login",
                "Invalid user or PIN.");
        }

        var permissions = await _identity.GetEffectivePermissionKeysAsync(
            user.Id,
            cancellationToken);

        var session = new UserSession
        {
            UserId = user.Id,
            ClientSessionId = command.ClientSessionId,
            StartedAt = _clock.UtcNow,
            IsRevoked = false
        };
        _sessions.AddSession(session);

        _audit.Record(
            "USER_LOGIN",
            "USER_SESSION",
            session.Id,
            user.Id,
            command.ClientSessionId,
            $"{user.DisplayName} signed in.");
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result<AuthenticatedUserDto>.Success(
            new AuthenticatedUserDto(
                user.Id,
                session.Id,
                user.DisplayName,
                role.Name,
                GetLoginAccountsHandler.BuildInitials(user.DisplayName),
                permissions));
    }
}

public sealed record EndUserSessionCommand(
    Guid UserId,
    Guid SessionId,
    Guid CorrelationId);

public sealed class EndUserSessionHandler
{
    private readonly IIdentitySessionRepository _sessions;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public EndUserSessionHandler(
        IIdentitySessionRepository sessions,
        IBusinessAuditWriter audit,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _sessions = sessions;
        _audit = audit;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }
    public async Task<Result> HandleAsync(
        EndUserSessionCommand command,
        CancellationToken cancellationToken)
    {
        if (command.UserId == Guid.Empty ||
            command.SessionId == Guid.Empty ||
            command.CorrelationId == Guid.Empty)
        {
            return Result.Failure(
                "identity.invalid_session_end",
                "User session identifiers are required.");
        }

        var session = await _sessions.GetSessionForUpdateAsync(
            command.SessionId,
            cancellationToken);
        if (session is null ||
            session.UserId != command.UserId ||
            session.IsRevoked)
        {
            return Result.Failure(
                "identity.session_not_active",
                "The active user session could not be found.");
        }

        if (session.EndedAt is not null)
        {
            return Result.Success();
        }

        session.EndedAt = _clock.UtcNow;
        _audit.Record(
            "USER_LOGOUT",
            "USER_SESSION",
            session.Id,
            command.UserId,
            command.CorrelationId,
            "User session ended.");
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
public interface IApplicationPermissionAuthorizer
{
    Task<Result> AuthorizeAsync(
        Guid actorId,
        string permissionKey,
        CancellationToken cancellationToken);
}

public sealed class ApplicationPermissionAuthorizer
    : IApplicationPermissionAuthorizer
{
    private readonly IIdentityReadRepository _identity;

    public ApplicationPermissionAuthorizer(
        IIdentityReadRepository identity)
    {
        _identity = identity;
    }

    public async Task<Result> AuthorizeAsync(
        Guid actorId,
        string permissionKey,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty ||
            string.IsNullOrWhiteSpace(permissionKey))
        {
            return Result.Failure(
                "authorization.invalid_request",
                "Authorization requires an actor and permission.");
        }

        var permissions = await _identity.GetEffectivePermissionKeysAsync(
            actorId,
            cancellationToken);
        return permissions.Contains(permissionKey)
            ? Result.Success()
            : Result.Failure(
                "authorization.denied",
                $"Permission '{permissionKey}' is required.");
    }
}
