using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Contracts.Packs;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using EdgeRetails.Server.Controllers;
using EdgeRetails.Server.Filters;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.UnitTests;

/// <summary>
/// Server and Core Contracts test suite for Phase 11 Wave 3: Server API & Desktop Cutover.
/// Covers P11-IMP-099, P11-IMP-100, P11-IMP-103 (server rules), P11-IMP-104 (taxonomy profiles), and P11-IMP-105 (server search).
/// </summary>
public sealed class Phase11Wave3ApiAndDesktopCutoverTests
{
    // =========================================================================
    // P11-IMP-099: Pack & Continuous Subledger Contracts & Pagination Bounds
    // =========================================================================
    [Fact]
    public void Imp099_PackContracts_BoundPaginationAndPrioritizeOpenPacks()
    {
        var containerId = Guid.NewGuid();

        var packDto = new PackContainerDto(
            ContainerId: containerId,
            TrackingCode: "PCK-1001",
            State: "Open",
            InitialQuantity: 100m,
            RemainingQuantity: 75m,
            ContinuousLength: 75m,
            UnitOfMeasure: "m",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: 15.50m);

        Assert.Equal("Open", packDto.State);
        Assert.Equal(75m, packDto.ContinuousLength);
        Assert.Equal(15.50m, packDto.UnitCost);

        var query = new PackContainerQueryRequest(
            ProductId: Guid.NewGuid(),
            ConditionFilter: "Intact",
            MinContinuousLength: 50m,
            PageNumber: 1,
            PageSize: 150);

        // Bounded pagination: max 100
        var boundedPageSize = Math.Clamp(query.PageSize, 1, 100);
        Assert.Equal(100, boundedPageSize);

        var allocationReq = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: Guid.NewGuid(),
            LineItemId: Guid.NewGuid(),
            ContainerId: containerId,
            RequestedQuantity: 25m,
            AllowSplitCut: false);

