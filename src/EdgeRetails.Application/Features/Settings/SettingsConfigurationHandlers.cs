using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;

namespace EdgeRetails.Application.Features.Settings;

public sealed record SettingsConfigurationDto(
    string ShopName,
    string? Phone,
    string? Address,
    string? ReceiptHeader,
    string? ReceiptFooter,
    bool ShowCustomer,
    bool ShowCashier,
    bool AutoPrintDefault);

public sealed record UpdateShopProfileCommand(
    string ShopName,
    string? Phone,
    string? Address,
    Guid ActorId,
    Guid CorrelationId);

public sealed record UpdateReceiptTemplateCommand(
    string? Header,
    string? Footer,
    bool ShowCustomer,
    bool ShowCashier,
    bool AutoPrintDefault,
    Guid ActorId,
    Guid CorrelationId);
public sealed class GetSettingsConfigurationHandler
{
    private readonly ISetupRepository _setup;

    public GetSettingsConfigurationHandler(ISetupRepository setup)
    {
        _setup = setup;
    }

    public async Task<SettingsConfigurationDto?> HandleAsync(
        CancellationToken cancellationToken)
    {
        var shop = await _setup.GetShopProfileAsync(cancellationToken);
        var receipt = await _setup.GetReceiptTemplateSettingsAsync(cancellationToken);
        if (shop is null || receipt is null)
        {
            return null;
        }

        return new SettingsConfigurationDto(
            shop.ShopName,
            shop.Phone,
            shop.Address,
            receipt.Header,
            receipt.Footer,
            receipt.ShowCustomer,
            receipt.ShowCashier,
            receipt.AutoPrintDefault);
    }
}
public sealed class UpdateShopProfileHandler
{
    private readonly ISetupRepository _setup;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly ITransactionRunner _transactions;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateShopProfileHandler(
        ISetupRepository setup,
        IApplicationPermissionAuthorizer authorization,
        ITransactionRunner transactions,
        IBusinessAuditWriter audit,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _setup = setup;
        _authorization = authorization;
        _transactions = transactions;
        _audit = audit;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }
    public Task<Result<bool>> HandleAsync(
        UpdateShopProfileCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.ShopName))
        {
            return Task.FromResult(Result<bool>.Failure(
                "settings.shop_name_required",
                "Shop name is required."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SettingsManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<bool>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }
            var profile = await _setup.GetShopProfileForUpdateAsync(ct);
            if (profile is null)
            {
                return Result<bool>.Failure(
                    "settings.shop_profile_missing",
                    "Authoritative shop profile is not configured.");
            }

            profile.ShopName = command.ShopName.Trim();
            profile.Phone = Normalize(command.Phone);
            profile.Address = Normalize(command.Address);
            profile.UpdatedAt = _clock.UtcNow;
            profile.Version++;

            _audit.Record(
                "SHOP_PROFILE_UPDATED",
                "SHOP_PROFILE",
                profile.Id,
                command.ActorId,
                command.CorrelationId,
                profile.ShopName);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<bool>.Success(true);
        }, cancellationToken);
    }
    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed class UpdateReceiptTemplateHandler
{
    private readonly ISetupRepository _setup;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly ITransactionRunner _transactions;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly IUnitOfWork _unitOfWork;
    public UpdateReceiptTemplateHandler(
        ISetupRepository setup,
        IApplicationPermissionAuthorizer authorization,
        ITransactionRunner transactions,
        IBusinessAuditWriter audit,
        IClock clock,
        IUnitOfWork unitOfWork)
    {
        _setup = setup;
        _authorization = authorization;
        _transactions = transactions;
        _audit = audit;
        _clock = clock;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<bool>> HandleAsync(
        UpdateReceiptTemplateCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SettingsManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<bool>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            var settings = await _setup.GetReceiptTemplateSettingsForUpdateAsync(ct);
            if (settings is null)
            {
                return Result<bool>.Failure(
                    "settings.receipt_template_missing",
                    "Authoritative receipt template is not configured.");
            }

            settings.Header = Normalize(command.Header);
            settings.Footer = Normalize(command.Footer);
            settings.ShowCustomer = command.ShowCustomer;
            settings.ShowCashier = command.ShowCashier;
            settings.AutoPrintDefault = command.AutoPrintDefault;
            settings.UpdatedAt = _clock.UtcNow;
            settings.Version++;

            _audit.Record(
                "RECEIPT_TEMPLATE_UPDATED",
                "RECEIPT_TEMPLATE",
                settings.Id,
                command.ActorId,
                command.CorrelationId,
                settings.TemplateKey);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<bool>.Success(true);
        }, cancellationToken);

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
