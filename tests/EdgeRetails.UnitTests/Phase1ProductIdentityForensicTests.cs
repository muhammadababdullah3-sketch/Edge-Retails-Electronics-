using System.Text.RegularExpressions;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase1ProductIdentityForensicTests
{
    #region Workstream A: Units

    [Fact]
    public async Task Unit_Create_Success()
    {
        var env = new TestEnvironment();
        var handler = new SaveUnitHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);

        var result = await handler.HandleAsync(
            new SaveUnitCommand(env.ActorId, null, "Piece", "Pcs", 0),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var unit = await env.Catalog.GetUnitAsync(result.Value, CancellationToken.None);
        Assert.NotNull(unit);
        Assert.Equal("Piece", unit.Name);
        Assert.Equal("Pcs", unit.Symbol);
        Assert.Equal(0, unit.DisplayDecimalPlaces);
        Assert.True(unit.IsActive);
    }

    [Theory]
    [InlineData("Pcs", "pcs")]
    [InlineData("Piece", "PIECE")]
    [InlineData("Piece", "  Piece  ")]
    [InlineData("Box Large", "Box   Large")]
    public async Task Unit_Duplicate_Name_CaseAndWhitespace_Rejected(string existingName, string duplicateName)
    {
        var env = new TestEnvironment();
        var existing = new Unit { Id = Guid.NewGuid(), Name = existingName, Symbol = "EX1", IsActive = true };
        env.Catalog.AddUnit(existing);

        var handler = new SaveUnitHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new SaveUnitCommand(env.ActorId, null, duplicateName, "SYM2", 0),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.unit_duplicate", result.Error?.Code);
    }

    [Theory]
    [InlineData("pcs", "PCS")]
    [InlineData("pcs", "  pcs  ")]
    [InlineData("kg", "KG")]
    public async Task Unit_Duplicate_Symbol_CaseAndWhitespace_Rejected(string existingSymbol, string duplicateSymbol)
    {
        var env = new TestEnvironment();
        var existing = new Unit { Id = Guid.NewGuid(), Name = "Existing Unit", Symbol = existingSymbol, IsActive = true };
        env.Catalog.AddUnit(existing);

        var handler = new SaveUnitHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new SaveUnitCommand(env.ActorId, null, "Different Name", duplicateSymbol, 0),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.unit_duplicate", result.Error?.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public async Task Unit_DecimalPlaces_Invalid_Rejected(int decimals)
    {
        var env = new TestEnvironment();
        var handler = new SaveUnitHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);

        var result = await handler.HandleAsync(
            new SaveUnitCommand(env.ActorId, null, "Valid Unit", "VU", decimals),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.unit_invalid", result.Error?.Code);
    }

    [Fact]
    public async Task Unit_Deactivate_InUse_Rejected()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        env.Catalog.AddUnit(unit);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Active Product",
            Sku = "SKU-000001",
            BaseUnitId = unit.Id,
            IsActive = true
        };
        env.Catalog.AddProduct(product);

        var handler = new SetUnitActiveHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new SetUnitActiveCommand(env.ActorId, unit.Id, false),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.unit_in_use", result.Error?.Code);
        Assert.True(unit.IsActive);
    }

    [Fact]
    public async Task Unit_Deactivate_And_Reactivate_Success()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Unused Unit", Symbol = "UU", IsActive = true };
        env.Catalog.AddUnit(unit);

        var handler = new SetUnitActiveHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);

        var deactivateResult = await handler.HandleAsync(
            new SetUnitActiveCommand(env.ActorId, unit.Id, false),
            CancellationToken.None);
        Assert.True(deactivateResult.IsSuccess);
        Assert.False(unit.IsActive);

        var reactivateResult = await handler.HandleAsync(
            new SetUnitActiveCommand(env.ActorId, unit.Id, true),
            CancellationToken.None);
        Assert.True(reactivateResult.IsSuccess);
        Assert.True(unit.IsActive);
    }

    #endregion

    #region Workstream B: Categories

    [Fact]
    public async Task Category_Create_Success()
    {
        var env = new TestEnvironment();
        var handler = new SaveCategoryHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);

        var result = await handler.HandleAsync(
            new SaveCategoryCommand(env.ActorId, null, "Mobile Phones"),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var category = await env.Catalog.GetCategoryAsync(result.Value, CancellationToken.None);
        Assert.NotNull(category);
        Assert.Equal("Mobile Phones", category.Name);
        Assert.True(category.IsActive);
    }

    [Theory]
    [InlineData("Mobile Phones", "mobile phones")]
    [InlineData("Mobile Phones", "MOBILE PHONES")]
    [InlineData("Mobile Phones", "  Mobile Phones  ")]
    [InlineData("Smart Phones", "Smart   Phones")]
    public async Task Category_Duplicate_Name_CaseAndWhitespace_Rejected(string existingName, string duplicateName)
    {
        var env = new TestEnvironment();
        var existing = new Category { Id = Guid.NewGuid(), Name = existingName, IsActive = true };
        env.Catalog.AddCategory(existing);

        var handler = new SaveCategoryHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new SaveCategoryCommand(env.ActorId, null, duplicateName),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.category_duplicate", result.Error?.Code);
    }

    [Fact]
    public async Task Category_Deactivate_InUse_Rejected()
    {
        var env = new TestEnvironment();
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddCategory(category);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Smartphone",
            Sku = "SKU-000001",
            CategoryId = category.Id,
            IsActive = true
        };
        env.Catalog.AddProduct(product);

        var handler = new SetCategoryActiveHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new SetCategoryActiveCommand(env.ActorId, category.Id, false),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.category_in_use", result.Error?.Code);
        Assert.True(category.IsActive);
    }

    [Fact]
    public async Task Category_Rename_DoesNotAffectProductOrSku()
    {
        var env = new TestEnvironment();
        var category = new Category { Id = Guid.NewGuid(), Name = "Old Category", IsActive = true };
        env.Catalog.AddCategory(category);

        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        env.Catalog.AddUnit(unit);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Galaxy S24",
            Sku = "SKU-000042",
            CategoryId = category.Id,
            BaseUnitId = unit.Id,
            IsActive = true,
            Version = 1
        };
        env.Catalog.AddProduct(product);

        var handler = new SaveCategoryHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var renameResult = await handler.HandleAsync(
            new SaveCategoryCommand(env.ActorId, category.Id, "Flagship Smartphones"),
            CancellationToken.None);

        Assert.True(renameResult.IsSuccess);
        Assert.Equal("Flagship Smartphones", category.Name);

        var productAfter = await env.Catalog.GetProductAsync(product.Id, CancellationToken.None);
        Assert.NotNull(productAfter);
        Assert.Equal("SKU-000042", productAfter.Sku);
        Assert.Equal(product.Id, productAfter.Id);
        Assert.Equal(1, productAfter.Version);
    }

    #endregion

    #region Workstream C: Semantic SKU Authority & Immutability

    [Fact]
    public async Task Product_Creation_Suggests_Deterministic_Semantic_Sku()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var handler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);

        // Pak Fan Deluxe 56 -> PKF-DLX56
        var fanResult = await handler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id) with
            {
                Brand = "Pak Fan",
                Name = "Deluxe",
                Model = "56",
                Sku = null
            }),
            CancellationToken.None);

        // Royal Fan Model 234
        var royalResult = await handler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id) with
            {
                Brand = "Royal Fan",
                Name = "Fan",
                Model = "234",
                Sku = null
            }),
            CancellationToken.None);

        Assert.True(fanResult.IsSuccess);
        Assert.True(royalResult.IsSuccess);

        var fan = await env.Catalog.GetProductAsync(fanResult.Value!.ProductId, CancellationToken.None);
        var royal = await env.Catalog.GetProductAsync(royalResult.Value!.ProductId, CancellationToken.None);

        Assert.Equal("PKF-DLX56", fan!.Sku);
        Assert.Equal("RYF-234", royal!.Sku);
    }

    [Fact]
    public async Task Product_Creation_100_Concurrent_Creates_Produces_100_Unique_Skus()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var handler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);

        var tasks = Enumerable.Range(1, 100).Select(async i =>
        {
            var result = await handler.HandleAsync(
                new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id) with
                {
                    Name = $"Product {i}",
                    Model = $"X{i}"
                }),
                CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error?.Message);
            return result.Value!.ProductId;
        }).ToArray();

        var productIds = await Task.WhenAll(tasks);
        Assert.Equal(100, productIds.Length);

        var allProducts = await env.Catalog.GetProductsAsync(true, CancellationToken.None);
        var skus = allProducts.Select(p => p.Sku).Where(s => s != null).ToList();

        Assert.Equal(100, skus.Count);
        Assert.Equal(100, skus.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Product_Update_Attempted_Manual_Sku_Overwrite_Rejected_When_History_Exists()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var createHandler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var created = await createHandler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id)),
            CancellationToken.None);
        Assert.True(created.IsSuccess);

        var product = await env.Catalog.GetProductAsync(created.Value!.ProductId, CancellationToken.None);
        Assert.NotNull(product);
        Assert.Equal("APX-SMRX10", product.Sku);

        var updateHandler = new UpdateProductHandler(
            env.Catalog,
            env.Authorizer,
            new FakeProductCatalogSafetyReadService(true), // History exists!
            env.Transactions,
            env.UnitOfWork);

        var updateResult = await updateHandler.HandleAsync(
            new UpdateProductCommand(
                env.ActorId,
                product.Id,
                product.Version,
                ValidInput(unit.Id, category.Id) with { Sku = "APX-NEWCODE" }),
            CancellationToken.None);

        Assert.False(updateResult.IsSuccess);
        Assert.Equal("catalog.sku_immutable", updateResult.Error?.Code);

        // Verify product SKU in repository was not modified
        var recheck = await env.Catalog.GetProductAsync(product.Id, CancellationToken.None);
        Assert.Equal("APX-SMRX10", recheck!.Sku);
    }

    [Fact]
    public async Task Product_Update_Allows_Sku_Correction_Before_History()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var createHandler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var created = await createHandler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id)),
            CancellationToken.None);
        Assert.True(created.IsSuccess);

        var product = await env.Catalog.GetProductAsync(created.Value!.ProductId, CancellationToken.None);
        Assert.NotNull(product);
        Assert.Equal("APX-SMRX10", product.Sku);

        var updateHandler = new UpdateProductHandler(
            env.Catalog,
            env.Authorizer,
            new FakeProductCatalogSafetyReadService(false), // No history yet
            env.Transactions,
            env.UnitOfWork);

        var updateResult = await updateHandler.HandleAsync(
            new UpdateProductCommand(
                env.ActorId,
                product.Id,
                product.Version,
                ValidInput(unit.Id, category.Id) with { Sku = "APX-CORRECTED" }),
            CancellationToken.None);

        Assert.True(updateResult.IsSuccess);
        var recheck = await env.Catalog.GetProductAsync(product.Id, CancellationToken.None);
        Assert.Equal("APX-CORRECTED", recheck!.Sku);
    }

    [Fact]
    public async Task Product_Update_Preserves_Sku_On_Product_Rename()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var createHandler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var created = await createHandler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id) with { Name = "Original Name" }),
            CancellationToken.None);

        var productBefore = await env.Catalog.GetProductAsync(created.Value!.ProductId, CancellationToken.None);
        var originalSku = productBefore!.Sku;

        var updateHandler = new UpdateProductHandler(
            env.Catalog,
            env.Authorizer,
            new FakeProductCatalogSafetyReadService(false),
            env.Transactions,
            env.UnitOfWork);

        var updateResult = await updateHandler.HandleAsync(
            new UpdateProductCommand(
                env.ActorId,
                created.Value.ProductId,
                created.Value.Version,
                ValidInput(unit.Id, category.Id) with { Name = "Renamed Name", Sku = null }),
            CancellationToken.None);

        Assert.True(updateResult.IsSuccess);
        var product = await env.Catalog.GetProductAsync(created.Value.ProductId, CancellationToken.None);
        Assert.Equal("Renamed Name", product!.Name);
        Assert.Equal(originalSku, product.Sku);
    }

    [Fact]
    public async Task Product_Deactivate_And_Reactivate_Preserves_Sku()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var createHandler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var created = await createHandler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id)),
            CancellationToken.None);

        var pCreated = await env.Catalog.GetProductAsync(created.Value!.ProductId, CancellationToken.None);
        var originalSku = pCreated!.Sku;

        var deactivateHandler = new DeactivateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var deactResult = await deactivateHandler.HandleAsync(
            new DeactivateProductCommand(env.ActorId, created.Value.ProductId, created.Value.Version),
            CancellationToken.None);
        Assert.True(deactResult.IsSuccess);

        var pDeactivated = await env.Catalog.GetProductAsync(created.Value.ProductId, CancellationToken.None);
        Assert.False(pDeactivated!.IsActive);
        Assert.Equal(originalSku, pDeactivated.Sku);

        var reactivateHandler = new ReactivateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var reactResult = await reactivateHandler.HandleAsync(
            new ReactivateProductCommand(env.ActorId, created.Value.ProductId, deactResult.Value!.Version),
            CancellationToken.None);
        Assert.True(reactResult.IsSuccess);

        var pReactivated = await env.Catalog.GetProductAsync(created.Value.ProductId, CancellationToken.None);
        Assert.True(pReactivated!.IsActive);
        Assert.Equal(originalSku, pReactivated.Sku);
    }

    #endregion

    #region Workstream D: Product Creation & Base ProductUnit

    [Fact]
    public async Task Product_Creation_Atomically_Creates_Product_And_BaseProductUnit()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var handler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var product = await env.Catalog.GetProductAsync(result.Value!.ProductId, CancellationToken.None);
        Assert.NotNull(product);

        var productUnits = await env.Catalog.GetProductUnitsAsync(product.Id, CancellationToken.None);
        var baseUnit = Assert.Single(productUnits);

        Assert.Equal(product.Id, baseUnit.ProductId);
        Assert.Equal(unit.Id, baseUnit.UnitId);
        Assert.Equal(1m, baseUnit.FactorToBaseUnit);
        Assert.True(baseUnit.CanPurchase);
        Assert.True(baseUnit.CanSell);
        Assert.True(baseUnit.CanUseInThaka);
        Assert.True(baseUnit.IsDefaultPurchaseUnit);
        Assert.True(baseUnit.IsDefaultSaleUnit);
        Assert.True(baseUnit.IsActive);
    }

    [Fact]
    public async Task Product_Creation_Missing_Category_Rejected()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        env.Catalog.AddUnit(unit);

        var handler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, null) with { CategoryId = null }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.category_required", result.Error?.Code);
    }

    [Fact]
    public async Task Product_Creation_Inactive_Category_Rejected()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Deprecated Category", IsActive = false };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var handler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id)),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.category_unavailable", result.Error?.Code);
    }

    [Fact]
    public async Task Product_Creation_Missing_Or_Inactive_BaseUnit_Rejected()
    {
        var env = new TestEnvironment();
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddCategory(category);

        var handler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);

        // Missing BaseUnit
        var missingResult = await handler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(Guid.NewGuid(), category.Id)),
            CancellationToken.None);
        Assert.False(missingResult.IsSuccess);
        Assert.Equal("catalog.base_unit_unavailable", missingResult.Error?.Code);

        // Inactive BaseUnit
        var inactiveUnit = new Unit { Id = Guid.NewGuid(), Name = "Inactive Unit", Symbol = "IU", IsActive = false };
        env.Catalog.AddUnit(inactiveUnit);

        var inactiveResult = await handler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(inactiveUnit.Id, category.Id)),
            CancellationToken.None);
        Assert.False(inactiveResult.IsSuccess);
        Assert.Equal("catalog.base_unit_unavailable", inactiveResult.Error?.Code);
    }

    [Theory]
    [InlineData(-10, 100, 5)]
    [InlineData(10, -50, 5)]
    [InlineData(10, 100, -2)]
    public async Task Product_Creation_Negative_Prices_Or_Stock_Rejected(decimal refCost, decimal salePrice, decimal minStock)
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var handler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new CreateProductCommand(
                env.ActorId,
                ValidInput(unit.Id, category.Id) with
                {
                    ReferencePurchaseCost = refCost,
                    DefaultSalePrice = salePrice,
                    MinimumStockLevel = minStock
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.price_or_threshold_negative", result.Error?.Code);
    }

    [Fact]
    public async Task Product_Creation_Serialized_Requires_Serial_Or_Imei()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var handler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var result = await handler.HandleAsync(
            new CreateProductCommand(
                env.ActorId,
                ValidInput(unit.Id, category.Id) with
                {
                    TrackingMode = TrackingMode.Serialized,
                    SerialTrackingEnabled = false,
                    ImeiTrackingEnabled = false
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.serialized_identity_required", result.Error?.Code);
    }

    [Fact]
    public async Task Product_Update_BaseUnit_Change_Rejected()
    {
        var env = new TestEnvironment();
        var unit1 = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var unit2 = new Unit { Id = Guid.NewGuid(), Name = "Box", Symbol = "Box", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit1);
        env.Catalog.AddUnit(unit2);
        env.Catalog.AddCategory(category);

        var createHandler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var created = await createHandler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit1.Id, category.Id)),
            CancellationToken.None);

        var updateHandler = new UpdateProductHandler(
            env.Catalog,
            env.Authorizer,
            new FakeProductCatalogSafetyReadService(false),
            env.Transactions,
            env.UnitOfWork);

        var result = await updateHandler.HandleAsync(
            new UpdateProductCommand(
                env.ActorId,
                created.Value!.ProductId,
                created.Value.Version,
                ValidInput(unit2.Id, category.Id)),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.base_unit_change_requires_reconfiguration", result.Error?.Code);
    }

    [Fact]
    public async Task Product_Update_TrackingPolicy_Locked_When_History_Exists()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var createHandler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var created = await createHandler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id)),
            CancellationToken.None);

        // Safety service reports stock or history exists
        var updateHandler = new UpdateProductHandler(
            env.Catalog,
            env.Authorizer,
            new FakeProductCatalogSafetyReadService(hasStockOrHistory: true),
            env.Transactions,
            env.UnitOfWork);

        var result = await updateHandler.HandleAsync(
            new UpdateProductCommand(
                env.ActorId,
                created.Value!.ProductId,
                created.Value.Version,
                ValidInput(unit.Id, category.Id) with
                {
                    TrackingMode = TrackingMode.Serialized,
                    SerialTrackingEnabled = true
                }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.tracking_policy_locked", result.Error?.Code);
    }

    [Fact]
    public async Task Product_Update_StaleVersion_Rejected()
    {
        var env = new TestEnvironment();
        var unit = new Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "Pcs", IsActive = true };
        var category = new Category { Id = Guid.NewGuid(), Name = "Electronics", IsActive = true };
        env.Catalog.AddUnit(unit);
        env.Catalog.AddCategory(category);

        var createHandler = new CreateProductHandler(env.Catalog, env.Authorizer, env.Transactions, env.UnitOfWork);
        var created = await createHandler.HandleAsync(
            new CreateProductCommand(env.ActorId, ValidInput(unit.Id, category.Id)),
            CancellationToken.None);

        var updateHandler = new UpdateProductHandler(
            env.Catalog,
            env.Authorizer,
            new FakeProductCatalogSafetyReadService(false),
            env.Transactions,
            env.UnitOfWork);

        var result = await updateHandler.HandleAsync(
            new UpdateProductCommand(
                env.ActorId,
                created.Value!.ProductId,
                ExpectedVersion: 999, // Stale version
                ValidInput(unit.Id, category.Id) with { Name = "Stale Edit" }),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("concurrency.stale_product", result.Error?.Code);
    }

    #endregion

    #region Helpers & Test Environment

    private static ProductCatalogInput ValidInput(Guid unitId, Guid? categoryId) =>
        new(
            Name: "Smartphone Pro",
            Sku: null,
            Brand: "Apex",
            Model: "X10",
            CategoryId: categoryId,
            BaseUnitId: unitId,
            TrackingMode: TrackingMode.Quantity,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 50000m,
            DefaultSalePrice: 65000m,
            MinimumStockLevel: 5m,
            DefaultWarrantyMonths: 12,
            AttributesJson: null,
            AttributesSchemaVersion: 1);

    private sealed class TestEnvironment
    {
        public Guid ActorId { get; } = Guid.NewGuid();
        public InMemoryCatalogRepository Catalog { get; } = new();
        public IApplicationPermissionAuthorizer Authorizer { get; } = new PermissiveAuthorizer();
        public ITransactionRunner Transactions { get; } = new PassThroughTransactionRunner();
        public IUnitOfWork UnitOfWork { get; } = new PassThroughUnitOfWork();
    }

    private sealed class InMemoryCatalogRepository : ICatalogRepository
    {
        private readonly Dictionary<Guid, Product> _products = new();
        private readonly Dictionary<Guid, Company> _companies = new();
        private readonly Dictionary<Guid, Category> _categories = new();
        private readonly Dictionary<Guid, Unit> _units = new();
        private readonly Dictionary<Guid, ProductUnit> _productUnits = new();
        private readonly object _lock = new();
        private long _skuSeq;

        public Task<string> AllocateNextSkuAsync(CancellationToken cancellationToken)
        {
            var next = Interlocked.Increment(ref _skuSeq);
            return Task.FromResult($"SKU-{next:D6}");
        }

        public Task<Product?> GetProductAsync(Guid productId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_products.TryGetValue(productId, out var p) ? p : null);
            }
        }

        public Task<Product?> GetProductForUpdateAsync(Guid productId, CancellationToken cancellationToken) =>
            GetProductAsync(productId, cancellationToken);

        public Task<Product?> GetProductBySkuAsync(string normalizedSku, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_products.Values.FirstOrDefault(p =>
                    string.Equals(p.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public Task<IReadOnlyList<Product>> GetProductsAsync(bool includeInactive, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<Product>>(_products.Values
                    .Where(p => includeInactive || p.IsActive)
                    .ToList());
            }
        }

        public Task<Company?> GetCompanyAsync(Guid companyId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_companies.TryGetValue(companyId, out var c) ? c : null);
            }
        }

        public Task<Company?> GetCompanyForUpdateAsync(Guid companyId, CancellationToken cancellationToken) =>
            GetCompanyAsync(companyId, cancellationToken);

        public Task<Company?> GetCompanyByCodeAsync(string normalizedCode, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_companies.Values.FirstOrDefault(c =>
                    string.Equals(c.Code, normalizedCode, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public Task<Company?> GetCompanyByNameAsync(string name, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_companies.Values.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public Task<IReadOnlyList<Company>> GetCompaniesAsync(bool includeInactive, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<Company>>(_companies.Values
                    .Where(c => includeInactive || c.IsActive)
                    .ToList());
            }
        }

        public Task<bool> IsCompanyInUseByActiveProductAsync(Guid companyId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_products.Values.Any(p => p.IsActive && p.CompanyId == companyId));
            }
        }

        public Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_categories.TryGetValue(categoryId, out var c) ? c : null);
            }
        }

        public Task<Category?> GetCategoryForUpdateAsync(Guid categoryId, CancellationToken cancellationToken) =>
            GetCategoryAsync(categoryId, cancellationToken);

        public Task<Category?> GetCategoryByIdentitySymbolAsync(string normalizedSymbol, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_categories.Values.FirstOrDefault(c =>
                    string.Equals(c.IdentitySymbol, normalizedSymbol, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public Task<Category?> GetCategoryByNameAsync(string name, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_categories.Values.FirstOrDefault(c =>
                    string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public Task<IReadOnlyList<Category>> GetCategoriesAsync(bool includeInactive, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<Category>>(_categories.Values
                    .Where(c => includeInactive || c.IsActive)
                    .ToList());
            }
        }

        public Task<bool> IsCategoryInUseByActiveProductAsync(Guid categoryId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_products.Values.Any(p => p.IsActive && p.CategoryId == categoryId));
            }
        }

        public Task<Unit?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_units.TryGetValue(unitId, out var u) ? u : null);
            }
        }

        public Task<Unit?> GetUnitForUpdateAsync(Guid unitId, CancellationToken cancellationToken) =>
            GetUnitAsync(unitId, cancellationToken);

        public Task<IReadOnlyList<Unit>> GetUnitsAsync(bool includeInactive, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<Unit>>(_units.Values
                    .Where(u => includeInactive || u.IsActive)
                    .ToList());
            }
        }

        public Task<bool> IsUnitInUseByActiveCatalogAsync(Guid unitId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_products.Values.Any(p => p.IsActive && p.BaseUnitId == unitId))
                {
                    return Task.FromResult(true);
                }

                return Task.FromResult(_productUnits.Values.Any(pu => pu.IsActive && pu.UnitId == unitId));
            }
        }

        public Task<ProductUnit?> GetProductUnitAsync(Guid productUnitId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_productUnits.TryGetValue(productUnitId, out var pu) ? pu : null);
            }
        }

        public Task<ProductUnit?> GetProductUnitAsync(Guid productId, Guid unitId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult(_productUnits.Values.FirstOrDefault(pu => pu.ProductId == productId && pu.UnitId == unitId));
            }
        }

        public Task<IReadOnlyList<ProductUnit>> GetProductUnitsAsync(Guid productId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<ProductUnit>>(_productUnits.Values.Where(pu => pu.ProductId == productId).ToList());
            }
        }

        public Task<ProductUnitBarcode?> GetBarcodeAsync(string barcode, CancellationToken cancellationToken) =>
            Task.FromResult<ProductUnitBarcode?>(null);

        public Task<IReadOnlyList<Product>> GetActiveProductsAsync(StocktakeScope scope, Guid? categoryId, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                return Task.FromResult<IReadOnlyList<Product>>(_products.Values
                    .Where(p => p.IsActive && (!categoryId.HasValue || p.CategoryId == categoryId))
                    .ToList());
            }
        }

        public void AddCompany(Company company)
        {
            lock (_lock)
            {
                if (company.Id == Guid.Empty)
                {
                    company.Id = Guid.NewGuid();
                }
                _companies[company.Id] = company;
            }
        }

        public void AddCategory(Category category)
        {
            lock (_lock)
            {
                if (category.Id == Guid.Empty)
                {
                    category.Id = Guid.NewGuid();
                }
                _categories[category.Id] = category;
            }
        }

        public void AddUnit(Unit unit)
        {
            lock (_lock)
            {
                if (unit.Id == Guid.Empty)
                {
                    unit.Id = Guid.NewGuid();
                }
                _units[unit.Id] = unit;
            }
        }

        public void AddProduct(Product product)
        {
            lock (_lock)
            {
                if (product.Id == Guid.Empty)
                {
                    product.Id = Guid.NewGuid();
                }
                _products[product.Id] = product;
            }
        }

        public void AddProductUnit(ProductUnit productUnit)
        {
            lock (_lock)
            {
                if (productUnit.Id == Guid.Empty)
                {
                    productUnit.Id = Guid.NewGuid();
                }
                _productUnits[productUnit.Id] = productUnit;
            }
        }

        public void AddBarcode(ProductUnitBarcode barcode) { }
    }

    private sealed class PermissiveAuthorizer : IApplicationPermissionAuthorizer
    {
        public Task<Result> AuthorizeAsync(Guid actorId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class PassThroughTransactionRunner : ITransactionRunner
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            operation(cancellationToken);
    }

    private sealed class PassThroughUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(1);
    }

    private sealed class FakeProductCatalogSafetyReadService : IProductCatalogSafetyReadService
    {
        private readonly bool _hasStockOrHistory;
        public FakeProductCatalogSafetyReadService(bool hasStockOrHistory) => _hasStockOrHistory = hasStockOrHistory;

        public Task<bool> HasStockOrHistoryAsync(Guid productId, CancellationToken cancellationToken) =>
            Task.FromResult(_hasStockOrHistory);
    }

    #endregion
}