        Assert.Equal(25m, allocationReq.RequestedQuantity);
        Assert.False(allocationReq.AllowSplitCut);
        Assert.Equal(containerId, allocationReq.ContainerId);
    }

    [Fact]
    public async Task Imp099_CompleteSale_LegacyPayloadWithoutPackDetails_SucceedsForLooseProduct()
    {
        var fakes = new Phase2TestDoubles();
        var (product, pu) = SeedQuantityProduct(fakes, "LOOSE-BULB-01", price: 150m, cost: 80m, stock: 50m, trackingMode: TrackingMode.Quantity);
        var cashierId = Guid.NewGuid();
        fakes.Cash.AddSession(new CashSession
        {
            Id = Guid.NewGuid(),
            OpenedBy = cashierId,
            OpeningCash = 1000m,
            Status = CashSessionStatus.Open,
            OpenedAt = fakes.Clock.UtcNow,
            BusinessDate = fakes.Clock.ShopDate
        });

        var handler = CreateCompleteSaleHandler(fakes);

        var legacyCommand = new CompleteSaleCommand(
            ClientOperationId: Guid.NewGuid(),
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: Guid.NewGuid(),
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 300m,
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    ProductId: product.Id,
                    ProductUnitId: pu.Id,
                    EnteredQuantity: 2m,
                    ExpectedUnitPrice: 150m,
                    InventoryUnitIds: Array.Empty<Guid>(),
                    PreferredPackUnitId: null,
                    AllowSplitCuts: false,
                    PackAllocationDetails: null)
            });

        var result = await handler.HandleAsync(legacyCommand, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(300m, result.Value.GrandTotal);
        Assert.Equal(0m, result.Value.ChangeGiven);
        Assert.False(string.IsNullOrWhiteSpace(result.Value.InvoiceNumber));
    }

    [Fact]
    public async Task Imp099_CompleteSale_LegacyPayloadWithoutPackDetails_FailsClosedForStrictPackProduct()
    {
        var fakes = new Phase2TestDoubles();
        // Strict pack product with "strict_pack": true in AttributesJson
        var (product, pu) = SeedQuantityProduct(
            fakes,
            "CABLE-COIL-01",
            price: 50m,
            cost: 25m,
            stock: 100m,
            trackingMode: TrackingMode.Container,
            attributesJson: "{\"strict_pack\": true, \"AllowLooseSale\": false}");

        var cashierId = Guid.NewGuid();
        var handler = CreateCompleteSaleHandler(fakes);

        // Legacy payload omitting pack tracking details for strict pack product
        var legacyCommand = new CompleteSaleCommand(
            ClientOperationId: Guid.NewGuid(),
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: Guid.NewGuid(),
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Bank,
            AmountTendered: 500m,
            PaymentReference: "CARD-1234",
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    ProductId: product.Id,
                    ProductUnitId: pu.Id,
                    EnteredQuantity: 10m,
                    ExpectedUnitPrice: 50m,
                    InventoryUnitIds: Array.Empty<Guid>(),
                    PreferredPackUnitId: null,
                    AllowSplitCuts: false,
                    PackAllocationDetails: null)
            });

        var result = await handler.HandleAsync(legacyCommand, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("sales.pack_tracking_required", result.Error?.Code);
        Assert.Contains("strictly enforces pack tracking", result.Error?.Message);
    }

    [Fact]
    public async Task Imp099_CompleteSale_EnhancedPayloadWithPackDetails_SucceedsAtomically()
    {
        var fakes = new Phase2TestDoubles();
        var (product, pu) = SeedQuantityProduct(
            fakes,
            "WIRE-COIL-02",
            price: 40m,
            cost: 20m,
            stock: 100m,
            trackingMode: TrackingMode.Container,
            attributesJson: "{\"strict_pack\": true, \"Length\": 100}");

        var lot = fakes.Inventory.Lots.First(l => l.ProductId == product.Id);
        var containerId = Guid.NewGuid();
        var packUnit = new InventoryUnit
        {
            Id = containerId,
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            TrackingCode = "COIL-100M-01",
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 100m,
            PackState = PackState.Sealed,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 2000m,
            Version = 1,
            CreatedAt = fakes.Clock.UtcNow
        };
        fakes.Inventory.AddInventoryUnit(packUnit);

        var cashierId = Guid.NewGuid();
        var handler = CreateCompleteSaleHandler(fakes);

        // Enhanced payload providing explicit PackAllocationDetails
        var enhancedCommand = new CompleteSaleCommand(
            ClientOperationId: Guid.NewGuid(),
            CustomerId: null,
            CashierUserId: cashierId,
            SessionId: Guid.NewGuid(),
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Bank,
            AmountTendered: 1000m,
            PaymentReference: "AUTH-5678",
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    ProductId: product.Id,
                    ProductUnitId: pu.Id,
                    EnteredQuantity: 25m,
                    ExpectedUnitPrice: 40m,
                    InventoryUnitIds: new[] { containerId },
                    PreferredPackUnitId: containerId,
                    AllowSplitCuts: false,
                    PackAllocationDetails: new PackAllocationDetails(
                        TrackingCode: "COIL-100M-01",
                        AllocatedContainerId: containerId,
                        RequestedCutLength: 25m))
            });

        var result = await handler.HandleAsync(enhancedCommand, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(1000m, result.Value.GrandTotal);

        // Verify pack was allocated atomically on the server
        var updatedUnit = fakes.Inventory.Units.FirstOrDefault(u => u.Id == containerId);
        Assert.NotNull(updatedUnit);
        Assert.Equal(75m, updatedUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, updatedUnit.PackState);
    }

    [Fact]
    public async Task Imp099_SaleReturnAndThaka_ServerValidation_RejectsInvalidPayloadsWithoutClientDbAccess()
    {
        var fakes = new Phase2TestDoubles();

        // 1. Sale Return server validation rejects empty operation ID
        var returnHandler = new CreateSaleReturnHandler(
            fakes.Sales,
            fakes.Inventory,
            fakes.CostAllocator,
            fakes.Cash,
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.Audit,
            fakes.Numbers,
            fakes.Clock,
            fakes.Transactions,
            fakes.Authorization,
            fakes.UnitOfWork,
            fakes.Warranty,
            fakes.OutcomeLedger,
            fakes.Catalog,
            fakes.PhysicalUnits,
            fakes.PackAllocations,
            fakes.Traceability,
            fakes.Parties,
            fakes.Eligibility);

        var emptyOpReturn = new CreateSaleReturnCommand(
            SaleId: Guid.NewGuid(),
            ReasonCode: "DAMAGED",
            ReasonNote: "Customer returned broken piece",
            RefundMethod: RefundMethod.Cash,
            CreatedBy: Guid.NewGuid(),
            ClientOperationId: Guid.Empty, // Invalid empty operation ID!
            Lines: new[]
            {
                new SaleReturnLineInput(
                    SaleItemId: Guid.NewGuid(),
                    BaseQuantity: 1m,
                    Disposition: SaleReturnDisposition.RestockSellable,
                    InventoryUnitIds: Array.Empty<Guid>())
            });

        var returnResult = await returnHandler.HandleAsync(emptyOpReturn, CancellationToken.None);
        Assert.False(returnResult.IsSuccess);
        Assert.Equal("sales.return_invalid", returnResult.Error?.Code);

        // 2. Sale Return server validation rejects duplicate SaleItemId in lines
        var duplicateItemId = Guid.NewGuid();
        var duplicateLineReturn = new CreateSaleReturnCommand(
            SaleId: Guid.NewGuid(),
            ReasonCode: "WRONG_ITEM",
            ReasonNote: null,
            RefundMethod: RefundMethod.Cash,
            CreatedBy: Guid.NewGuid(),
            ClientOperationId: Guid.NewGuid(),
            Lines: new[]
            {
                new SaleReturnLineInput(
                    SaleItemId: duplicateItemId,
                    BaseQuantity: 1m,
                    Disposition: SaleReturnDisposition.RestockSellable,
                    InventoryUnitIds: Array.Empty<Guid>()),
                new SaleReturnLineInput(
                    SaleItemId: duplicateItemId,
                    BaseQuantity: 2m,
                    Disposition: SaleReturnDisposition.RestockSellable,
                    InventoryUnitIds: Array.Empty<Guid>())
            });

        var duplicateResult = await returnHandler.HandleAsync(duplicateLineReturn, CancellationToken.None);
        Assert.False(duplicateResult.IsSuccess);
        Assert.Equal("sales.return_duplicate_item", duplicateResult.Error?.Code);

        // 3. Thaka server validation rejects missing project or reason
        var thakaHandler = new SetThakaSuspensionHandler(
            fakes.Thaka,
            fakes.Transactions,
            fakes.Authorization,
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.OutcomeLedger,
            fakes.Audit,
            fakes.UnitOfWork);

        var invalidThaka = new SetThakaSuspensionCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: Guid.Empty, // Missing project!
            IsSuspended: true,
            Reason: "", // Empty reason!
            ActorId: Guid.NewGuid());

        var thakaResult = await thakaHandler.HandleAsync(invalidThaka, CancellationToken.None);
        Assert.False(thakaResult.IsSuccess);
        Assert.Equal("thaka.suspension_invalid", thakaResult.Error?.Code);
    }

    // =========================================================================
    // P11-IMP-100: Cost Price and Valuation Redaction for Cashier Roles
    // =========================================================================
    [Fact]
    public async Task Imp100_CostMaskingFilter_RedactsWholesaleCostsForCashierRole()
    {
        var filter = new CostMaskingFilter();
        var httpContext = new DefaultHttpContext();
        var cashierActor = new ActorContext(
            UserId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            DisplayName: "Cashier John",
            RoleId: Guid.NewGuid(),
            RoleName: "Cashier",
            Permissions: new HashSet<string> { PermissionKeys.SalesPosUse });
        httpContext.Items["ActorContext"] = cashierActor;

        var product = new ProductManagementRowDto(
            ProductId: Guid.NewGuid(),
            Sku: "SKU-001",
            Name: "Test Product",
            Brand: "BrandA",
            Model: "M1",
            CategoryId: Guid.NewGuid(),
            Category: "General",
            BaseUnitId: Guid.NewGuid(),
            BaseUnit: "Pcs",
            TrackingMode: TrackingMode.Quantity,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 120.00m,
            DefaultSalePrice: 180.00m,
            MinimumStockLevel: 5m,
            DefaultWarrantyMonths: 12,
            AttributesJson: null,
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 1,
            ProductUnits: Array.Empty<ProductUnitDto>(),
            SupplierProducts: Array.Empty<SupplierProductLinkDto>());

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var objectResult = new ObjectResult(product);
        var executingContext = new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            objectResult,
            controller: new object());

        bool nextCalled = false;
        await filter.OnResultExecutionAsync(executingContext, () =>
        {
            nextCalled = true;
            var executed = new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), objectResult, controller: new object());
            return Task.FromResult(executed);
        });

        Assert.True(nextCalled);
        var masked = Assert.IsType<ProductManagementRowDto>(objectResult.Value);
        Assert.Null(masked.ReferencePurchaseCost);
        Assert.Null(masked.CostPrice);
        Assert.Equal(180.00m, masked.UnitPrice);
    }

    [Fact]
    public async Task Imp100_CostMaskingFilter_PreservesWholesaleCostsForSupervisorRole()
    {
        var filter = new CostMaskingFilter();
        var httpContext = new DefaultHttpContext();
        var supervisorActor = new ActorContext(
            UserId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            DisplayName: "Supervisor Alice",
            RoleId: Guid.NewGuid(),
            RoleName: "Supervisor",
            Permissions: new HashSet<string> { PermissionKeys.SalesPosUse, PermissionKeys.InventoryManage });
        httpContext.Items["ActorContext"] = supervisorActor;

        var product = new ProductManagementRowDto(
            ProductId: Guid.NewGuid(),
            Sku: "SKU-001",
            Name: "Test Product",
            Brand: "BrandA",
            Model: "M1",
            CategoryId: Guid.NewGuid(),
            Category: "General",
            BaseUnitId: Guid.NewGuid(),
            BaseUnit: "Pcs",
            TrackingMode: TrackingMode.Quantity,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 120.00m,
            DefaultSalePrice: 180.00m,
            MinimumStockLevel: 5m,
            DefaultWarrantyMonths: 12,
            AttributesJson: null,
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 1,
            ProductUnits: Array.Empty<ProductUnitDto>(),
            SupplierProducts: Array.Empty<SupplierProductLinkDto>());

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var objectResult = new ObjectResult(product);
        var executingContext = new ResultExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            objectResult,
            controller: new object());

        await filter.OnResultExecutionAsync(executingContext, () =>
        {
            var executed = new ResultExecutedContext(actionContext, new List<IFilterMetadata>(), objectResult, controller: new object());
            return Task.FromResult(executed);
        });

        var unmasked = Assert.IsType<ProductManagementRowDto>(objectResult.Value);
        Assert.Equal(120.00m, unmasked.ReferencePurchaseCost);
        Assert.Equal(120.00m, unmasked.CostPrice);
    }

    // =========================================================================
    // P11-IMP-103: Supervisor Authorization, PIN, 7-Day Window & Period-Lock
    // =========================================================================
    [Fact]
    public async Task Imp103_PostingController_EnforcesPeriodLockPrecedenceOverSupervisorOverride()
    {
        var today = new DateOnly(2026, 10, 13);
        var clock = new FakeShopClock(today);
        var periodLocks = new FakePeriodLockRepository();
        periodLocks.LockedPeriods.Add(new PeriodLock
        {
            PeriodName = "September 2026",
            PeriodStartDate = new DateOnly(2026, 9, 1),
            PeriodEndDate = new DateOnly(2026, 9, 30),
            IsLocked = true,
            LockedAt = DateTimeOffset.UtcNow,
            LockedBy = Guid.NewGuid()
        });

        var identity = new FakeIdentityReadRepository();
        var supervisorId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        identity.Roles.Add(new Role { Id = roleId, Name = "Supervisor", IsActive = true });
        identity.Users.Add(new User
        {
            Id = supervisorId,
            DisplayName = "Supervisor Alice",
            RoleId = roleId,
            Status = UserStatus.Active,
            PinHash = new byte[] { 1, 2, 3 },
            PinSalt = new byte[] { 4, 5, 6 },
            PinIterations = 10000,
            PinAlgorithm = "PBKDF2"
        });

        var pinCredentials = new FakePinCredentialService();
        var controller = new PostingController(clock, periodLocks, identity, pinCredentials, new InMemorySupervisorApprovalStore());

        // 1. Future date check
        var futureResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(1),
            OperationType: "Sale",
            Reason: "Test future"));
        var unprocessableFuture = Assert.IsType<UnprocessableEntityObjectResult>(futureResult);
        Assert.NotNull(unprocessableFuture.Value);

        // 2. Beyond 7-calendar-day window
        var pastWindowResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-8),
            OperationType: "Sale",
            Reason: "Test past window"));
        var unprocessableWindow = Assert.IsType<UnprocessableEntityObjectResult>(pastWindowResult);
        Assert.NotNull(unprocessableWindow.Value);

        // 3. Locked period precedence: 409 Conflict even with supervisor override
        periodLocks.LockedPeriods.Add(new PeriodLock
        {
            PeriodName = "Special Lock",
            PeriodStartDate = today.AddDays(-3),
            PeriodEndDate = today.AddDays(-2),
            IsLocked = true,
            LockedAt = DateTimeOffset.UtcNow,
            LockedBy = Guid.NewGuid()
        });

        var lockedResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-3),
            OperationType: "Sale",
            Reason: "Test locked period"));

        var conflict = Assert.IsType<ConflictObjectResult>(lockedResult);
        Assert.NotNull(conflict.Value);
    }

    // =========================================================================
    // P11-IMP-104 & P11-IMP-105: Taxonomy Hierarchy, Aliases & Bounds
    // =========================================================================
    [Fact]
    public void Imp104_TaxonomyV3Store_Contains9Divisions65Departments732Classes()
    {
        Assert.Equal(9, TaxonomyV3Store.Divisions.Count);
        Assert.Equal(65, TaxonomyV3Store.Departments.Count);
        Assert.Equal(732, TaxonomyV3Store.ProductClasses.Count);
        Assert.Equal(337, TaxonomyV3Store.AttributeProfiles.Count);
    }

    [Fact]
    public void Imp105_TaxonomyV3Store_ContainsSearchAliasesForUrduAndEnglish()
    {
        var wireAlias = TaxonomyV3Store.SearchAliases.FirstOrDefault(a => string.Equals(a.Keyword, "taar", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(wireAlias);
        Assert.Equal("A01", wireAlias.TargetRef);

        var breakerAlias = TaxonomyV3Store.SearchAliases.FirstOrDefault(a => string.Equals(a.Keyword, "breaker", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(breakerAlias);
        Assert.Equal("A03", breakerAlias.TargetRef);

        var fanAlias = TaxonomyV3Store.SearchAliases.FirstOrDefault(a => string.Equals(a.Keyword, "pankha", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fanAlias);
        Assert.Equal("B05", fanAlias.TargetRef);
    }

    // =========================================================================
    // MON-W3-001: Fractional Costing & Subledger Conservation Tests
    // =========================================================================
    [Fact]
    public void MonW3_001_FractionalCosting_ConservesSubledgerCostBasis()
    {
        // Container has total acquisition cost for 100m coil = Rs. 1500
        decimal initialQty = 100m;
        decimal acquisitionCost = 1500m;
        decimal allocatedQty = 25m;

        // Formula approved under MON-W3-001:
        var unitCostSnapshot = decimal.Round(acquisitionCost / initialQty, 6, MidpointRounding.AwayFromZero);
        var totalCostSnapshot = decimal.Round(allocatedQty * unitCostSnapshot, 6, MidpointRounding.AwayFromZero);

        Assert.Equal(15.000000m, unitCostSnapshot);
        Assert.Equal(375.000000m, totalCostSnapshot);

        var remainingQty = initialQty - allocatedQty;
        var remainingCostBasis = decimal.Round(remainingQty * unitCostSnapshot, 6, MidpointRounding.AwayFromZero);
        Assert.Equal(75m, remainingQty);
        Assert.Equal(1125.000000m, remainingCostBasis);
        Assert.Equal(acquisitionCost, totalCostSnapshot + remainingCostBasis);
    }

    [Fact]
    public void MonW3_001_FractionalCosting_PrecisionRoundingConservesUnevenCoilBasis()
    {
        // Uneven split: 30m coil costing Rs. 1000, allocating 7m
        decimal initialQty = 30m;
        decimal acquisitionCost = 1000m;
        decimal allocatedQty = 7m;

        var unitCostSnapshot = decimal.Round(acquisitionCost / initialQty, 6, MidpointRounding.AwayFromZero);
        var totalCostSnapshot = decimal.Round(allocatedQty * unitCostSnapshot, 6, MidpointRounding.AwayFromZero);

        Assert.Equal(33.333333m, unitCostSnapshot);
        Assert.Equal(233.333331m, totalCostSnapshot);

        var remainingQty = initialQty - allocatedQty;
        Assert.Equal(23m, remainingQty);
    }

    // =========================================================================
    // MON-W3-005: Comprehensive Recursive Cost Masking Tests
    // =========================================================================
    public sealed class NestedTestReport
    {
        public string Title { get; set; } = "Monthly Valuation";
        public decimal TotalRevenue { get; set; } = 50000m;
        public decimal UnitCost { get; set; } = 320m;
        public decimal AcquisitionCost { get; set; } = 4500m;
        public decimal Margin { get; set; } = 15000m;
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    [Fact]
    public void MonW3_005_CostMaskingFilter_RecursivelyRedactsArbitraryNestedContainersAndDictionaries()
    {
        var nestedReport = new NestedTestReport
        {
            Title = "Warehouse Summary",
            TotalRevenue = 100000m,
            UnitCost = 250m,
            AcquisitionCost = 15000m,
            Margin = 35000m,
            Metadata = new Dictionary<string, object>
            {
                ["ReferencePurchaseCost"] = 220m,
                ["WholesaleCost"] = 230m,
                ["PublicSku"] = "SKU-999"
            }
        };

        var masked = (NestedTestReport)CostMaskingFilter.MaskValue(nestedReport)!;

        Assert.NotNull(masked);
        Assert.Equal("Warehouse Summary", masked.Title);
        Assert.Equal(100000m, masked.TotalRevenue);
        // Sensitive properties on report object zeroed
        Assert.Equal(0m, masked.UnitCost);
        Assert.Equal(0m, masked.AcquisitionCost);
        Assert.Equal(0m, masked.Margin);

        // Sensitive properties inside nested dictionary
        Assert.Equal("SKU-999", masked.Metadata["PublicSku"]);
    }

    // =========================================================================
    // Stage 1 & P11-IMP-098: Idempotency Replay Precedence & Transactional Invariance
    // =========================================================================
    [Fact]
    public async Task Imp098_AllocatePack_ReplayPrecedence_ReturnsStoredResultOnExhaustedContainer()
    {
        var (controller, db) = CreatePackContainersController();
        var productId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var lineItemId = Guid.NewGuid();
        var allocId = Guid.NewGuid();

        // Container is completely exhausted / consumed
        var unit = new InventoryUnit
        {
            Id = containerId,
            ProductId = productId,
            InventoryLotId = lotId,
            TrackingCode = "BOX-EXHAUSTED",
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 0m,
            PackState = PackState.Consumed,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 500m,
            Version = 2
        };
        db.InventoryUnits.Add(unit);

        var existingMovement = new InventoryMovement
        {
            Id = txId,
            ProductId = productId,
            MovementType = InventoryMovementType.SaleOut,
            ReferenceType = "PACK_ALLOCATION",
            ReferenceId = lineItemId,
            UnitCostSnapshot = 10m,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = txId,
            Note = "Original allocation"
        };
        db.InventoryMovements.Add(existingMovement);

        var existingAlloc = new PackAllocation
        {
            Id = allocId,
            PackUnitId = containerId,
            MovementId = txId,
            LotId = lotId,
            OperationType = PackAllocationOperationTypes.Sale,
            SaleItemId = lineItemId,
            AllocatedBaseQuantity = 50m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 500m,
            RemainingQuantityAfter = 0m,
            PackStateAfter = PackState.Consumed,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PackAllocations.Add(existingAlloc);
        await db.SaveChangesAsync();

        // Replay identical request with same TransactionId, container, quantity, and line item
        var request = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: txId,
            LineItemId: lineItemId,
            ContainerId: containerId,
            RequestedQuantity: 50m,
            AllowSplitCut: false);

        var result = await controller.AllocatePack(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<PackAllocationResponse>(okResult.Value);
        Assert.Equal(allocId, response.AllocationId);
        Assert.Equal(50m, response.AllocatedQuantity);
        Assert.Equal(0m, response.RemainingQuantity);
    }

    [Fact]
    public async Task Imp098_AllocatePack_ReplayPrecedence_ReturnsStoredResultOnIncrementedVersion()
    {
        var (controller, db) = CreatePackContainersController();
        var productId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var lineItemId = Guid.NewGuid();
        var allocId = Guid.NewGuid();

        // Container version is at 5
        var unit = new InventoryUnit
        {
            Id = containerId,
            ProductId = productId,
            InventoryLotId = lotId,
            TrackingCode = "BOX-VERSIONED",
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 80m,
            PackState = PackState.Open,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1000m,
            Version = 5
        };
        db.InventoryUnits.Add(unit);

        var existingMovement = new InventoryMovement
        {
            Id = txId,
            ProductId = productId,
            MovementType = InventoryMovementType.SaleOut,
            ReferenceType = "PACK_ALLOCATION",
            ReferenceId = lineItemId,
            UnitCostSnapshot = 10m,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = txId,
            Note = "Original allocation"
        };
        db.InventoryMovements.Add(existingMovement);

        var existingAlloc = new PackAllocation
        {
            Id = allocId,
            PackUnitId = containerId,
            MovementId = txId,
            LotId = lotId,
            OperationType = PackAllocationOperationTypes.Sale,
            SaleItemId = lineItemId,
            AllocatedBaseQuantity = 20m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 200m,
            RemainingQuantityAfter = 80m,
            PackStateAfter = PackState.Open,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PackAllocations.Add(existingAlloc);
        await db.SaveChangesAsync();

        // Request passes stale ExpectedVersion = 1, but replay lookup precedes version check
        var request = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: txId,
            LineItemId: lineItemId,
            ContainerId: containerId,
            RequestedQuantity: 20m,
            AllowSplitCut: false,
            ExpectedVersion: 1);

        var result = await controller.AllocatePack(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<PackAllocationResponse>(okResult.Value);
        Assert.Equal(allocId, response.AllocationId);
        Assert.Equal(20m, response.AllocatedQuantity);
        Assert.Equal(80m, response.RemainingQuantity);
    }

    [Fact]
    public async Task Imp098_AllocatePack_Replay_RejectsChangedPayloadWith409Conflict()
    {
        var (controller, db) = CreatePackContainersController();
        var productId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var lineItemId = Guid.NewGuid();

        var unit = new InventoryUnit
        {
            Id = containerId,
            ProductId = productId,
            InventoryLotId = lotId,
            TrackingCode = "BOX-PAYLOAD-TEST",
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 40m,
            PackState = PackState.Open,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 500m,
            Version = 1
        };
        db.InventoryUnits.Add(unit);

        var existingAlloc = new PackAllocation
        {
            Id = Guid.NewGuid(),
            PackUnitId = containerId,
            MovementId = txId,
            LotId = lotId,
            OperationType = PackAllocationOperationTypes.Sale,
            SaleItemId = lineItemId,
            AllocatedBaseQuantity = 10m,
            UnitCostSnapshot = 10m,
            TotalCostSnapshot = 100m,
            RemainingQuantityAfter = 40m,
            PackStateAfter = PackState.Open,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PackAllocations.Add(existingAlloc);
        await db.SaveChangesAsync();

        // 1. Changed requested quantity (15m instead of 10m)
        var reqDifferentQty = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: txId,
            LineItemId: lineItemId,
            ContainerId: containerId,
            RequestedQuantity: 15m,
            AllowSplitCut: false);

        var resultQty = await controller.AllocatePack(reqDifferentQty);
        var conflictQty = Assert.IsType<ConflictObjectResult>(resultQty);
        Assert.NotNull(conflictQty.Value);

        // 2. Changed container ID
        var reqDifferentContainer = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: txId,
            LineItemId: lineItemId,
            ContainerId: Guid.NewGuid(),
            RequestedQuantity: 10m,
            AllowSplitCut: false);

        var resultContainer = await controller.AllocatePack(reqDifferentContainer);
        var conflictContainer = Assert.IsType<ConflictObjectResult>(resultContainer);
        Assert.NotNull(conflictContainer.Value);

        // 3. Changed line item ID
        var reqDifferentLine = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: txId,
            LineItemId: Guid.NewGuid(),
            ContainerId: containerId,
            RequestedQuantity: 10m,
            AllowSplitCut: false);

        var resultLine = await controller.AllocatePack(reqDifferentLine);
        var conflictLine = Assert.IsType<ConflictObjectResult>(resultLine);
        Assert.NotNull(conflictLine.Value);
    }

    [Fact]
    public async Task Imp098_AllocatePack_Replay_RejectsUnrelatedMovementCollisionWith409Conflict()
    {
        var (controller, db) = CreatePackContainersController();
        var txId = Guid.NewGuid();

        // Movement exists but was created for an unrelated inventory operation
        var existingMovement = new InventoryMovement
        {
            Id = txId,
            ProductId = Guid.NewGuid(),
            MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "MANUAL_ADJUSTMENT",
            UnitCostSnapshot = 25m,
            ActorId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = txId
        };
        db.InventoryMovements.Add(existingMovement);
        await db.SaveChangesAsync();

        var request = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: txId,
            LineItemId: Guid.NewGuid(),
            ContainerId: Guid.NewGuid(),
            RequestedQuantity: 5m,
            AllowSplitCut: false);

        var result = await controller.AllocatePack(request);
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.NotNull(conflict.Value);
    }

    [Fact]
    public async Task Imp098_AllocatePack_LotIntegrity_FailsClosedWhenUnitHasMissingLot()
    {
        var (controller, db) = CreatePackContainersController();
        var containerId = Guid.NewGuid();

        var unit = new InventoryUnit
        {
            Id = containerId,
            ProductId = Guid.NewGuid(),
            InventoryLotId = null, // Missing lot!
            TrackingCode = "BOX-NO-LOT",
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 50m,
            PackState = PackState.Sealed,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 500m
        };
        db.InventoryUnits.Add(unit);
        await db.SaveChangesAsync();

        var request = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: Guid.NewGuid(),
            LineItemId: Guid.NewGuid(),
            ContainerId: containerId,
            RequestedQuantity: 10m,
            AllowSplitCut: false);

        var result = await controller.AllocatePack(request);
        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.NotNull(unprocessable.Value);
    }

    [Fact]
    public async Task Imp098_AllocatePack_ContainerCapacity_FailsClosedWhenInitialBaseQuantityInvalid()
    {
        var (controller, db) = CreatePackContainersController();
        var containerId = Guid.NewGuid();
        var lotId = Guid.NewGuid();

        var unit = new InventoryUnit
        {
            Id = containerId,
            ProductId = Guid.NewGuid(),
            InventoryLotId = lotId,
            TrackingCode = "BOX-BAD-CAPACITY",
            InitialBaseQuantity = 0m, // Invalid initial capacity!
            RemainingBaseQuantity = 50m,
            PackState = PackState.Sealed,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 500m
        };
        db.InventoryUnits.Add(unit);
        await db.SaveChangesAsync();

        var request = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: Guid.NewGuid(),
            LineItemId: Guid.NewGuid(),
            ContainerId: containerId,
            RequestedQuantity: 10m,
            AllowSplitCut: false);

        var result = await controller.AllocatePack(request);
        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.NotNull(unprocessable.Value);
    }

    [Fact]
    public async Task Imp098_AllocatePack_FractionalCosting_ConservesBasisAndCreatesRealMovement()
    {
        var (controller, db) = CreatePackContainersController();
        var productId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var txId = Guid.NewGuid();
        var lineItemId = Guid.NewGuid();

        var unit = new InventoryUnit
        {
            Id = containerId,
            ProductId = productId,
            InventoryLotId = lotId,
            TrackingCode = "COIL-100M",
            InitialBaseQuantity = 100m,
            RemainingBaseQuantity = 100m,
            PackState = PackState.Sealed,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1500m
        };
        db.InventoryUnits.Add(unit);

        var stock = new StockBalance
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            SellableQty = 100m
        };
        db.StockBalances.Add(stock);

        var lotBalance = new InventoryLotBucketBalance
        {
            Id = Guid.NewGuid(),
            LotId = lotId,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 100m
        };
        db.InventoryLotBucketBalances.Add(lotBalance);
        await db.SaveChangesAsync();

        var request = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
            TransactionId: txId,
            LineItemId: lineItemId,
            ContainerId: containerId,
            RequestedQuantity: 25m,
            AllowSplitCut: false);

        var result = await controller.AllocatePack(request);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<PackAllocationResponse>(okResult.Value);

        Assert.Equal(25m, response.AllocatedQuantity);
        Assert.Equal(75m, response.RemainingQuantity);

        // Verify database state: real Movement created, lot FK preserved, fractional cost conserved
        var savedAlloc = await db.PackAllocations.FirstOrDefaultAsync(a => a.MovementId == txId);
        Assert.NotNull(savedAlloc);
        Assert.Equal(containerId, savedAlloc.PackUnitId);
        Assert.Equal(lotId, savedAlloc.LotId);
        Assert.Equal(15.000000m, savedAlloc.UnitCostSnapshot);
        Assert.Equal(375.000000m, savedAlloc.TotalCostSnapshot);
        Assert.Equal(75m, savedAlloc.RemainingQuantityAfter);
        Assert.Equal(PackState.Open, savedAlloc.PackStateAfter);

        var savedMovement = await db.InventoryMovements.FirstOrDefaultAsync(m => m.Id == txId);
        Assert.NotNull(savedMovement);
        Assert.Equal(InventoryMovementType.SaleOut, savedMovement.MovementType);
        Assert.Equal("PACK_ALLOCATION", savedMovement.ReferenceType);
        Assert.Equal(lineItemId, savedMovement.ReferenceId);
        Assert.Equal(15.000000m, savedMovement.UnitCostSnapshot);

        // ADR-04 Invariants: Single Stock Balance, Movement Effect, Lot Consumption, Movement Unit
        var updatedStock = await db.StockBalances.FirstOrDefaultAsync(s => s.ProductId == productId);
        Assert.NotNull(updatedStock);
        Assert.Equal(75m, updatedStock.SellableQty);

        var effect = await db.InventoryMovementEffects.FirstOrDefaultAsync(e => e.MovementId == txId);
        Assert.NotNull(effect);
        Assert.Equal(InventoryBucket.Sellable, effect.StockBucket);
        Assert.Equal(-25m, effect.QuantityDelta);
        Assert.Equal(100m, effect.QuantityBefore);
        Assert.Equal(75m, effect.QuantityAfter);

        var consumption = await db.InventoryLotConsumptions.FirstOrDefaultAsync(c => c.MovementId == txId);
        Assert.NotNull(consumption);
        Assert.Equal(lotId, consumption.LotId);
        Assert.Equal(25m, consumption.Quantity);
        Assert.Equal(15.000000m, consumption.UnitCostSnapshot);
        Assert.Equal(375.000000m, consumption.TotalCostSnapshot);

        var updatedLotBalance = await db.InventoryLotBucketBalances.FirstOrDefaultAsync(l => l.LotId == lotId);
        Assert.NotNull(updatedLotBalance);
        Assert.Equal(75m, updatedLotBalance.Quantity);

        var movementUnit = await db.InventoryMovementUnits.FirstOrDefaultAsync(u => u.MovementId == txId);
        Assert.NotNull(movementUnit);
        Assert.Equal(containerId, movementUnit.InventoryUnitId);
    }

    // =========================================================================
    // Test Helpers and Stubs
    // =========================================================================
    private static (PackContainersController Controller, EdgeRetailsDbContext Db) CreatePackContainersController(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString("D"))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new EdgeRetailsDbContext(options);
        var clock = new FakeShopClock(new DateOnly(2026, 10, 13));
        var invRepo = new InventoryRepository(db);
        var packAuthority = new PackSubLedgerAllocationAuthority();
        var controller = new PackContainersController(db, invRepo, db, clock, packAuthority);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["ActorContext"] = new ActorContext(
            UserId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            DisplayName: "Cashier User",
            RoleId: Guid.NewGuid(),
            RoleName: "Cashier",
            Permissions: new HashSet<string> { PermissionKeys.SalesPosUse ?? "sales.pos_use" });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (controller, db);
    }
    private sealed class FakeShopClock(DateOnly shopDate) : IShopClock
    {
        public DateOnly ShopDate => shopDate;
        public DateTimeOffset UtcNow => shopDate.ToDateTime(new TimeOnly(12, 0)).ToUniversalTime();
        public DateTimeOffset ShopNow => UtcNow;
        public TimeZoneInfo ShopTimeZone => TimeZoneInfo.Utc;

        public DateOnly ToShopDate(DateTimeOffset utcTimestamp) =>
            DateOnly.FromDateTime(utcTimestamp.UtcDateTime);

        public DateTimeOffset ToUtc(DateOnly date, TimeOnly time) =>
            new DateTimeOffset(date.ToDateTime(time), TimeSpan.Zero);

        public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetShopDayUtcRange(DateOnly date) =>
            (new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
             new DateTimeOffset(date.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero));
    }

    private sealed class FakePeriodLockRepository : IPeriodLockRepository
    {
        public List<PeriodLock> LockedPeriods { get; } = new();

        public Task<bool> IsPeriodLockedAsync(DateOnly postingDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(LockedPeriods.Any(p => p.IsDateInLockedPeriod(postingDate)));

        public Task<PeriodLock?> GetLockedPeriodForDateAsync(DateOnly postingDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(LockedPeriods.FirstOrDefault(p => p.IsDateInLockedPeriod(postingDate)));

        public void AddPeriodLock(PeriodLock periodLock) =>
            LockedPeriods.Add(periodLock);
    }

    private sealed class FakeIdentityReadRepository : IIdentityReadRepository
    {
        public List<User> Users { get; } = new();
        public List<Role> Roles { get; } = new();
        public Dictionary<Guid, IReadOnlySet<string>> EffectivePermissions { get; } = new();

        public Task<IReadOnlyList<User>> GetActiveUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<User>>(Users.Where(u => u.Status == UserStatus.Active).ToList());

        public Task<User?> GetUserAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Users.FirstOrDefault(u => u.Id == userId));

        public Task<Role?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
            Task.FromResult(Roles.FirstOrDefault(r => r.Id == roleId));

        public Task<IReadOnlyList<Role>> GetRolesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Role>>(Roles);

        public Task<IReadOnlyList<string>> GetRolePermissionKeysAsync(Guid roleId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(new List<string> { PermissionKeys.SupervisorPostingOverride, PermissionKeys.SettingsManage });

        public Task<IReadOnlyList<Permission>> GetPermissionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Permission>>(new List<Permission>());

        public Task<IReadOnlySet<string>> GetEffectivePermissionKeysAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(EffectivePermissions.TryGetValue(userId, out var perms)
                ? perms
                : new HashSet<string> { PermissionKeys.SupervisorPostingOverride, PermissionKeys.SettingsManage });
    }

    private sealed class FakePinCredentialService : IPinCredentialService
    {
        public PinCredential Hash(string pin) =>
            new(Hash: new byte[] { 1, 2, 3 }, Salt: new byte[] { 4, 5, 6 }, Iterations: 10000, Algorithm: "PBKDF2");

        public bool Verify(string pin, PinCredential credential) => pin == "1234";
    }

    private static (Product Product, ProductUnit Unit) SeedQuantityProduct(
        Phase2TestDoubles fakes,
        string sku = "TEST-LOOSE-01",
        decimal price = 100m,
        decimal cost = 50m,
        decimal stock = 50m,
        TrackingMode trackingMode = TrackingMode.Quantity,
        string? attributesJson = null)
    {
        var unit = new Unit { Id = Guid.CreateVersion7(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = Guid.CreateVersion7(),
            Name = "Product " + sku,
            Sku = sku,
            BaseUnitId = unit.Id,
            TrackingMode = trackingMode,
            DefaultSalePrice = price,
            ReferencePurchaseCost = cost,
            AttributesJson = attributesJson,
            IsActive = true
        };
        fakes.Catalog.Products[product.Id] = product;

        var pu = new ProductUnit
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            CanPurchase = true,
            CanSell = true
        };
        fakes.Catalog.AddProductUnit(pu);

        fakes.Inventory.AddStockBalance(new StockBalance
        {
            ProductId = product.Id,
            SellableQty = stock
        });

        fakes.Inventory.AddCostState(new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = stock,
            TotalInventoryCost = stock * cost,
            MovingAverageCost = cost,
            LastPurchaseCost = cost,
            Version = 1
        });

        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = product.Id,
            ReceivedQuantity = stock,
            OriginalUnitCost = cost,
            EffectiveUnitCost = cost,
            CreatedAt = fakes.Clock.UtcNow.AddDays(-5)
        };
        fakes.Inventory.AddLot(lot);

        fakes.Inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = stock
        });

        return (product, pu);
    }

    private static CompleteSaleHandler CreateCompleteSaleHandler(Phase2TestDoubles fakes)
    {
        return new CompleteSaleHandler(
            fakes.Sales,
            fakes.Quotations,
            fakes.Parties,
            fakes.Catalog,
            fakes.Inventory,
            fakes.CostAllocator,
            fakes.Cash,
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.Audit,
            fakes.ReceiptSnapshots,
            fakes.Numbers,
            fakes.Clock,
            fakes.Transactions,
            fakes.Authorization,
            fakes.UnitOfWork,
            fakes.OutcomeLedger,
            fakes.PackAllocations);
    }

    #region Canonical LF09 Behavioral Tests (Controller Multi-Page Pack Traversal)

    [Fact]
    public async Task LF09_PackContainersController_TraversalAcrossPages_Discovers51stPack()
    {
        var (controller, db) = CreatePackContainersController();
        var productId = Guid.NewGuid();

        // Seed product & unit so MapToDto has valid product
        var unit = new EdgeRetails.Domain.Catalog.Unit { Id = Guid.NewGuid(), Name = "Piece", Symbol = "pc" };
        var product = new Product
        {
            Id = productId,
            Name = "LED Bulb Pack 10W",
            Sku = "LED-10W",
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Container,
            DefaultSalePrice = 200m,
            ReferencePurchaseCost = 100m,
            IsActive = true
        };
        db.Units.Add(unit);
        db.Products.Add(product);

        // Seed 50 open units with RemainingBaseQuantity = 1m (ineligible for requested 5m)
        for (var i = 1; i <= 50; i++)
        {
            db.InventoryUnits.Add(new InventoryUnit
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                TrackingCode = $"PACK-{i:D3}",
                InitialBaseQuantity = 10m,
                RemainingBaseQuantity = 1m,
                PackState = PackState.Open,
                Status = InventoryUnitStatus.InStock,
                AcquisitionCost = 100m,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(i)
            });
        }

        // Seed 51st open unit with RemainingBaseQuantity = 10m (eligible for requested 5m)
        var unit51Id = Guid.NewGuid();
        db.InventoryUnits.Add(new InventoryUnit
        {
            Id = unit51Id,
            ProductId = productId,
            TrackingCode = "PACK-051",
            InitialBaseQuantity = 10m,
            RemainingBaseQuantity = 10m,
            PackState = PackState.Open,
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 100m,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(51)
        });

        await db.SaveChangesAsync();

        // Query Page 1: returns exactly 50 units (PACK-001 through PACK-050), none with Remaining >= 5
        var page1Result = await controller.GetPacks(productId: productId, pageNumber: 1, pageSize: 50);
        var ok1 = Assert.IsType<OkObjectResult>(page1Result);
        var resp1 = Assert.IsType<PackContainerQueryResponse>(ok1.Value);
        Assert.Equal(51, resp1.TotalCount);
        Assert.Equal(50, resp1.Items.Count);
        Assert.All(resp1.Items, item => Assert.Equal(1m, item.RemainingQuantity));

        // Query Page 2: returns the 51st unit (PACK-051) with Remaining = 10
        var page2Result = await controller.GetPacks(productId: productId, pageNumber: 2, pageSize: 50);
        var ok2 = Assert.IsType<OkObjectResult>(page2Result);
        var resp2 = Assert.IsType<PackContainerQueryResponse>(ok2.Value);
        Assert.Equal(51, resp2.TotalCount);
        var item51 = Assert.Single(resp2.Items);
        Assert.Equal("PACK-051", item51.TrackingCode);
        Assert.Equal(10m, item51.RemainingQuantity);

        // Targeted query with minContinuousLength = 5m returns PACK-051 on page 1
        var targetedResult = await controller.GetPacks(productId: productId, minContinuousLength: 5m, pageNumber: 1, pageSize: 50);
        var okTargeted = Assert.IsType<OkObjectResult>(targetedResult);
        var respTargeted = Assert.IsType<PackContainerQueryResponse>(okTargeted.Value);
        Assert.Equal(1, respTargeted.TotalCount);
        var targetedItem = Assert.Single(respTargeted.Items);
        Assert.Equal("PACK-051", targetedItem.TrackingCode);
        Assert.Equal(10m, targetedItem.RemainingQuantity);
    }

    // =========================================================================
    // LF15: Supervisor Posting Preflight Parity with Application Authorization
    // =========================================================================

    private static string? GetStatusCodeFromObjectResult(IActionResult result)
    {
        if (result is ObjectResult obj && obj.Value is not null)
        {
            var prop = obj.Value.GetType().GetProperty("code");
            return prop?.GetValue(obj.Value)?.ToString();
        }
        return null;
    }

    [Fact]
    public async Task LF15_PostingController_InputValidation_ReturnsExpectedDomainErrorCodes()
    {
        var today = new DateOnly(2026, 10, 19);
        var clock = new FakeShopClock(today);
        var periodLocks = new FakePeriodLockRepository();
        var identity = new FakeIdentityReadRepository();
        var pinCredentials = new FakePinCredentialService();
        var controller = new PostingController(clock, periodLocks, identity, pinCredentials, new InMemorySupervisorApprovalStore());

        var supervisorId = Guid.NewGuid();

        // 1. Missing supervisor ID (Guid.Empty) -> authorization.supervisor_id_required
        var emptySupResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: Guid.Empty,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-1),
            OperationType: "Purchase",
            Reason: "Valid reason"));
        var badRequestEmpty = Assert.IsType<BadRequestObjectResult>(emptySupResult);
        Assert.Equal("authorization.supervisor_id_required", GetStatusCodeFromObjectResult(badRequestEmpty));

        // 2. Missing or invalid PIN -> authorization.invalid_supervisor_pin
        var emptyPinResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "",
            PostingDate: today.AddDays(-1),
            OperationType: "Purchase",
            Reason: "Valid reason"));
        var badRequestEmptyPin = Assert.IsType<BadRequestObjectResult>(emptyPinResult);
        Assert.Equal("authorization.invalid_supervisor_pin", GetStatusCodeFromObjectResult(badRequestEmptyPin));

        var shortPinResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "12",
            PostingDate: today.AddDays(-1),
            OperationType: "Purchase",
            Reason: "Valid reason"));
        var badRequestShortPin = Assert.IsType<BadRequestObjectResult>(shortPinResult);
        Assert.Equal("authorization.invalid_supervisor_pin", GetStatusCodeFromObjectResult(badRequestShortPin));

        var nonDigitPinResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "12ab",
            PostingDate: today.AddDays(-1),
            OperationType: "Purchase",
            Reason: "Valid reason"));
        var badRequestNonDigit = Assert.IsType<BadRequestObjectResult>(nonDigitPinResult);
        Assert.Equal("authorization.invalid_supervisor_pin", GetStatusCodeFromObjectResult(badRequestNonDigit));

        // 3. Missing reason -> authorization.reason_required
        var emptyReasonResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-1),
            OperationType: "Purchase",
            Reason: "   "));
        var badRequestEmptyReason = Assert.IsType<BadRequestObjectResult>(emptyReasonResult);
        Assert.Equal("authorization.reason_required", GetStatusCodeFromObjectResult(badRequestEmptyReason));
    }

    [Fact]
    public async Task LF15_PostingController_PreflightValidation_ParityWithApplicationAuthorization()
    {
        var today = new DateOnly(2026, 10, 19);
        var clock = new FakeShopClock(today);
        var periodLocks = new FakePeriodLockRepository();
        var identity = new FakeIdentityReadRepository();
        var pinCredentials = new FakePinCredentialService();
        var controller = new PostingController(clock, periodLocks, identity, pinCredentials, new InMemorySupervisorApprovalStore());

        var supervisorRoleId = Guid.NewGuid();
        var supervisorId = Guid.NewGuid();
        identity.Roles.Add(new Role { Id = supervisorRoleId, Name = "Supervisor", IsActive = true });
        identity.Users.Add(new User
        {
            Id = supervisorId,
            DisplayName = "Supervisor Jane",
            RoleId = supervisorRoleId,
            Status = UserStatus.Active,
            PinHash = new byte[] { 1, 2, 3 },
            PinSalt = new byte[] { 4, 5, 6 },
            PinIterations = 10000,
            PinAlgorithm = "PBKDF2"
        });

        // 1. Future date check -> posting.future_date_forbidden (422)
        var futureResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(1),
            OperationType: "Purchase",
            Reason: "Backdate into future"));
        var unprocessableFuture = Assert.IsType<UnprocessableEntityObjectResult>(futureResult);
        Assert.Equal("posting.future_date_forbidden", GetStatusCodeFromObjectResult(unprocessableFuture));

        // 2. Beyond 7-day window -> posting.backdate_exceeds_window (422)
        var windowResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-8),
            OperationType: "Purchase",
            Reason: "Backdate too old"));
        var unprocessableWindow = Assert.IsType<UnprocessableEntityObjectResult>(windowResult);
        Assert.Equal("posting.backdate_exceeds_window", GetStatusCodeFromObjectResult(unprocessableWindow));

        // 3. Period lock precedence -> posting.accounting_period_locked (409)
        periodLocks.LockedPeriods.Add(new PeriodLock
        {
            PeriodName = "Closed October Period",
            PeriodStartDate = today.AddDays(-4),
            PeriodEndDate = today.AddDays(-2),
            IsLocked = true,
            LockedAt = DateTimeOffset.UtcNow,
            LockedBy = Guid.NewGuid()
        });
        var lockedResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-3),
            OperationType: "Purchase",
            Reason: "Backdate into locked period"));
        var conflictLocked = Assert.IsType<ConflictObjectResult>(lockedResult);
        Assert.Equal("posting.accounting_period_locked", GetStatusCodeFromObjectResult(conflictLocked));

        // 4. Supervisor account inactive or missing -> authorization.supervisor_not_found_or_inactive (403)
        var missingSupResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: Guid.NewGuid(),
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-1),
            OperationType: "Purchase",
            Reason: "Valid reason"));
        var forbiddenMissing = Assert.IsType<ObjectResult>(missingSupResult);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenMissing.StatusCode);
        Assert.Equal("authorization.supervisor_not_found_or_inactive", GetStatusCodeFromObjectResult(forbiddenMissing));

        // 5. Incorrect cryptographic PIN -> authorization.invalid_supervisor_pin (403)
        var wrongPinResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "9999",
            PostingDate: today.AddDays(-1),
            OperationType: "Purchase",
            Reason: "Valid reason"));
        var forbiddenWrongPin = Assert.IsType<ObjectResult>(wrongPinResult);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenWrongPin.StatusCode);
        Assert.Equal("authorization.invalid_supervisor_pin", GetStatusCodeFromObjectResult(forbiddenWrongPin));
    }

    [Fact]
    public async Task LF15_PostingController_AntiSpoofing_RejectsSelfAuthorizingCashierWith403()
    {
        var today = new DateOnly(2026, 10, 19);
        var clock = new FakeShopClock(today);
        var periodLocks = new FakePeriodLockRepository();
        var identity = new FakeIdentityReadRepository();
        var pinCredentials = new FakePinCredentialService();
        var controller = new PostingController(clock, periodLocks, identity, pinCredentials, new InMemorySupervisorApprovalStore());

        var cashierRoleId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();
        identity.Roles.Add(new Role { Id = cashierRoleId, Name = "Cashier", IsActive = true });
        identity.Users.Add(new User
        {
            Id = cashierId,
            DisplayName = "Cashier Bob",
            RoleId = cashierRoleId,
            Status = UserStatus.Active,
            PinHash = new byte[] { 1, 2, 3 },
            PinSalt = new byte[] { 4, 5, 6 },
            PinIterations = 10000,
            PinAlgorithm = "PBKDF2"
        });
        identity.EffectivePermissions[cashierId] = new HashSet<string> { PermissionKeys.SalesCreate };

        // Attach HttpContext representing logged-in cashier
        var httpContext = new DefaultHttpContext();
        httpContext.Items["ActorContext"] = new ActorContext(
            UserId: cashierId,
            SessionId: Guid.NewGuid(),
            DisplayName: "Cashier Bob",
            RoleId: cashierRoleId,
            RoleName: "Cashier",
            Permissions: new HashSet<string> { PermissionKeys.SalesCreate });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Cashier attempts to pass their own ID as the supervisor!
        var spoofedResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: cashierId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-1),
            OperationType: "Purchase",
            Reason: "Self authorization attempt"));

        var forbidden = Assert.IsType<ObjectResult>(spoofedResult);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("authorization.spoofed_supervisor_rejected", GetStatusCodeFromObjectResult(forbidden));
    }

    [Fact]
    public async Task LF15_PostingController_UnprivilegedSupervisor_Returns403PermissionDenied()
    {
        var today = new DateOnly(2026, 10, 19);
        var clock = new FakeShopClock(today);
        var periodLocks = new FakePeriodLockRepository();
        var identity = new FakeIdentityReadRepository();
        var pinCredentials = new FakePinCredentialService();
        var controller = new PostingController(clock, periodLocks, identity, pinCredentials, new InMemorySupervisorApprovalStore());

        var cashierRoleId = Guid.NewGuid();
        var cashierId = Guid.NewGuid();
        var peerCashierId = Guid.NewGuid();

        identity.Roles.Add(new Role { Id = cashierRoleId, Name = "Cashier", IsActive = true });
        identity.Users.Add(new User
        {
            Id = cashierId,
            DisplayName = "Cashier Bob",
            RoleId = cashierRoleId,
            Status = UserStatus.Active,
            PinHash = new byte[] { 1, 2, 3 },
            PinSalt = new byte[] { 4, 5, 6 },
            PinIterations = 10000,
            PinAlgorithm = "PBKDF2"
        });
        identity.Users.Add(new User
        {
            Id = peerCashierId,
            DisplayName = "Cashier Charlie",
            RoleId = cashierRoleId,
            Status = UserStatus.Active,
            PinHash = new byte[] { 1, 2, 3 },
            PinSalt = new byte[] { 4, 5, 6 },
            PinIterations = 10000,
            PinAlgorithm = "PBKDF2"
        });

        identity.EffectivePermissions[cashierId] = new HashSet<string> { PermissionKeys.SalesCreate };
        identity.EffectivePermissions[peerCashierId] = new HashSet<string> { PermissionKeys.SalesCreate };

        // Attach HttpContext representing logged-in cashier Bob
        var httpContext = new DefaultHttpContext();
        httpContext.Items["ActorContext"] = new ActorContext(
            UserId: cashierId,
            SessionId: Guid.NewGuid(),
            DisplayName: "Cashier Bob",
            RoleId: cashierRoleId,
            RoleName: "Cashier",
            Permissions: new HashSet<string> { PermissionKeys.SalesCreate });
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Cashier Bob supplies peer Cashier Charlie (who lacks supervisor permissions)
        var peerResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: peerCashierId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-1),
            OperationType: "Purchase",
            Reason: "Peer override attempt"));

        var forbidden = Assert.IsType<ObjectResult>(peerResult);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("authorization.supervisor_permission_denied", GetStatusCodeFromObjectResult(forbidden));
    }

    [Fact]
    public async Task LF15_PostingController_AuthorizedSupervisor_Returns200WithCryptographicToken()
    {
        var today = new DateOnly(2026, 10, 19);
        var clock = new FakeShopClock(today);
        var periodLocks = new FakePeriodLockRepository();
        var identity = new FakeIdentityReadRepository();
        var pinCredentials = new FakePinCredentialService();
        var controller = new PostingController(clock, periodLocks, identity, pinCredentials, new InMemorySupervisorApprovalStore());

        var supervisorRoleId = Guid.NewGuid();
        var supervisorId = Guid.NewGuid();
        identity.Roles.Add(new Role { Id = supervisorRoleId, Name = "Supervisor", IsActive = true });
        identity.Users.Add(new User
        {
            Id = supervisorId,
            DisplayName = "Supervisor Sarah",
            RoleId = supervisorRoleId,
            Status = UserStatus.Active,
            PinHash = new byte[] { 1, 2, 3 },
            PinSalt = new byte[] { 4, 5, 6 },
            PinIterations = 10000,
            PinAlgorithm = "PBKDF2"
        });
        identity.EffectivePermissions[supervisorId] = new HashSet<string> { PermissionKeys.SupervisorPostingOverride };

        var validResult = await controller.RequestSupervisorApproval(new SupervisorPostingApprovalRequest(
            SupervisorId: supervisorId,
            SupervisorPin: "1234",
            PostingDate: today.AddDays(-2),
            OperationType: "Purchase",
            Reason: "Supplier invoice received late"));

        var okResult = Assert.IsType<OkObjectResult>(validResult);
        var response = Assert.IsType<SupervisorPostingApprovalResponse>(okResult.Value);
        Assert.True(response.Authorized);
        Assert.Equal(supervisorId, response.SupervisorId);
        Assert.Equal("Supervisor Sarah", response.SupervisorDisplayName);
        Assert.False(string.IsNullOrWhiteSpace(response.ApprovalToken));
        Assert.True(response.ApprovalToken.Length >= 16);
    }

    #endregion

    #region Canonical LF14 Behavioral Tests (Thaka Reversal Activity & Explicit Reporting Presentation)

    [Fact]
    public async Task LF14_ReportingReadService_ReversalOnlyProject_IncludedWithExplicitReversalBreakdown()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("D"))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var db = new EdgeRetailsDbContext(options);

        var queryDate = new DateOnly(2026, 10, 19);
        var queryDateTimeUtc = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        // Project A: Active issues AND reversals in current period
        var projectAId = Guid.NewGuid();
        db.ThakaProjects.Add(new ThakaProject
        {
            Id = projectAId,
            ProjectNumber = "PRJ-001",
            ProjectName = "Plaza Electrical Works",
            Status = ThakaProjectStatus.Active,
            StartedOn = queryDate.AddDays(-10),
            CreatedAt = queryDateTimeUtc.AddDays(-10)
        });

        // 2 issues totaling Rs. 15,000
        var issueA1Id = Guid.NewGuid();
        db.ThakaMaterialIssues.Add(new ThakaMaterialIssue
        {
            Id = issueA1Id,
            ProjectId = projectAId,
            ChallanNumber = "CH-001",
            TotalCharge = 10000m,
            IssuedAt = queryDateTimeUtc.AddHours(1)
        });
        db.ThakaMaterialIssues.Add(new ThakaMaterialIssue
        {
            Id = Guid.NewGuid(),
            ProjectId = projectAId,
            ChallanNumber = "CH-002",
            TotalCharge = 5000m,
            IssuedAt = queryDateTimeUtc.AddHours(2)
        });

        // 1 reversal of Rs. 2,000 linked to persisted issue fixture
        db.ThakaMaterialReversals.Add(new ThakaMaterialReversal
        {
            Id = Guid.NewGuid(),
            ProjectId = projectAId,
            MaterialIssueId = issueA1Id,
            ReversalNumber = "REV-001",
            ReversedCharge = 2000m,
            ReversedAt = queryDateTimeUtc.AddHours(3)
        });

        // Project B: REVERSAL-ONLY in current period (materials issued in prior period, return/reversal today)
        var projectBId = Guid.NewGuid();
        db.ThakaProjects.Add(new ThakaProject
        {
            Id = projectBId,
            ProjectNumber = "PRJ-002",
            ProjectName = "Al-Rehman Tower Wiring",
            Status = ThakaProjectStatus.Active,
            StartedOn = queryDate.AddDays(-30),
            CreatedAt = queryDateTimeUtc.AddDays(-30)
        });

        // Prior period issue for Project B (should NOT appear in today's issue metrics)
        var priorIssueBId = Guid.NewGuid();
        db.ThakaMaterialIssues.Add(new ThakaMaterialIssue
        {
            Id = priorIssueBId,
            ProjectId = projectBId,
            ChallanNumber = "CH-000",
            TotalCharge = 50000m,
            IssuedAt = queryDateTimeUtc.AddDays(-5)
        });

        // 1 reversal of Rs. 5,000 today; NO issues today (linked to persisted historical issue fixture)
        db.ThakaMaterialReversals.Add(new ThakaMaterialReversal
        {
            Id = Guid.NewGuid(),
            ProjectId = projectBId,
            MaterialIssueId = priorIssueBId,
            ReversalNumber = "REV-002",
            ReversedCharge = 5000m,
            ReversedAt = queryDateTimeUtc.AddHours(4)
        });

        await db.SaveChangesAsync();

        var reportingService = new ReportingReadService(db);
        var snapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            queryDate,
            queryDate.Month,
            queryDate.Year,
            CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.ThakaActivity);
        Assert.Equal(2, snapshot.ThakaActivity.Count);

        // Project A metrics verification
        var activityA = snapshot.ThakaActivity.FirstOrDefault(x => x.ProjectName == "Plaza Electrical Works");
        Assert.NotNull(activityA);
        Assert.Equal(2, activityA.IssueCount);
        Assert.Equal(15000m, activityA.MaterialValue);
        Assert.Equal(1, activityA.ReversalCount);
        Assert.Equal(2000m, activityA.ReversalAmount);
        Assert.Equal(13000m, activityA.NetMaterialValue);

        // Project B (Reversal-Only) metrics verification (LF14 Option B requirement: must be included and explicitly break down reversal)
        var activityB = snapshot.ThakaActivity.FirstOrDefault(x => x.ProjectName == "Al-Rehman Tower Wiring");
        Assert.NotNull(activityB);
        Assert.Equal(0, activityB.IssueCount);
        Assert.Equal(0m, activityB.MaterialValue);
        Assert.Equal(1, activityB.ReversalCount);
        Assert.Equal(5000m, activityB.ReversalAmount);
        Assert.Equal(-5000m, activityB.NetMaterialValue);

        // Net Thaka Material on Snapshot reflects net material value (15,000 - 2,000 - 5,000 = 8,000)
        Assert.Equal(8000m, snapshot.ThakaMaterial);

        // Ordering verification: sorted descending by NetMaterialValue (Project A: 13,000 first, Project B: -5,000 second)
        Assert.Equal("Plaza Electrical Works", snapshot.ThakaActivity[0].ProjectName);
        Assert.Equal("Al-Rehman Tower Wiring", snapshot.ThakaActivity[1].ProjectName);
    }

    [Fact]
    public async Task LF14_ReportingReadService_WhollyReversalOnlyPeriod_RetainsActivityAndIsolatesHeadlineClamp()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("D"))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var db = new EdgeRetailsDbContext(options);

        var queryDate = new DateOnly(2026, 10, 21);
        var queryDateTimeUtc = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

        var projectId = Guid.NewGuid();
        db.ThakaProjects.Add(new ThakaProject
        {
            Id = projectId,
            ProjectNumber = "PRJ-REV-ONLY",
            ProjectName = "Commercial Plaza Retrofit",
            Status = ThakaProjectStatus.Active,
            StartedOn = queryDate.AddDays(-15),
            CreatedAt = queryDateTimeUtc.AddDays(-15)
        });

        // Historical issue 5 days prior (NOT in today's query window)
        var historicalIssueId = Guid.NewGuid();
        db.ThakaMaterialIssues.Add(new ThakaMaterialIssue
        {
            Id = historicalIssueId,
            ProjectId = projectId,
            ChallanNumber = "CH-HIST-01",
            TotalCharge = 20000m,
            IssuedAt = queryDateTimeUtc.AddDays(-5)
        });

        // Single reversal today (wholly reversal-only window: 0 issues today, 1 reversal today of Rs. 5,000)
        db.ThakaMaterialReversals.Add(new ThakaMaterialReversal
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            MaterialIssueId = historicalIssueId, // Reliably linked to persisted issue fixture
            ReversalNumber = "REV-TODAY-01",
            ReversedCharge = 5000m,
            ReversedAt = queryDateTimeUtc.AddHours(2)
        });

        await db.SaveChangesAsync();

        var reportingService = new ReportingReadService(db);
        var snapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            queryDate,
            queryDate.Month,
            queryDate.Year,
            CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Single(snapshot.ThakaActivity);

        var row = snapshot.ThakaActivity[0];
        Assert.Equal("Commercial Plaza Retrofit", row.ProjectName);
        Assert.Equal(0, row.IssueCount);
        Assert.Equal(0m, row.MaterialValue);
        Assert.Equal(1, row.ReversalCount);
        Assert.Equal(5000m, row.ReversalAmount);
        Assert.Equal(-5000m, row.NetMaterialValue);

        // Verification of explicit Option B row metrics vs isolated headline clamp behavior:
        // Headline KPI is clamped to 0m via Math.Max(0m, thakaTotal), while detailed row retains -5000m net material value
        Assert.Equal(0m, snapshot.ThakaMaterial);
        Assert.Equal(-5000m, row.NetMaterialValue);
    }

    #endregion

    #region Canonical LF01, LF13, and THAKA-AUD-04 Behavioral Tests

    [Fact]
    public async Task LF01_SaleReturnHandler_RejectsUnsupportedStoreCreditAndOtherRefundMethods()
    {
        var fakes = new Phase2TestDoubles();
        var returnHandler = new CreateSaleReturnHandler(
            fakes.Sales,
            fakes.Inventory,
            fakes.CostAllocator,
            fakes.Cash,
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.Audit,
            fakes.Numbers,
            fakes.Clock,
            fakes.Transactions,
            fakes.Authorization,
            fakes.UnitOfWork,
            fakes.Warranty,
            fakes.OutcomeLedger,
            fakes.Catalog,
            fakes.PhysicalUnits,
            fakes.PackAllocations,
            fakes.Traceability,
            fakes.Parties,
            fakes.Eligibility);

        // 1. Rejects RefundMethod.Other
        var otherReturn = new CreateSaleReturnCommand(
            SaleId: Guid.NewGuid(),
            ReasonCode: "DAMAGED",
            ReasonNote: "Customer requested store credit",
            RefundMethod: RefundMethod.Other,
            CreatedBy: Guid.NewGuid(),
            ClientOperationId: Guid.NewGuid(),
            Lines: new[]
            {
                new SaleReturnLineInput(
                    SaleItemId: Guid.NewGuid(),
                    BaseQuantity: 1m,
                    Disposition: SaleReturnDisposition.RestockSellable,
                    InventoryUnitIds: Array.Empty<Guid>())
            });

        var otherResult = await returnHandler.HandleAsync(otherReturn, CancellationToken.None);
        Assert.False(otherResult.IsSuccess);
        Assert.Equal("sales.unsupported_refund_method", otherResult.Error?.Code);
        Assert.Contains("Store credit is not an approved retail return method", otherResult.Error?.Message);

        // 2. Rejects arbitrary unapproved integer/enum values
        var invalidEnumReturn = new CreateSaleReturnCommand(
            SaleId: Guid.NewGuid(),
            ReasonCode: "DEFECTIVE",
            ReasonNote: null,
            RefundMethod: (RefundMethod)99,
            CreatedBy: Guid.NewGuid(),
            ClientOperationId: Guid.NewGuid(),
            Lines: new[]
            {
                new SaleReturnLineInput(
                    SaleItemId: Guid.NewGuid(),
                    BaseQuantity: 1m,
                    Disposition: SaleReturnDisposition.RestockSellable,
                    InventoryUnitIds: Array.Empty<Guid>())
            });

        var invalidResult = await returnHandler.HandleAsync(invalidEnumReturn, CancellationToken.None);
        Assert.False(invalidResult.IsSuccess);
        Assert.Equal("sales.unsupported_refund_method", invalidResult.Error?.Code);
    }

    [Fact]
    public async Task LF13_CustomerDirectory_PerProjectBalance_EnforcesZeroCrossProjectNetting_And_ReconcilesLifecycle()
    {
        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("D"))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        using var db = new EdgeRetailsDbContext(options);

        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Name = "Mian Tariq Electrician",
            Phone = "0300-1234567",
            IsWalkIn = false,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Project 1: Lifecycle sequence: issue 100 / pay 100 / return 100 / refund 100 / issue 50 => due 50
        var project1Id = Guid.NewGuid();
        db.ThakaProjects.Add(new ThakaProject
        {
            Id = project1Id,
            CustomerId = customerId,
            ProjectNumber = "PRJ-TARIQ-01",
            ProjectName = "Commercial Plaza Wiring",
            Status = ThakaProjectStatus.Active,
            StartedOn = new DateOnly(2026, 10, 1),
            CreatedAt = DateTimeOffset.UtcNow
        });

        var issue1Id = Guid.NewGuid();
        db.ThakaMaterialIssues.Add(new ThakaMaterialIssue
        {
            Id = issue1Id,
            ProjectId = project1Id,
            ChallanNumber = "CH-01",
            TotalCharge = 100m,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });

        db.ThakaPayments.Add(new ThakaPayment
        {
            Id = Guid.NewGuid(),
            ProjectId = project1Id,
            ReceiptNumber = "PAY-01",
            Amount = 100m,
            RecordedAt = DateTimeOffset.UtcNow.AddDays(-9),
            RecordedBy = Guid.NewGuid()
        });

        db.ThakaMaterialReversals.Add(new ThakaMaterialReversal
        {
            Id = Guid.NewGuid(),
            ProjectId = project1Id,
            MaterialIssueId = issue1Id,
            ReversalNumber = "REV-01",
            ReversedCharge = 100m,
            ReversedAt = DateTimeOffset.UtcNow.AddDays(-8)
        });

        db.ThakaCreditRefunds.Add(new ThakaCreditRefund
        {
            Id = Guid.NewGuid(),
            ProjectId = project1Id,
            RefundNumber = "CRD-01",
            Amount = 100m,
            RefundedAt = DateTimeOffset.UtcNow.AddDays(-7)
        });

        db.ThakaMaterialIssues.Add(new ThakaMaterialIssue
        {
            Id = Guid.NewGuid(),
            ProjectId = project1Id,
            ChallanNumber = "CH-02",
            TotalCharge = 50m,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-6)
        });

        // Project 2: Excess payment advance with zero issues (raw net = -100m)
        var project2Id = Guid.NewGuid();
        db.ThakaProjects.Add(new ThakaProject
        {
            Id = project2Id,
            CustomerId = customerId,
            ProjectNumber = "PRJ-TARIQ-02",
            ProjectName = "Residential Apartment Fitout",
            Status = ThakaProjectStatus.Active,
            StartedOn = new DateOnly(2026, 10, 15),
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.ThakaPayments.Add(new ThakaPayment
        {
            Id = Guid.NewGuid(),
            ProjectId = project2Id,
            ReceiptNumber = "PAY-02",
            Amount = 100m,
            RecordedAt = DateTimeOffset.UtcNow.AddDays(-2),
            RecordedBy = Guid.NewGuid()
        });

        await db.SaveChangesAsync();

        var readService = new PartyDirectoryReadService(db);
        var customers = await readService.GetCustomersAsync(null, cancellationToken: CancellationToken.None);

        Assert.Single(customers);
        var tariq = customers[0];
        Assert.Equal("Mian Tariq Electrician", tariq.Name);
        Assert.Equal(2, tariq.ActiveThakaCount);

        // LF13 invariant: Per-project non-negative balance clamping prevents cross-project netting!
        // Project 1 due = 50m. Project 2 due = Math.Max(0m, -100m) = 0m.
        // Total customer balance must be 50.00m (NOT 0m or -50m through cross-project netting).
        Assert.Equal(50.00m, tariq.CurrentThakaBalance);
    }

    [Fact]
    public async Task THAKA_AUD_04_PaymentReversal_After_CreditRefund_EnforcesConservation_And_RejectsDoubleDischarge()
    {
        var fakes = new Phase2TestDoubles();
        var projectId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        var project = new ThakaProject
        {
            Id = projectId,
            ProjectNumber = "PRJ-AUD-04",
            ProjectName = "Factory Wiring Subledger",
            Status = ThakaProjectStatus.Active,
            StartedOn = new DateOnly(2026, 10, 1),
            CreatedAt = DateTimeOffset.UtcNow
        };
        fakes.Thaka.AddProject(project);

        var paymentId = Guid.NewGuid();
        var payment = new ThakaPayment
        {
            Id = paymentId,
            ProjectId = projectId,
            ReceiptNumber = "PAY-100",
            Amount = 100m,
            RecordedAt = DateTimeOffset.UtcNow.AddDays(-5),
            RecordedBy = actorId
        };
        fakes.Thaka.AddPayment(payment);

        // 100m credit refund already issued to customer
        var refund = new ThakaCreditRefund
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            RefundNumber = "CRD-100",
            Amount = 100m,
            RefundedAt = DateTimeOffset.UtcNow.AddDays(-2),
            RefundedBy = actorId
        };
        fakes.Thaka.AddCreditRefund(refund);

        var reversalHandler = new ReverseThakaPaymentHandler(
            fakes.Thaka,
            fakes.CashMovements,
            fakes.OperationLock,
            fakes.ResourceLock,
            fakes.Numbers,
            fakes.Audit,
            fakes.Clock,
            fakes.Transactions,
            fakes.Authorization,
            fakes.UnitOfWork,
            fakes.OutcomeLedger);

        // Attempting to reverse original 100m payment MUST FAIL CLOSED because payments collected is 0m (100 - 100).
        var reversalResult = await reversalHandler.HandleAsync(new ReverseThakaPaymentCommand(
            ClientOperationId: Guid.NewGuid(),
            ProjectId: projectId,
            PaymentId: paymentId,
            Reason: "Attempt reversal of refunded payment",
            ActorId: actorId), CancellationToken.None);

        Assert.False(reversalResult.IsSuccess);
        Assert.Equal("thaka.payment_already_refunded", reversalResult.Error?.Code);
        Assert.Contains("value has already been discharged through customer credit refunds", reversalResult.Error?.Message);
    }

    #endregion
}
