using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;

namespace EdgeRetails.Application.Features.Catalog;

public sealed record SaveCompanyCommand(
    Guid ActorId,
    Guid? CompanyId,
    string Name,
    string? Code = null);

public sealed record SetCompanyActiveCommand(
    Guid ActorId,
    Guid CompanyId,
    bool IsActive);

public sealed record SaveCategoryCommand(
    Guid ActorId,
    Guid? CategoryId,
    string Name,
    string? IdentitySymbol = null);

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

public sealed class SaveCompanyHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IProductCatalogSafetyReadService? _safety;

    public SaveCompanyHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IProductCatalogSafetyReadService? safety = null)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _safety = safety;
    }

    public Task<Result<Guid>> HandleAsync(
        SaveCompanyCommand command,
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

            var name = System.Text.RegularExpressions.Regex.Replace(command.Name?.Trim() ?? string.Empty, @"\s+", " ");
            if (name.Length == 0 || name.Length > 150)
            {
                return Result<Guid>.Failure(
                    "catalog.company_name_invalid",
                    "Company name is required and cannot exceed 150 characters.");
            }

            var companies = await _catalog.GetCompaniesAsync(true, ct);
            if (companies.Any(x =>
                x.Id != command.CompanyId &&
                string.Equals(
                    System.Text.RegularExpressions.Regex.Replace(x.Name.Trim(), @"\s+", " "),
                    name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return Result<Guid>.Failure(
                    "catalog.company_duplicate",
                    "A company with this name already exists.");
            }

            Company company;
            if (command.CompanyId is Guid companyId)
            {
                company = await _catalog.GetCompanyForUpdateAsync(companyId, ct)
                    ?? throw new InvalidOperationException("Company disappeared during update.");

                if (!string.IsNullOrWhiteSpace(command.Code))
                {
                    string normalizedCode;
                    try
                    {
                        normalizedCode = TraceabilityCodeRules.NormalizeCompanyCode(command.Code);
                    }
                    catch (BusinessRuleException ex)
                    {
                        return Result<Guid>.Failure(ex.Code, ex.Message);
                    }

                    if (!string.Equals(company.Code, normalizedCode, StringComparison.OrdinalIgnoreCase))
                    {
                        if (await _catalog.IsCompanyInUseByActiveProductAsync(company.Id, ct))
                        {
                            return Result<Guid>.Failure(
                                "catalog.company_code_immutable",
                                "Company code cannot be modified once products are assigned.");
                        }

                        if (_safety is not null)
                        {
                            var affectedProducts = await _catalog.GetProductsAsync(true, ct);
                            foreach (var product in affectedProducts.Where(x => x.CompanyId == company.Id))
                            {
                                if (await _safety.HasStockOrHistoryAsync(product.Id, ct))
                                {
                                    return Result<Guid>.Failure(
                                        "catalog.company_code_immutable",
                                        "Company code cannot be modified after affected products have inventory or transaction history.");
                                }
                            }
                        }
                        if (companies.Any(x => x.Id != company.Id && string.Equals(x.Code, normalizedCode, StringComparison.OrdinalIgnoreCase)))
                        {
                            return Result<Guid>.Failure(
                                "catalog.company_code_duplicate",
                                "A company with this code already exists.");
                        }

                        company.Code = normalizedCode;
                    }
                }
            }
            else
            {
                string code;
                if (!string.IsNullOrWhiteSpace(command.Code))
                {
                    try
                    {
                        code = TraceabilityCodeRules.NormalizeCompanyCode(command.Code);
                    }
                    catch (BusinessRuleException ex)
                    {
                        return Result<Guid>.Failure(ex.Code, ex.Message);
                    }
                }
                else
                {
                    try
                    {
                        code = TraceabilityCodeRules.SuggestCompanyCode(
                            name,
                            candidate => companies.Any(x => string.Equals(x.Code, candidate, StringComparison.OrdinalIgnoreCase)));
                    }
                    catch (BusinessRuleException ex)
                    {
                        return Result<Guid>.Failure(ex.Code, ex.Message);
                    }
                }

                if (companies.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
                {
                    return Result<Guid>.Failure(
                        "catalog.company_code_duplicate",
                        "A company with this code already exists.");
                }

                company = new Company
                {
                    Code = code,
                    IsActive = true,
                    Version = 1
                };
                _catalog.AddCompany(company);
            }

            company.Name = name;
            company.Version++;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(company.Id);
        }, cancellationToken);
}

public sealed class SetCompanyActiveHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SetCompanyActiveHandler(
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
        SetCompanyActiveCommand command,
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

            var company = await _catalog.GetCompanyForUpdateAsync(command.CompanyId, ct);
            if (company is null)
            {
                return Result.Failure("catalog.company_not_found", "Company was not found.");
            }

            if (!command.IsActive &&
                await _catalog.IsCompanyInUseByActiveProductAsync(company.Id, ct))
            {
                return Result.Failure(
                    "catalog.company_in_use",
                    "Reassign active products before deactivating this company.");
            }

            company.IsActive = command.IsActive;
            company.Version++;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}

public sealed class SaveCategoryHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IApplicationPermissionAuthorizer _authorizer;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IProductCatalogSafetyReadService? _safety;

    public SaveCategoryHandler(
        ICatalogRepository catalog,
        IApplicationPermissionAuthorizer authorizer,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IProductCatalogSafetyReadService? safety = null)
    {
        _catalog = catalog;
        _authorizer = authorizer;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _safety = safety;
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

            var name = System.Text.RegularExpressions.Regex.Replace(command.Name.Trim(), @"\s+", " ");
            if (name.Length == 0 || name.Length > 150)
            {
                return Result<Guid>.Failure(
                    "catalog.category_name_invalid",
                    "Category name is required and cannot exceed 150 characters.");
            }

            var categories = await _catalog.GetCategoriesAsync(true, ct);
            if (categories.Any(x =>
                x.Id != command.CategoryId &&
                string.Equals(
                    System.Text.RegularExpressions.Regex.Replace(x.Name.Trim(), @"\s+", " "),
                    name,
                    StringComparison.OrdinalIgnoreCase)))
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

                if (!string.IsNullOrWhiteSpace(command.IdentitySymbol))
                {
                    string normalizedSymbol;
                    try
                    {
                        normalizedSymbol = TraceabilityCodeRules.NormalizeCategorySymbol(command.IdentitySymbol);
                    }
                    catch (BusinessRuleException ex)
                    {
                        return Result<Guid>.Failure(ex.Code, ex.Message);
                    }

                    if (!string.Equals(category.IdentitySymbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase))
                    {
                        if (await _catalog.IsCategoryInUseByActiveProductAsync(category.Id, ct))
                        {
                            return Result<Guid>.Failure(
                                "catalog.category_symbol_immutable",
                                "Category identity symbol cannot be modified once products are assigned.");
                        }

                        if (_safety is not null)
                        {
                            var affectedProducts = await _catalog.GetProductsAsync(true, ct);
                            foreach (var product in affectedProducts.Where(x => x.CategoryId == category.Id))
                            {
                                if (await _safety.HasStockOrHistoryAsync(product.Id, ct))
                                {
                                    return Result<Guid>.Failure(
                                        "catalog.category_symbol_immutable",
                                        "Category identity symbol cannot be modified after affected products have inventory or transaction history.");
                                }
                            }
                        }
                        if (categories.Any(x => x.Id != category.Id && string.Equals(x.IdentitySymbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase)))
                        {
                            return Result<Guid>.Failure(
                                "catalog.category_symbol_duplicate",
                                "A category with this identity symbol already exists.");
                        }

                        category.IdentitySymbol = normalizedSymbol;
                    }
                }
            }
            else
            {
                string symbol;
                if (!string.IsNullOrWhiteSpace(command.IdentitySymbol))
                {
                    try
                    {
                        symbol = TraceabilityCodeRules.NormalizeCategorySymbol(command.IdentitySymbol);
                    }
                    catch (BusinessRuleException ex)
                    {
                        return Result<Guid>.Failure(ex.Code, ex.Message);
                    }
                }
                else
                {
                    try
                    {
                        symbol = TraceabilityCodeRules.SuggestCategorySymbol(
                            name,
                            candidate => categories.Any(x => string.Equals(x.IdentitySymbol, candidate, StringComparison.OrdinalIgnoreCase)));
                    }
                    catch (BusinessRuleException ex)
                    {
                        return Result<Guid>.Failure(ex.Code, ex.Message);
                    }
                }

                if (categories.Any(x => string.Equals(x.IdentitySymbol, symbol, StringComparison.OrdinalIgnoreCase)))
                {
                    return Result<Guid>.Failure(
                        "catalog.category_symbol_duplicate",
                        "A category with this identity symbol already exists.");
                }

                category = new Category
                {
                    IdentitySymbol = symbol,
                    IsActive = true,
                    Version = 1
                };
                _catalog.AddCategory(category);
            }

            category.Name = name;
            category.Version++;
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

            var name = System.Text.RegularExpressions.Regex.Replace(command.Name.Trim(), @"\s+", " ");
            var symbol = System.Text.RegularExpressions.Regex.Replace(command.Symbol.Trim(), @"\s+", " ");
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
                (string.Equals(
                    System.Text.RegularExpressions.Regex.Replace(x.Name.Trim(), @"\s+", " "),
                    name,
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                    System.Text.RegularExpressions.Regex.Replace(x.Symbol.Trim(), @"\s+", " "),
                    symbol,
                    StringComparison.OrdinalIgnoreCase))))
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
