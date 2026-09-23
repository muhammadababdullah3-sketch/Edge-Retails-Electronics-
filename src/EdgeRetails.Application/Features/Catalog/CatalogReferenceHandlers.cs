using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Application.Features.Catalog;

public sealed record SaveCategoryCommand(
    Guid ActorId,
    Guid? CategoryId,
    string Name);

public sealed record SetCategoryActiveCommand(
    Guid ActorId,
    Guid CategoryId,
    bool IsActive);

public sealed record SaveUnitCommand(
    Guid ActorId,
    Guid? UnitId,
    string Name,
    string Symbol,
    int DisplayDecimalPlaces);

public sealed record SetUnitActiveCommand(
    Guid ActorId,
    Guid UnitId,
    bool IsActive);

public sealed class SaveCategoryHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SaveCategoryHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        SaveCategoryCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return Result<Guid>.Failure(auth.Error!.Code, auth.Error.Message);
            }

            var name = command.Name.Trim();
            if (name.Length == 0 || name.Length > 150)
            {
                return Result<Guid>.Failure(
                    "catalog.category_name_invalid",
                    "Category name is required and cannot exceed 150 characters.");
            }

            var categories = await _catalog.GetCategoriesAsync(true, ct);
            if (categories.Any(x =>
                x.Id != command.CategoryId &&
                string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return Result<Guid>.Failure(
                    "catalog.category_duplicate",
                    "A category with this name already exists.");
            }

            Category category;
            if (command.CategoryId is Guid categoryId)
            {
                category = await _catalog.GetCategoryForUpdateAsync(categoryId, ct)
                    ?? throw new InvalidOperationException("Category disappeared during update.");
            }
            else
            {
                category = new Category { IsActive = true };
                _catalog.AddCategory(category);
            }

            category.Name = name;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(category.Id);
        }, cancellationToken);
}

public sealed class SetCategoryActiveHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SetCategoryActiveHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        SetCategoryActiveCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return auth;
            }

            var category = await _catalog.GetCategoryForUpdateAsync(command.CategoryId, ct);
            if (category is null)
            {
                return Result.Failure("catalog.category_not_found", "Category was not found.");
            }

            if (!command.IsActive &&
                await _catalog.IsCategoryInUseByActiveProductAsync(category.Id, ct))
            {
                return Result.Failure(
                    "catalog.category_in_use",
                    "Reassign active products before deactivating this category.");
            }

            category.IsActive = command.IsActive;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}

public sealed class SaveUnitHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SaveUnitHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        SaveUnitCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return Result<Guid>.Failure(auth.Error!.Code, auth.Error.Message);
            }

            var name = command.Name.Trim();
            var symbol = command.Symbol.Trim();
            if (name.Length == 0 || name.Length > 100 ||
                symbol.Length == 0 || symbol.Length > 20 ||
                command.DisplayDecimalPlaces is < 0 or > 6)
            {
                return Result<Guid>.Failure(
                    "catalog.unit_invalid",
                    "Unit name, symbol and decimal precision are invalid.");
            }

            var units = await _catalog.GetUnitsAsync(true, ct);
            if (units.Any(x =>
                x.Id != command.UnitId &&
                (string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase))))
            {
                return Result<Guid>.Failure(
                    "catalog.unit_duplicate",
                    "Unit name or symbol is already in use.");
            }

            Unit unit;
            if (command.UnitId is Guid unitId)
            {
                unit = await _catalog.GetUnitForUpdateAsync(unitId, ct)
                    ?? throw new InvalidOperationException("Unit disappeared during update.");
            }
            else
            {
                unit = new Unit { IsActive = true };
                _catalog.AddUnit(unit);
            }

            unit.Name = name;
            unit.Symbol = symbol;
            unit.DisplayDecimalPlaces = command.DisplayDecimalPlaces;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(unit.Id);
        }, cancellationToken);
}

public sealed class SetUnitActiveHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SetUnitActiveHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        SetUnitActiveCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var auth = await _authorizer.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.InventoryManage,
                ct);
            if (!auth.IsSuccess)
            {
                return auth;
            }

            var unit = await _catalog.GetUnitForUpdateAsync(command.UnitId, ct);
            if (unit is null)
            {
                return Result.Failure("catalog.unit_not_found", "Unit was not found.");
            }

            if (!command.IsActive &&
                await _catalog.IsUnitInUseByActiveCatalogAsync(unit.Id, ct))
            {
                return Result.Failure(
                    "catalog.unit_in_use",
                    "This unit is used by an active product or active product-unit mapping.");
            }

            unit.IsActive = command.IsActive;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}
