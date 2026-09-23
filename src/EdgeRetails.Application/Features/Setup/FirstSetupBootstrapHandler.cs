using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Features.Setup;

public sealed record FirstSetupBootstrapCommand(
    string ShopName,
    string? Phone,
    string? Address,
    string OwnerDisplayName,
    string OwnerPin,
    string? SelectedModule,
    Guid CorrelationId);

public sealed record FirstSetupBootstrapResult(
    Guid InstallationId,
    Guid OwnerUserId,
    Guid WalkInCustomerId);

public sealed class FirstSetupBootstrapHandler
{
    private static readonly string[] PermissionKeys =
    [
        "dashboard.view",
        "sales.create",
        "sales.view",
        "sales.pos.use",
        "sales.draft.create",
        "sales.draft.resume",
        "sales.draft.cancel",
        "sales.price_check",
        "sales.price_override",
        "sales.price_override_below_cost",
        "thaka.manage",
        "purchasing.manage",
        "inventory.manage",
        "expenses.manage",
        "customers.manage",
        "suppliers.manage",
        "supplier.account.view",
        "supplier.payment.create",
        "supplier.payment.reverse",
        "supplier.advance.create",
        "supplier.refund.create",
        "supplier.refund.reverse",
        "supplier.account.adjust",
        "supplier.warranty.view",
        "warranty.view",
        "warranty.claim.create",
        "warranty.claim.update",
        "warranty.claim.send_supplier",
        "warranty.claim.resolve",
        "warranty.claim.replace",
        "warranty.claim.handover",
        "warranty.shop_stock.manage",
        "warranty.shop_stock.credit",
        "reports.view",
        "settings.manage"
    ];

    private static readonly HashSet<string> ManagerPermissions =
    [
        "dashboard.view",
        "sales.create",
        "sales.view",
        "sales.pos.use",
        "sales.draft.create",
        "sales.draft.resume",
        "sales.draft.cancel",
        "sales.price_check",
        "sales.price_override",
        "purchasing.manage",
        "inventory.manage",
        "customers.manage",
        "suppliers.manage",
        "supplier.account.view",
        "supplier.payment.create",
        "supplier.refund.create",
        "supplier.warranty.view",
        "warranty.view",
        "warranty.claim.create",
        "warranty.claim.update",
        "warranty.claim.send_supplier",
        "warranty.claim.resolve",
        "warranty.claim.replace",
        "warranty.claim.handover",
        "warranty.shop_stock.manage"
    ];

