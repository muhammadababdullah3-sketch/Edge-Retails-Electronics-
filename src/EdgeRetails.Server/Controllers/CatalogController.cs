using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class CatalogController : ControllerBase
{
    private readonly IProductManagementReadService _productReads;
    private readonly CreateProductHandler _createProductHandler;
    private readonly UpdateProductHandler _updateProductHandler;
    private readonly DeactivateProductHandler _deactivateProductHandler;
    private readonly ReactivateProductHandler _reactivateProductHandler;
    private readonly SaveCompanyHandler _saveCompanyHandler;
    private readonly SaveCategoryHandler _saveCategoryHandler;
    private readonly SaveUnitHandler _saveUnitHandler;
    private readonly SetCompanyActiveHandler _setCompanyActiveHandler;
    private readonly SetCategoryActiveHandler _setCategoryActiveHandler;
    private readonly SetUnitActiveHandler _setUnitActiveHandler;
    private readonly SetSupplierProductActiveHandler _setSupplierProductActiveHandler;
    private readonly ConfigureProductUnitsHandler _configureUnitsHandler;
    private readonly SetProductUnitBarcodeHandler _setBarcodeHandler;
    private readonly SaveProductAggregateHandler _saveProductAggregateHandler;
    private readonly EdgeRetails.Application.Features.Setup.CatalogReferenceUpgradeHandler _catalogReferenceUpgradeHandler;

    public CatalogController(
        IProductManagementReadService productReads,
        CreateProductHandler createProductHandler,
        UpdateProductHandler updateProductHandler,
        DeactivateProductHandler deactivateProductHandler,
        ReactivateProductHandler reactivateProductHandler,
        SaveCompanyHandler saveCompanyHandler,
        SaveCategoryHandler saveCategoryHandler,
        SaveUnitHandler saveUnitHandler,
        SetCompanyActiveHandler setCompanyActiveHandler,
        SetCategoryActiveHandler setCategoryActiveHandler,
        SetUnitActiveHandler setUnitActiveHandler,
        SetSupplierProductActiveHandler setSupplierProductActiveHandler,
        ConfigureProductUnitsHandler configureUnitsHandler,
        SetProductUnitBarcodeHandler setBarcodeHandler,
        SaveProductAggregateHandler saveProductAggregateHandler,
        EdgeRetails.Application.Features.Setup.CatalogReferenceUpgradeHandler catalogReferenceUpgradeHandler)
    {
        _productReads = productReads;
        _createProductHandler = createProductHandler;
        _updateProductHandler = updateProductHandler;
        _deactivateProductHandler = deactivateProductHandler;
        _reactivateProductHandler = reactivateProductHandler;
        _saveCompanyHandler = saveCompanyHandler;
        _saveCategoryHandler = saveCategoryHandler;
        _saveUnitHandler = saveUnitHandler;
        _setCompanyActiveHandler = setCompanyActiveHandler;
        _setCategoryActiveHandler = setCategoryActiveHandler;
        _setUnitActiveHandler = setUnitActiveHandler;
        _setSupplierProductActiveHandler = setSupplierProductActiveHandler;
        _configureUnitsHandler = configureUnitsHandler;
        _setBarcodeHandler = setBarcodeHandler;
        _saveProductAggregateHandler = saveProductAggregateHandler;
        _catalogReferenceUpgradeHandler = catalogReferenceUpgradeHandler;
    }

    [HttpGet("search")]
    public async Task<IActionResult> SearchCatalog(
        [FromQuery] string? query,
        [FromQuery] string? packagingFacet,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            var posDenied = this.RequirePermission(PermissionKeys.SalesPosUse);
            if (posDenied is not null)
            {
                var viewDenied = this.RequirePermission(PermissionKeys.SalesView);
                if (viewDenied is not null)
                {
                    return denied;
                }
            }
        }

        var result = await _productReads.SearchCatalogAsync(
            query,
            packagingFacet,
            pageNumber,
            pageSize,
            cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Explicit one-time upgrade for an existing completed shop. Not invoked at server startup.
    /// Also reconciles Taxonomy V3 department categories and any missing reference units idempotently.
    /// </summary>
    [HttpPost("upgrade/fixed-units")]
    public async Task<IActionResult> UpgradeExistingShopFixedUnits(CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.Items.TryGetValue("CurrentUserId", out var value) && value is Guid userId
            ? userId
            : Guid.Empty;
        var result = await _catalogReferenceUpgradeHandler.HandleAsync(actor, cancellationToken);
        if (!result.IsSuccess)
        {
            return Conflict(new { code = result.Error!.Code, message = result.Error.Message });
        }

        return Ok(result.Value);
    }

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts(
        [FromQuery] string? search,
        [FromQuery] Guid? categoryId,
        [FromQuery] bool? isActive,
        [FromQuery] int pageSize = 100,
        [FromQuery] string? beforeName = null,
        [FromQuery] Guid? beforeProductId = null,
        [FromQuery] string? classRef = null,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        if (string.IsNullOrWhiteSpace(beforeName) != !beforeProductId.HasValue)
        {
            return BadRequest(new { code = "catalog.cursor_invalid", message = "Both beforeName and beforeProductId are required for a catalog cursor." });
        }

        var query = new ProductManagementPageQuery(
            IncludeInactive: isActive != true,
            Search: search,
            PageSize: Math.Clamp(pageSize, 1, 500),
            BeforeName: beforeName,
            BeforeProductId: beforeProductId,
            CategoryId: categoryId,
            IsActive: isActive,
            ClassRef: string.IsNullOrWhiteSpace(classRef) ? null : classRef.Trim());

        var products = await _productReads.GetProductsPageAsync(query, cancellationToken);
        return Ok(products);
    }

    [HttpGet("products/{id:guid}")]
    public async Task<IActionResult> GetProduct(
        [FromRoute] Guid id,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            var posDenied = this.RequirePermission(PermissionKeys.SalesPosUse);
            if (posDenied is not null)
            {
                var viewDenied = this.RequirePermission(PermissionKeys.SalesView);
                if (viewDenied is not null)
                {
                    return denied;
                }
            }
        }

        var product = await _productReads.GetProductAsync(id, cancellationToken);
        if (product is null)
        {
            return NotFound(new { code = "catalog.product_not_found", message = $"Product '{id}' was not found." });
        }

        var actor = HttpContext.GetActorContext();
        if (actor is not null && string.Equals(actor.RoleName, "Cashier", StringComparison.OrdinalIgnoreCase))
        {
            product = product with { ReferencePurchaseCost = null };
        }

        return Ok(product);
    }

    [HttpGet("products/sku/{sku}")]
    public async Task<IActionResult> GetProductBySku(
        [FromRoute] string sku,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var product = await _productReads.GetProductBySkuAsync(sku, cancellationToken);
        if (product is null)
        {
            return NotFound(new { code = "catalog.product_not_found", message = $"Product with SKU '{sku}' was not found." });
        }
        return Ok(product);
    }

    [HttpPost("products")]
    public async Task<IActionResult> CreateProduct(
        [FromBody] CreateProductCommand command,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _createProductHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("products/aggregate")]
    public async Task<IActionResult> SaveProductAggregate(
        [FromBody] SaveProductAggregateCommand command,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _saveProductAggregateHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPut("products/{id:guid}")]
    public async Task<IActionResult> UpdateProduct(
        [FromRoute] Guid id,
        [FromBody] UpdateProductCommand command,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null
            ? command with { ProductId = id, ActorId = actor.UserId }
            : command with { ProductId = id };
        var result = await _updateProductHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("products/{id:guid}/suppliers/{supplierId:guid}/active")]
    public async Task<IActionResult> SetSupplierProductActive(
        [FromRoute] Guid id,
        [FromRoute] Guid supplierId,
        [FromBody] SetCatalogReferenceActiveRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var result = await _setSupplierProductActiveHandler.HandleAsync(
            new SetSupplierProductActiveCommand(
                actor?.UserId ?? Guid.Empty,
                id,
                supplierId,
                request.IsActive,
                request.ExpectedVersion),
            cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    [HttpPost("products/{id:guid}/deactivate")]
    public async Task<IActionResult> DeactivateProduct(
        [FromRoute] Guid id,
        [FromBody] DeactivateProductRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;
        var command = new DeactivateProductCommand(actorId, id, request.ExpectedVersion);
        var result = await _deactivateProductHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("products/{id:guid}/reactivate")]
    public async Task<IActionResult> ReactivateProduct(
        [FromRoute] Guid id,
        [FromBody] ReactivateProductRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var actorId = actor?.UserId ?? request.ActorId ?? Guid.Empty;
        var command = new ReactivateProductCommand(actorId, id, request.ExpectedVersion);
        var result = await _reactivateProductHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("companies")]
    public async Task<IActionResult> GetCompanies(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var companies = await _productReads.GetCompaniesAsync(includeInactive, cancellationToken);
        return Ok(companies);
    }

    [HttpPost("companies")]
    public async Task<IActionResult> CreateCompany(
        [FromBody] SaveCompanyCommand command,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _saveCompanyHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("companies/{id:guid}/active")]
    public async Task<IActionResult> SetCompanyActive(
        [FromRoute] Guid id,
        [FromBody] SetCatalogReferenceActiveRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var result = await _setCompanyActiveHandler.HandleAsync(
            new SetCompanyActiveCommand(actor?.UserId ?? Guid.Empty, id, request.IsActive),
            cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var categories = await _productReads.GetCategoriesAsync(includeInactive, cancellationToken);
        return Ok(categories);
    }

    [HttpPost("categories")]
    public async Task<IActionResult> CreateCategory(
        [FromBody] SaveCategoryCommand command,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _saveCategoryHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("categories/{id:guid}/active")]
    public async Task<IActionResult> SetCategoryActive(
        [FromRoute] Guid id,
        [FromBody] SetCatalogReferenceActiveRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var result = await _setCategoryActiveHandler.HandleAsync(
            new SetCategoryActiveCommand(actor?.UserId ?? Guid.Empty, id, request.IsActive),
            cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    [HttpGet("units")]
    public async Task<IActionResult> GetUnits(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var units = await _productReads.GetUnitsAsync(includeInactive, cancellationToken);
        return Ok(units);
    }

    [HttpPost("units")]
    public async Task<IActionResult> SaveUnit(
        [FromBody] SaveUnitCommand command,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var sanitized = actor is not null ? command with { ActorId = actor.UserId } : command;
        var result = await _saveUnitHandler.HandleAsync(sanitized, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("units/{id:guid}/active")]
    public async Task<IActionResult> SetUnitActive(
        [FromRoute] Guid id,
        [FromBody] SetCatalogReferenceActiveRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var result = await _setUnitActiveHandler.HandleAsync(
            new SetUnitActiveCommand(actor?.UserId ?? Guid.Empty, id, request.IsActive),
            cancellationToken);
        return result.IsSuccess ? Ok(new { success = true }) : ToActionResult(result);
    }

    [HttpPost("products/{id:guid}/units")]
    public async Task<IActionResult> ConfigureUnits(
        [FromRoute] Guid id,
        [FromBody] IReadOnlyList<ProductUnitInput> units,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var command = new ConfigureProductUnitsCommand(id, units, actor?.UserId ?? Guid.Empty);
        var result = await _configureUnitsHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("units/{unitId:guid}/barcode")]
    public async Task<IActionResult> SetBarcode(
        [FromRoute] Guid unitId,
        [FromBody] SetBarcodeRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.InventoryManage);
        if (denied is not null)
        {
            return denied;
        }

        var actor = HttpContext.GetActorContext();
        var command = new SetProductUnitBarcodeCommand(unitId, request.Barcode, actor?.UserId ?? Guid.Empty);
        var result = await _setBarcodeHandler.HandleAsync(command, cancellationToken);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        var error = result.Error!;
        var statusCode = error.Code switch
        {
            var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
            var c when c.Contains("mismatch") => StatusCodes.Status409Conflict,
            var c when c.Contains("locked") => StatusCodes.Status409Conflict,
            var c when c.Contains("duplicate") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }

    private IActionResult ToActionResult(Result result)
    {
        if (result.IsSuccess)
        {
            return Ok(new { success = true });
        }

        var error = result.Error!;
        var statusCode = error.Code switch
        {
            var c when c.Contains("not_found") => StatusCodes.Status404NotFound,
            var c when c.Contains("mismatch") => StatusCodes.Status409Conflict,
            var c when c.Contains("locked") => StatusCodes.Status409Conflict,
            var c when c.Contains("duplicate") => StatusCodes.Status409Conflict,
            var c when c.StartsWith("auth.") || c.StartsWith("authorization.") => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };

        return StatusCode(statusCode, new { code = error.Code, message = error.Message });
    }
}

public sealed record DeactivateProductRequest(long ExpectedVersion, Guid? ActorId = null);
public sealed record ReactivateProductRequest(long ExpectedVersion, Guid? ActorId = null);
public sealed record SetBarcodeRequest(string Barcode);
public sealed record SetCatalogReferenceActiveRequest(bool IsActive, long? ExpectedVersion = null);