    private static readonly HashSet<string> CashierPermissions =
    [
        "dashboard.view",
        "sales.create",
        "sales.view",
        "sales.pos.use",
        "sales.draft.create",
        "sales.draft.resume",
        "sales.draft.cancel",
        "sales.price_check"
    ]; private readonly ISetupRepository _setup;
    private readonly IPinCredentialService _pinCredentials;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public FirstSetupBootstrapHandler(
        ISetupRepository setup,
        IPinCredentialService pinCredentials,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _setup = setup;
        _pinCredentials = pinCredentials;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result<FirstSetupBootstrapResult>> HandleAsync(
        FirstSetupBootstrapCommand command,
        CancellationToken cancellationToken)
    {
        var validation = Validate(command);
        if (validation is not null)
        {
            return Task.FromResult(
                Result<FirstSetupBootstrapResult>.Failure(
                    validation.Code,
                    validation.Message));
        }
        return _transactions.ExecuteAsync(
            ct => BootstrapAsync(command, ct),
            cancellationToken);
    }

    private async Task<Result<FirstSetupBootstrapResult>> BootstrapAsync(
        FirstSetupBootstrapCommand command,
        CancellationToken cancellationToken)
    {
        var installation = await _setup.GetInstallationStateForUpdateAsync(
            cancellationToken);

        if (installation?.SetupStatus == SetupStatus.Complete)
        {
            return Result<FirstSetupBootstrapResult>.Failure(
                "setup.already_complete",
                "First Setup has already been completed.");
        }

        if (await _setup.AnyUsersAsync(cancellationToken))
        {
            return Result<FirstSetupBootstrapResult>.Failure(
                "setup.identity_exists",
                "Identity data already exists. Bootstrap will not overwrite it.");
        }

        var now = _clock.UtcNow;
        var isNewInstallation = installation is null;
        installation ??= new InstallationState
        {
            SetupStatus = SetupStatus.InProgress,
            StartedAt = now
        };

        if (installation.SetupStatus == SetupStatus.NotStarted)
        {
            installation.SetupStatus = SetupStatus.InProgress;
            installation.StartedAt = now;
        }

        installation.SelectedModule = Normalize(command.SelectedModule);

        if (isNewInstallation)
        {
            _setup.AddInstallationState(installation);
        }

        var shop = new ShopProfile
        {
            ProfileKey = "PRIMARY",
            ShopName = command.ShopName.Trim(),
            Phone = Normalize(command.Phone),
            Address = Normalize(command.Address),
            UpdatedAt = now
        };
        _setup.AddShopProfile(shop);

        var walkIn = new Customer
        {
            Name = "Walk-in Customer",
            IsWalkIn = true,
            IsActive = true,
            CreatedAt = now
        };
        _setup.AddCustomer(walkIn); _setup.AddReceiptTemplateSettings(new ReceiptTemplateSettings
        {
            TemplateKey = "PRIMARY",
            Header = command.ShopName.Trim(),
            Footer = "Thank you for your business.",
            ShowCustomer = true,
            ShowCashier = true,
            LogoBehavior = "DEFAULT",
            TemplateVersion = 1,
            UpdatedAt = now
        });

        var ownerRole = CreateRole("Owner");
        var managerRole = CreateRole("Manager");
        var cashierRole = CreateRole("Cashier");
        _setup.AddRole(ownerRole);
        _setup.AddRole(managerRole);
        _setup.AddRole(cashierRole);

        var permissions = PermissionKeys
            .Select(key => new Permission
            {
                Key = key,
                Description = key,
                IsActive = true
            })
            .ToArray();

        foreach (var permission in permissions)
        {
            _setup.AddPermission(permission);
            _setup.AddRolePermission(new RolePermission
            {
                RoleId = ownerRole.Id,
                PermissionId = permission.Id
            }); if (ManagerPermissions.Contains(permission.Key))
            {
                _setup.AddRolePermission(new RolePermission
                {
                    RoleId = managerRole.Id,
                    PermissionId = permission.Id
                });
            }

            if (CashierPermissions.Contains(permission.Key))
            {
                _setup.AddRolePermission(new RolePermission
                {
                    RoleId = cashierRole.Id,
                    PermissionId = permission.Id
                });
            }
        }

        var credential = _pinCredentials.Hash(command.OwnerPin);
        var owner = new User
        {
            DisplayName = command.OwnerDisplayName.Trim(),
            RoleId = ownerRole.Id,
            PinHash = credential.Hash,
            PinSalt = credential.Salt,
            PinIterations = credential.Iterations,
            PinAlgorithm = credential.Algorithm,
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        _setup.AddUser(owner); installation.SetupStatus = SetupStatus.Complete;
        installation.CompletedAt = now;
        installation.Version++;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<FirstSetupBootstrapResult>.Success(
            new FirstSetupBootstrapResult(
                installation.InstallationId,
                owner.Id,
                walkIn.Id));
    }

    private static Role CreateRole(string name) =>
        new()
        {
            Name = name,
            IsSystem = true,
            IsActive = true
        };

    private static Error? Validate(FirstSetupBootstrapCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ShopName))
        {
            return new Error("setup.shop_name_required", "Shop name is required.");
        }

        if (string.IsNullOrWhiteSpace(command.OwnerDisplayName))
        {
            return new Error("setup.owner_name_required", "Owner name is required.");
        }
        if (command.OwnerPin.Length != 4 ||
            command.OwnerPin.Any(ch => !char.IsAsciiDigit(ch)))
        {
            return new Error(
                "setup.owner_pin_invalid",
                "Owner PIN must contain exactly four digits.");
        }

        if (command.CorrelationId == Guid.Empty)
        {
            return new Error(
                "setup.correlation_required",
                "A correlation identifier is required.");
        }

        return null;
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
