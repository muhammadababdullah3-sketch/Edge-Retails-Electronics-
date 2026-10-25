using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EdgeRetails.Application.Contracts.Packs;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Desktop;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Desktop.Views;
using EdgeRetails.Desktop.Views.Dialogs;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Posting;
using Xunit;

namespace EdgeRetails.Desktop.PerformanceTests;

/// <summary>
/// Desktop POS client cutover test suite for Phase 11 Wave 3: Server API & Desktop Cutover.
/// Covers tasks P11-IMP-098, P11-IMP-101, P11-IMP-102, P11-IMP-103 (modal), P11-IMP-104 (tree/bounds), and P11-IMP-105 (POS fast search).
/// </summary>
public sealed class Phase11Wave3DesktopCutoverTests
{
    // =========================================================================
    // P11-IMP-098: Desktop client HTTP cutover & Protocol Version Headers
    // =========================================================================
    [Fact]
    public async Task Imp098_DesktopApiClient_SetsRequiredProtocolAndClientVersionHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new DelegatingHandlerStub(req =>
        {
            capturedRequest = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}")
            };
            return Task.FromResult(response);
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") };
        var apiClient = new DesktopApiClient(httpClient);
        var terminalId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        apiClient.SetTerminalContext(terminalId, "secret_token_123");
        apiClient.SetSession(sessionId);

        await apiClient.GetAsync<object>("/api/health");

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains("X-Protocol-Version"));
        Assert.Equal(TerminalProtocol.CurrentProtocolVersion, capturedRequest.Headers.GetValues("X-Protocol-Version").First());

        Assert.True(capturedRequest.Headers.Contains("X-Client-Version"));
        Assert.Equal("1.0.0", capturedRequest.Headers.GetValues("X-Client-Version").First());

        Assert.True(capturedRequest.Headers.Contains("X-Terminal-Id"));
        Assert.Equal(terminalId.ToString("D"), capturedRequest.Headers.GetValues("X-Terminal-Id").First());

        Assert.True(capturedRequest.Headers.Contains("X-Terminal-Secret"));
        Assert.Equal("secret_token_123", capturedRequest.Headers.GetValues("X-Terminal-Secret").First());

        Assert.True(capturedRequest.Headers.Contains("X-Session-Id"));
        Assert.Equal(sessionId.ToString("D"), capturedRequest.Headers.GetValues("X-Session-Id").First());
    }

    // =========================================================================
    // P11-IMP-101: Pack Picker UX in Desktop POS (FIFO, Auto-Pick, Stale Capacity)
    // =========================================================================
    [Fact]
    public void Imp101_PackPickerViewModel_EnforcesFifoSelectionAndStaleCapacityPrompt()
    {
        var apiClient = new DesktopApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);

        var viewModel = new PackPickerViewModel(
            workflowService,
            productId: Guid.NewGuid(),
            productName: "LED Bulb 12W Pack",
            requestedQuantity: 5m,
            unitOfMeasure: "pcs");

        var openPack = new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: "BOX-01",
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 6m,
            ContinuousLength: 6m,
            UnitOfMeasure: "pcs",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        var sealedPack = new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: "BOX-02",
            State: "Sealed",
            InitialQuantity: 10m,
            RemainingQuantity: 10m,
            ContinuousLength: 10m,
            UnitOfMeasure: "pcs",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        viewModel.Containers.Add(openPack);
        viewModel.Containers.Add(sealedPack);

        // Auto-select open pack
        viewModel.SelectContainer(openPack);
        Assert.Equal(openPack, viewModel.SelectedContainer);
        Assert.Null(viewModel.StaleCapacityPrompt);
        Assert.True(viewModel.CanConfirm);

        // Stale capacity alert when modified
        viewModel.PromptStaleCapacity("BOX-01", 3m);
        Assert.Null(viewModel.SelectedContainer);
        Assert.NotNull(viewModel.StaleCapacityPrompt);
        Assert.Contains("stale capacity", viewModel.StaleCapacityPrompt);
        Assert.False(viewModel.CanConfirm);
    }

    // =========================================================================
    // P11-IMP-102: Cable Cutting and Continuous Measurement UX
    // =========================================================================
    [Fact]
    public void Imp102_CableCutViewModel_PrioritizesContinuousCoilsAndRequiresSplitCutConfirmation()
    {
        var apiClient = new DesktopApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);

        var viewModel = new CableCutViewModel(
            workflowService,
            productId: Guid.NewGuid(),
            productName: "Copper Flex Cable 2.5mm",
            requestedCutLength: 20m,
            unitOfMeasure: "m");

        var shortCoil = new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: "COIL-SHORT",
            State: "Open",
            InitialQuantity: 30m,
            RemainingQuantity: 15m,
            ContinuousLength: 15m,
            UnitOfMeasure: "m",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        viewModel.SelectedCoil = shortCoil;

        // Coil continuous length (15m) < requested (20m): Cannot confirm without split-cut checkbox
        Assert.False(viewModel.CanConfirm);
        Assert.NotNull(viewModel.SplitCutNotice);
        Assert.Contains("Split cut checkbox confirmation is required", viewModel.SplitCutNotice);

        // Explicit split-cut confirmation enabled
        viewModel.AllowSplitCuts = true;
        Assert.True(viewModel.CanConfirm);
        Assert.Contains("Segmented split cut confirmed", viewModel.SplitCutNotice);

        // Remnant warning when cut leaves < 5m
        var remnantCoil = new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: "COIL-REMNANT",
            State: "Open",
            InitialQuantity: 50m,
            RemainingQuantity: 23m,
            ContinuousLength: 23m,
            UnitOfMeasure: "m",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        viewModel.AllowSplitCuts = false;
        viewModel.SelectedCoil = remnantCoil; // 23m - 20m cut = 3m remnant remaining (< 5m)

        Assert.True(viewModel.CanConfirm);
        Assert.NotNull(viewModel.RemnantWarning);
        Assert.Contains("< 5m", viewModel.RemnantWarning);
    }

    // =========================================================================
    // P11-IMP-103: Supervisor Approval Modal (Backdate & Period Lock Banner)
    // =========================================================================
    [Fact]
    public void Imp103_SupervisorApprovalModal_CalculatesBackdatingAndEnforcesPeriodLockBanner()
    {
        var apiClient = new DesktopApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var shopDate = new DateOnly(2026, 10, 13);
        var postingDate = new DateOnly(2026, 10, 10);

        var viewModel = new SupervisorApprovalModalViewModel(
            apiClient,
            postingDate: postingDate,
            shopDate: shopDate,
            operationType: "Sale");

        Assert.True(viewModel.IsBackdating);
        Assert.Equal(3, viewModel.BackdateDays);

        viewModel.IsPeriodLocked = true;
        viewModel.LockedPeriodBanner = "Accounting period is LOCKED.";

        // When period is locked, submit must be disabled
        Assert.False(viewModel.CanSubmit);
    }

    // =========================================================================
    // P11-IMP-104: Taxonomy V3 Tree Navigation and Typed Attribute Bounds
    // =========================================================================
    [Fact]
    public void Imp104_ProductManagementViewModel_BuildsTaxonomyTreeAndValidatesBounds()
    {
        var viewModel = new ProductManagementViewModel();
        viewModel.InitializeTaxonomyTree();

        // 9 Divisions
        Assert.Equal(9, viewModel.TaxonomyTree.Count);

        // Total classes should match 732
        var totalClasses = viewModel.TaxonomyTree
            .SelectMany(div => div.Children)
            .SelectMany(dept => dept.Children)
            .Count();
        Assert.Equal(732, totalClasses);

        // Deprecated redirect classes have redirect flags
        var deprecatedNode = viewModel.TaxonomyTree
            .SelectMany(div => div.Children)
            .SelectMany(dept => dept.Children)
            .FirstOrDefault(cls => cls.IsDeprecated);
        Assert.NotNull(deprecatedNode);
        Assert.NotNull(deprecatedNode.RedirectTargetCode);

        // Attribute Bounds Validation: NUMBER min/max
        var numberProfile = new AttributeProfile(
            ProfileCode: "PROF-01",
            DepartmentCode: "A01",
            AttributeKey: "core_count",
            AttributeName: "Core count",
            ValueType: "NUMBER",
            UnitSymbol: null,
            EnumSuggestedValues: null,
            NumericMin: "1",
            NumericMax: "10",
            VariantRole: null,
            EffectiveRequiredness: "REQUIRED");

        var (validNum, _) = ProductManagementViewModel.ValidateAttributeValue(numberProfile, "4");
        Assert.True(validNum);

        var (invalidNum, errNum) = ProductManagementViewModel.ValidateAttributeValue(numberProfile, "15");
        Assert.False(invalidNum);
        Assert.Contains("exceeds maximum allowed bound 10", errNum);

        // Attribute Bounds Validation: ENUM allowed values
        var enumProfile = new AttributeProfile(
            ProfileCode: "PROF-02",
            DepartmentCode: "A01",
            AttributeKey: "insulation_type",
            AttributeName: "Insulation type",
            ValueType: "ENUM",
            UnitSymbol: null,
            EnumSuggestedValues: "PVC;XLPE;Rubber",
            NumericMin: null,
            NumericMax: null,
            VariantRole: null,
            EffectiveRequiredness: "REQUIRED");

        var (validEnum, _) = ProductManagementViewModel.ValidateAttributeValue(enumProfile, "PVC");
        Assert.True(validEnum);

        var (invalidEnum, errEnum) = ProductManagementViewModel.ValidateAttributeValue(enumProfile, "UnknownPlastic");
        Assert.False(invalidEnum);
        Assert.Contains("not among allowed choices", errEnum);

        // Attribute Bounds Validation: BOOLEAN
        var boolProfile = new AttributeProfile(
            ProfileCode: "PROF-03",
            DepartmentCode: "A01",
            AttributeKey: "flame_retardant",
            AttributeName: "Flame retardant",
            ValueType: "BOOLEAN",
            UnitSymbol: null,
            EnumSuggestedValues: null,
            NumericMin: null,
            NumericMax: null,
            VariantRole: null,
            EffectiveRequiredness: "OPTIONAL");

        var (validBool, _) = ProductManagementViewModel.ValidateAttributeValue(boolProfile, "true");
        Assert.True(validBool);

        var (invalidBool, errBool) = ProductManagementViewModel.ValidateAttributeValue(boolProfile, "YesMaybe");
        Assert.False(invalidBool);
        Assert.Contains("must be true or false", errBool);
    }

    // =========================================================================
    // P11-IMP-105: Catalog Paged Search, Packaging Facets, and Scanner Keystrokes
    // =========================================================================
    [Fact]
    public void Imp105_PosViewModel_HandlesPackagingFacetsAndPhoneticAliases()
    {
        var viewModel = CreateTestPosViewModel();
        Assert.Contains("PACK", viewModel.PackagingFacets);
        Assert.Contains("COIL", viewModel.PackagingFacets);
        Assert.Contains("BOX", viewModel.PackagingFacets);
        Assert.Contains("REEL", viewModel.PackagingFacets);

        var wireProduct = new PosProductItemViewModel(
            id: Guid.NewGuid().ToString("D"),
            name: "Pakistan Cables 7/029 Wire",
            sku: "PAK-7029",
            brand: "Pakistan Cables",
            category: "Wiring",
            stock: 100m,
            price: 5500m,
            unit: "Coil");

        var breakerProduct = new PosProductItemViewModel(
            id: Guid.NewGuid().ToString("D"),
            name: "Schneider Electric MCB 32A Breaker",
            sku: "SCH-32A",
            brand: "Schneider",
            category: "Switchgear",
            stock: 50m,
            price: 1200m,
            unit: "Box");

        viewModel.AllProducts.Clear();
        viewModel.AllProducts.Add(wireProduct);
        viewModel.AllProducts.Add(breakerProduct);

        // Urdu phonetic alias test: "taar" resolves to wire product
        viewModel.SearchText = "taar";
        Assert.Single(viewModel.FilteredProducts);
        Assert.Equal(wireProduct.Name, viewModel.FilteredProducts[0].Name);

        // Urdu phonetic alias test: "breaker" resolves to breaker product
        viewModel.SearchText = "breaker";
        Assert.Single(viewModel.FilteredProducts);
        Assert.Equal(breakerProduct.Name, viewModel.FilteredProducts[0].Name);

        // Packaging facet filtering: COIL
        viewModel.SearchText = string.Empty;
        viewModel.SelectedPackagingFacet = "COIL";
        Assert.Single(viewModel.FilteredProducts);
        Assert.Equal(wireProduct.Name, viewModel.FilteredProducts[0].Name);

        // Packaging facet filtering: BOX
        viewModel.SelectedPackagingFacet = "BOX";
        Assert.Single(viewModel.FilteredProducts);
        Assert.Equal(breakerProduct.Name, viewModel.FilteredProducts[0].Name);
    }

    [Fact]
    public void Imp105_PosViewModel_ProcessesScannerInputImmediately()
    {
        var viewModel = CreateTestPosViewModel();
        viewModel.ProcessScannerInput("BARCODE-998877");
        Assert.Equal("BARCODE-998877", viewModel.SearchText);
    }

    // =========================================================================
    // MON-W3-004: POS UI Wiring & Command Execution Tests
    // =========================================================================
    [Fact]
    public void MonW3_004_PosViewModel_AddPackContainerToCart_WiresContainerToCartItem()
    {
        var viewModel = CreateTestPosViewModel();
        var product = new PosProductItemViewModel(
            id: Guid.NewGuid().ToString("D"),
            name: "LED Pack 10x",
            sku: "LED-10X",
            brand: "Philips",
            category: "Lighting",
            stock: 50m,
            price: 250m,
            unit: "Pack");

        var container = new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: "BOX-PK-001",
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 7m,
            ContinuousLength: 7m,
            UnitOfMeasure: "pcs",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        viewModel.AddPackContainerToCart(product, container, 3m);

        Assert.Single(viewModel.CartItems);
        var item = viewModel.CartItems[0];
        Assert.Equal(product.Id, item.ProductId);
        Assert.Equal(3m, item.Quantity);
        Assert.True(item.IsPackContainer);
        Assert.NotNull(item.PackContainer);
        Assert.Equal(container.ContainerId, item.PackContainer.ContainerId);
        Assert.Equal("BOX-PK-001", item.ExactIdentityDisplay);
        Assert.Contains(container.ContainerId, item.InventoryUnitIds);
        Assert.Contains("BOX-PK-001", item.UnitPriceCalculationDisplay);
    }

    [Fact]
    public void MonW3_004_PosViewModel_AddCableCutToCart_WiresSegmentedCutsAndLength()
    {
        var viewModel = CreateTestPosViewModel();
        var product = new PosProductItemViewModel(
            id: Guid.NewGuid().ToString("D"),
            name: "Copper Cable 4mm",
            sku: "CAB-4MM",
            brand: "Pakistan Cables",
            category: "Wiring",
            stock: 200m,
            price: 180m,
            unit: "m");

        var coil = new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: "COIL-CU-77",
            State: "Open",
            InitialQuantity: 100m,
            RemainingQuantity: 65m,
            ContinuousLength: 65m,
            UnitOfMeasure: "m",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        viewModel.AddCableCutToCart(product, coil, 25m, allowSplitCuts: true);

        Assert.Single(viewModel.CartItems);
        var item = viewModel.CartItems[0];
        Assert.Equal(product.Id, item.ProductId);
        Assert.Equal(25m, item.Quantity);
        Assert.True(item.AllowSplitCuts);
        Assert.True(item.IsPackContainer);
        Assert.Equal("COIL-CU-77", item.ExactIdentityDisplay);
        Assert.Contains(coil.ContainerId, item.InventoryUnitIds);
    }

    [Fact]
    public void MonW3_004_PosViewModel_OpenPackPickerAndCableCutCommands_ExecuteSuccessfully()
    {
        var viewModel = CreateTestPosViewModel();
        var product = new PosProductItemViewModel(
            id: Guid.NewGuid().ToString("D"),
            name: "General Product",
            sku: "GEN-01",
            brand: "Brand",
            category: "Cat",
            stock: 10m,
            price: 100m,
            unit: "pcs");

        // Commands must be executable
        Assert.True(viewModel.OpenPackPickerCommand.CanExecute(product));
        Assert.True(viewModel.OpenCableCutCommand.CanExecute(product));
    }

    // =========================================================================
    // MON-W3R3-003: Rendered Dialogs, DataTemplates & Caller Wiring
    // =========================================================================
    [Fact]
    public void Imp101_PackPickerDialog_RendersVisualTree_AndBindsControls()
    {
        OnSta(() =>
        {
            var apiClient = new DesktopApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5000") });
            var workflowService = new RemotePosWorkflowService(apiClient);
            var dialogService = new TestDialogService();

            var viewModel = new PackPickerViewModel(
                workflowService,
                productId: Guid.NewGuid(),
                productName: "LED Bulb 12W Pack",
                requestedQuantity: 5m,
                unitOfMeasure: "pcs",
                dialogService: dialogService);

            var openPack = new PackContainerDto(
                ContainerId: Guid.NewGuid(),
                TrackingCode: "BOX-01",
                State: "Open",
                InitialQuantity: 10m,
                RemainingQuantity: 6m,
                ContinuousLength: 6m,
                UnitOfMeasure: "pcs",
                Condition: "Intact",
                ConcurrencyVersion: 1,
                UnitCost: null);

            viewModel.Containers.Add(openPack);

            var dialog = new PackPickerDialog { DataContext = viewModel };
            Layout(dialog, 820, 640);

            // Verify visual controls exist in visual tree
            var staleCapacityAlert = Assert.IsType<TextBlock>(dialog.FindName("StaleCapacityAlert"));
            var confirmButton = Assert.IsType<Button>(dialog.FindName("ConfirmButton"));
            var containersGrid = Assert.IsType<DataGrid>(dialog.FindName("ContainersGrid"));
            var errorMessageAlert = Assert.IsType<TextBlock>(dialog.FindName("ErrorMessageAlert"));

            Assert.NotNull(staleCapacityAlert);
            Assert.NotNull(confirmButton);
            Assert.NotNull(containersGrid);
            Assert.NotNull(errorMessageAlert);

            // Container selection enables confirm button
            viewModel.SelectContainer(openPack);
            Assert.True(viewModel.CanConfirm);
            Assert.True(confirmButton.IsEnabled);

            // Stale capacity updates alert TextBlock and disables confirm button
            viewModel.PromptStaleCapacity("BOX-01", 3m);
            Assert.False(viewModel.CanConfirm);
            Assert.False(confirmButton.IsEnabled);
            Assert.Contains("stale capacity", staleCapacityAlert.Text);

            // Cancel command closes dialog via dialogService
            dialogService.Show(viewModel);
            Assert.True(dialogService.IsOpen);
            Assert.True(viewModel.CancelCommand.CanExecute(null));
            viewModel.CancelCommand.Execute(null);
            Assert.False(dialogService.IsOpen);
        });
    }

    [Fact]
    public void Imp102_CableCutDialog_RendersVisualTree_AndHandlesSplitCutCheckbox()
    {
        OnSta(() =>
        {
            var apiClient = new DesktopApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5000") });
            var workflowService = new RemotePosWorkflowService(apiClient);
            var dialogService = new TestDialogService();

            var viewModel = new CableCutViewModel(
                workflowService,
                productId: Guid.NewGuid(),
                productName: "Copper Flex Cable 2.5mm",
                requestedCutLength: 20m,
                unitOfMeasure: "m",
                dialogService: dialogService);

            var shortCoil = new PackContainerDto(
                ContainerId: Guid.NewGuid(),
                TrackingCode: "COIL-SHORT",
                State: "Open",
                InitialQuantity: 30m,
                RemainingQuantity: 15m,
                ContinuousLength: 15m,
                UnitOfMeasure: "m",
                Condition: "Intact",
                ConcurrencyVersion: 1,
                UnitCost: null);

            var dialog = new CableCutDialog { DataContext = viewModel };
            Layout(dialog, 840, 660);

            var splitCutCheckBox = Assert.IsType<CheckBox>(dialog.FindName("SplitCutCheckBox"));
            var splitCutNoticeAlert = Assert.IsType<TextBlock>(dialog.FindName("SplitCutNoticeAlert"));
            var remnantWarningAlert = Assert.IsType<TextBlock>(dialog.FindName("RemnantWarningAlert"));
            var confirmCutButton = Assert.IsType<Button>(dialog.FindName("ConfirmCutButton"));
            var coilsGrid = Assert.IsType<DataGrid>(dialog.FindName("CoilsGrid"));

            Assert.NotNull(splitCutCheckBox);
            Assert.NotNull(splitCutNoticeAlert);
            Assert.NotNull(remnantWarningAlert);
            Assert.NotNull(confirmCutButton);
            Assert.NotNull(coilsGrid);

            // Short coil requires split cut confirmation
            viewModel.SelectedCoil = shortCoil;
            Assert.False(viewModel.CanConfirm);
            Assert.False(confirmCutButton.IsEnabled);
            Assert.Contains("Split cut checkbox confirmation is required", splitCutNoticeAlert.Text);

            // Checking split cuts enables confirm button
            viewModel.AllowSplitCuts = true;
            Assert.True(viewModel.CanConfirm);
            Assert.True(confirmCutButton.IsEnabled);
            Assert.Contains("Segmented split cut confirmed", splitCutNoticeAlert.Text);

            // Remnant coil (< 5m remaining) displays danger alert
            var remnantCoil = new PackContainerDto(
                ContainerId: Guid.NewGuid(),
                TrackingCode: "COIL-REMNANT",
                State: "Open",
                InitialQuantity: 50m,
                RemainingQuantity: 23m,
                ContinuousLength: 23m,
                UnitOfMeasure: "m",
                Condition: "Intact",
                ConcurrencyVersion: 1,
                UnitCost: null);

            viewModel.AllowSplitCuts = false;
            viewModel.SelectedCoil = remnantCoil;
            Assert.True(viewModel.CanConfirm);
            Assert.True(confirmCutButton.IsEnabled);
            Assert.Contains("< 5m", remnantWarningAlert.Text);

            // Cancel closes dialog via dialogService
            dialogService.Show(viewModel);
            Assert.True(dialogService.IsOpen);
            Assert.True(viewModel.CancelCommand.CanExecute(null));
            viewModel.CancelCommand.Execute(null);
            Assert.False(dialogService.IsOpen);
        });
    }

    [Fact]
    public void Imp103_SupervisorApprovalModalDialog_RendersVisualTree_AndBindsLockedPeriod()
    {
        OnSta(() =>
        {
            var apiClient = new DesktopApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5000") });
            var dialogService = new TestDialogService();
            var shopDate = new DateOnly(2026, 10, 14);
            var postingDate = new DateOnly(2026, 10, 11);

            var viewModel = new SupervisorApprovalModalViewModel(
                apiClient,
                postingDate: postingDate,
                shopDate: shopDate,
                operationType: "Sale",
                dialogService: dialogService);

            var dialog = new SupervisorApprovalModalDialog { DataContext = viewModel };
            Layout(dialog, 520, 520);

            var lockedPeriodBannerBorder = Assert.IsType<Border>(dialog.FindName("LockedPeriodBannerBorder"));
            var lockedPeriodBannerText = Assert.IsType<TextBlock>(dialog.FindName("LockedPeriodBannerText"));
            var supervisorComboBox = Assert.IsType<ComboBox>(dialog.FindName("SupervisorComboBox"));
            var supervisorPinBox = Assert.IsType<PasswordBox>(dialog.FindName("SupervisorPinBox"));
            var reasonBox = Assert.IsType<TextBox>(dialog.FindName("ReasonBox"));
            var submitButton = Assert.IsType<Button>(dialog.FindName("SubmitButton"));

            Assert.NotNull(lockedPeriodBannerBorder);
            Assert.NotNull(lockedPeriodBannerText);
            Assert.NotNull(supervisorComboBox);
            Assert.NotNull(supervisorPinBox);
            Assert.NotNull(reasonBox);
            Assert.NotNull(submitButton);

            Assert.True(viewModel.IsBackdating);
            Assert.Equal(3, viewModel.BackdateDays);

            // MON-W3R3-004: Verify PasswordBox masking and secure secret clearing
            supervisorPinBox.Password = "1234";
            Assert.Equal("1234", viewModel.SupervisorPin);

            // When period is locked, submit button must be disabled and banner visible
            viewModel.IsPeriodLocked = true;
            viewModel.LockedPeriodBanner = "Accounting period 2026-10 is locked.";
            Assert.False(viewModel.CanSubmit);
            Assert.False(submitButton.IsEnabled);
            Assert.Contains("locked", lockedPeriodBannerText.Text);

            // Cancel closes dialog via dialogService and securely clears supervisor PIN
            dialogService.Show(viewModel);
            Assert.True(dialogService.IsOpen);
            viewModel.CancelCommand.Execute(null);
            Assert.False(dialogService.IsOpen);
            Assert.Equal(string.Empty, viewModel.SupervisorPin);
            Assert.Equal(string.Empty, supervisorPinBox.Password);
        });
    }

    [Fact]
    public void Imp101_103_AppXaml_ResolvesDataTemplatesForDialogViewModels()
    {
        OnSta(() =>
        {
            var app = System.Windows.Application.Current ?? new App();
            if (app is App edgeApp)
            {
                edgeApp.InitializeComponent();
            }

            // Verify App.xaml DataTemplate mappings for all 3 cutover dialogs
            var packTemplate = app.FindResource(new DataTemplateKey(typeof(PackPickerViewModel))) as DataTemplate;
            Assert.NotNull(packTemplate);
            var packView = packTemplate.LoadContent();
            Assert.IsType<PackPickerDialog>(packView);

            var cableTemplate = app.FindResource(new DataTemplateKey(typeof(CableCutViewModel))) as DataTemplate;
            Assert.NotNull(cableTemplate);
            var cableView = cableTemplate.LoadContent();
            Assert.IsType<CableCutDialog>(cableView);

            var supervisorTemplate = app.FindResource(new DataTemplateKey(typeof(SupervisorApprovalModalViewModel))) as DataTemplate;
            Assert.NotNull(supervisorTemplate);
            var supervisorView = supervisorTemplate.LoadContent();
            Assert.IsType<SupervisorApprovalModalDialog>(supervisorView);
        });
    }

    [Fact]
    public void MonW3R3_003_PosViewModel_OpenSupervisorApproval_WiresDialogServiceAndCallback()
    {
        var dialogService = new TestDialogService();
        var apiClient = new DesktopApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);
        var viewModel = CreateTestPosViewModel(dialogService: dialogService, workflowService: workflowService);

        var postingDate = new DateOnly(2026, 10, 10);
        SupervisorApprovalResult? capturedResult = null;

        viewModel.OpenSupervisorApproval(
            postingDate,
            operationType: "SaleReturn",
            onAuthorized: res => capturedResult = res);

        Assert.True(dialogService.IsOpen);
        Assert.NotNull(dialogService.Content);
        var modal = Assert.IsType<SupervisorApprovalModalViewModel>(dialogService.Content);

        Assert.Equal(postingDate, modal.PostingDate);
        Assert.Equal("SaleReturn", modal.OperationType);
        Assert.True(modal.IsBackdating);

        // Cancel closes dialog
        modal.CancelCommand.Execute(null);
        Assert.False(dialogService.IsOpen);
        Assert.Null(capturedResult);
    }

    [Fact]
    public void Imp105_PosViewModel_ScannerSimulation_AllocatesTrackingCodeToCart()
    {
        var viewModel = CreateTestPosViewModel();
        var product = new PosProductItemViewModel(
            id: Guid.NewGuid().ToString("D"),
            name: "High Voltage Cable 10mm",
            sku: "HV-10MM",
            brand: "Pakistan Cables",
            category: "Wiring",
            stock: 500m,
            price: 450m,
            unit: "m");

        var coil = new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: "COIL-HV-888",
            State: "Open",
            InitialQuantity: 100m,
            RemainingQuantity: 80m,
            ContinuousLength: 80m,
            UnitOfMeasure: "m",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        // Simulate scanner tracking code allocation
        viewModel.AddCableCutToCart(product, coil, 30m, allowSplitCuts: false);

        Assert.Single(viewModel.CartItems);
        var item = viewModel.CartItems[0];
        Assert.Equal("COIL-HV-888", item.ExactIdentityDisplay);
        Assert.True(item.IsPackContainer);
        Assert.Equal(30m, item.Quantity);
        Assert.Contains(coil.ContainerId, item.InventoryUnitIds);
        Assert.Contains("COIL-HV-888", item.UnitPriceCalculationDisplay);
        Assert.Equal("Added cable cut 30 m from coil COIL-HV-888.", viewModel.ScannerStatusMessage);

        // Scanning same container again flags duplicate
        viewModel.AddCableCutToCart(product, coil, 10m, allowSplitCuts: false);
        Assert.Single(viewModel.CartItems); // Not added twice
        Assert.Contains("already in the cart", viewModel.ScannerStatusMessage);
    }

    // =========================================================================
    // MON-W3R3-005: Scanner Fast-Path & Tracking Mode Routing Tests
    // =========================================================================
    [Fact]
    public async Task Imp101_105_PosViewModel_ScanCommand_DirectContainerTrackingCode_FastPathAllocatesToCart()
    {
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var trackingCode = "BOX-FAST-999";

        var containerDto = new PackContainerDto(
            ContainerId: containerId,
            TrackingCode: trackingCode,
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 8m,
            ContinuousLength: 8m,
            UnitOfMeasure: "pcs",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        var matchDto = new ScannerProductMatchDto(
            Namespace: ScannerResolutionNamespace.TrackingCode,
            ProductId: productId,
            ProductUnitId: productUnitId,
            ProductName: "LED Downlight Pack",
            Sku: "LED-DL-PK",
            Brand: "Philips",
            Category: "Lighting",
            UnitSymbol: "pcs",
            SellableStock: 8m,
            UnitPrice: 350m,
            IsSerialized: false,
            InventoryUnitId: containerId,
            TrackingCode: trackingCode,
            SerialNumber: null,
            Imei1: null,
            Imei2: null,
            UnitStatus: InventoryUnitStatus.InStock,
            IsSellable: true,
            SupplierName: "Alpha Supplies",
            PurchaseNumber: "PO-100",
            SourcePurchaseItemId: Guid.NewGuid(),
            SupplierId: Guid.NewGuid(),
            TrackingMode: TrackingMode.Container);

        var handler = new DelegatingHandlerStub(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/api/sales/scan"))
            {
                var json = JsonSerializer.Serialize(new[] { matchDto });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (path.Contains($"/api/inventory/packs/{trackingCode}"))
            {
                var json = JsonSerializer.Serialize(containerDto);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);
        var dialogService = new TestDialogService();
        var viewModel = CreateTestPosViewModel(dialogService: dialogService, workflowService: workflowService);

        // Enter tracking code and execute ScanCommand
        viewModel.SearchText = trackingCode;
        Assert.True(viewModel.ScanCommand.CanExecute(null));
        viewModel.ScanCommand.Execute(null);

        // Allow async ScanAsync to run
        for (var i = 0; i < 50 && viewModel.CartItems.Count == 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Single(viewModel.CartItems);
        var cartItem = viewModel.CartItems[0];
        Assert.True(cartItem.IsPackContainer);
        Assert.NotNull(cartItem.PackContainer);
        Assert.Equal(containerId, cartItem.PackContainer.ContainerId);
        Assert.Equal(trackingCode, cartItem.ExactIdentityDisplay);
        Assert.Equal(1m, cartItem.Quantity);
        Assert.Contains(containerId, cartItem.InventoryUnitIds);
        Assert.Equal($"Added container {trackingCode} (1 pcs).", viewModel.ScannerStatusMessage);
        Assert.False(dialogService.IsOpen);
    }

    [Fact]
    public async Task Imp101_105_PosViewModel_ScanCommand_ContainerTrackingCode_StaleCapacityPromptAndPicker()
    {
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var trackingCode = "BOX-STALE-000";

        var staleContainerDto = new PackContainerDto(
            ContainerId: containerId,
            TrackingCode: trackingCode,
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 0m, // Stale capacity: empty
            ContinuousLength: 0m,
            UnitOfMeasure: "pcs",
            Condition: "Empty",
            ConcurrencyVersion: 2,
            UnitCost: null);

        var matchDto = new ScannerProductMatchDto(
            Namespace: ScannerResolutionNamespace.TrackingCode,
            ProductId: productId,
            ProductUnitId: productUnitId,
            ProductName: "Switch Socket Pack",
            Sku: "SW-SK-PK",
            Brand: "Schneider",
            Category: "Switchgear",
            UnitSymbol: "pcs",
            SellableStock: 0m,
            UnitPrice: 200m,
            IsSerialized: false,
            InventoryUnitId: containerId,
            TrackingCode: trackingCode,
            SerialNumber: null,
            Imei1: null,
            Imei2: null,
            UnitStatus: InventoryUnitStatus.InStock,
            IsSellable: true,
            SupplierName: "Schneider Pak",
            PurchaseNumber: "PO-101",
            SourcePurchaseItemId: Guid.NewGuid(),
            SupplierId: Guid.NewGuid(),
            TrackingMode: TrackingMode.Container);

        var handler = new DelegatingHandlerStub(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/api/sales/scan"))
            {
                var json = JsonSerializer.Serialize(new[] { matchDto });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (path.Contains($"/api/inventory/packs/{trackingCode}"))
            {
                var json = JsonSerializer.Serialize(staleContainerDto);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (path.Contains("/api/inventory/packs"))
            {
                var response = new PackContainerQueryResponse(0, Array.Empty<PackContainerDto>());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);
        var dialogService = new TestDialogService();
        var viewModel = CreateTestPosViewModel(dialogService: dialogService, workflowService: workflowService);

        viewModel.SearchText = trackingCode;
        viewModel.ScanCommand.Execute(null);

        for (var i = 0; i < 50 && !dialogService.IsOpen; i++)
        {
            await Task.Delay(20);
        }

        // Cart must be empty, stale capacity message displayed, and pack picker dialog opened
        Assert.Empty(viewModel.CartItems);
        Assert.Contains("has no remaining capacity", viewModel.ScannerStatusMessage);
        Assert.True(dialogService.IsOpen);
        Assert.IsType<PackPickerViewModel>(dialogService.Content);
    }

    [Fact]
    public async Task Imp101_105_PosViewModel_ScanCommand_DuplicateContainerTrackingCode_FlagsDuplicate()
    {
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var trackingCode = "BOX-DUP-111";

        var containerDto = new PackContainerDto(
            ContainerId: containerId,
            TrackingCode: trackingCode,
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 5m,
            ContinuousLength: 5m,
            UnitOfMeasure: "pcs",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        var matchDto = new ScannerProductMatchDto(
            Namespace: ScannerResolutionNamespace.TrackingCode,
            ProductId: productId,
            ProductUnitId: productUnitId,
            ProductName: "Relay Module Pack",
            Sku: "RELAY-PK",
            Brand: "Omron",
            Category: "Automation",
            UnitSymbol: "pcs",
            SellableStock: 5m,
            UnitPrice: 500m,
            IsSerialized: false,
            InventoryUnitId: containerId,
            TrackingCode: trackingCode,
            SerialNumber: null,
            Imei1: null,
            Imei2: null,
            UnitStatus: InventoryUnitStatus.InStock,
            IsSellable: true,
            SupplierName: "Omron Industrial",
            PurchaseNumber: "PO-102",
            SourcePurchaseItemId: Guid.NewGuid(),
            SupplierId: Guid.NewGuid(),
            TrackingMode: TrackingMode.Container);

        var handler = new DelegatingHandlerStub(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/api/sales/scan"))
            {
                var json = JsonSerializer.Serialize(new[] { matchDto });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (path.Contains($"/api/inventory/packs/{trackingCode}"))
            {
                var json = JsonSerializer.Serialize(containerDto);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);
        var viewModel = CreateTestPosViewModel(workflowService: workflowService);

        // First scan adds to cart
        viewModel.SearchText = trackingCode;
        viewModel.ScanCommand.Execute(null);

        for (var i = 0; i < 50 && viewModel.CartItems.Count == 0; i++)
        {
            await Task.Delay(20);
        }
        Assert.Single(viewModel.CartItems);

        // Second scan with same tracking code
        viewModel.SearchText = trackingCode;
        viewModel.ScanCommand.Execute(null);

        for (var i = 0; i < 50 && !(viewModel.ScannerStatusMessage?.Contains("already in the cart") ?? false); i++)
        {
            await Task.Delay(20);
        }

        Assert.Single(viewModel.CartItems); // Cart item count unchanged
        Assert.Contains("already in the cart", viewModel.ScannerStatusMessage);
    }

    [Fact]
    public async Task Imp101_105_PosViewModel_ScanCommand_OrdinaryBarcodeOrName_OpensPackPicker()
    {
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();

        // Ordinary barcode scan: no exact unit or tracking code in match
        var matchDto = new ScannerProductMatchDto(
            Namespace: ScannerResolutionNamespace.ProductBarcode,
            ProductId: productId,
            ProductUnitId: productUnitId,
            ProductName: "Fuse Box 12-Way Pack",
            Sku: "FUSE-12W",
            Brand: "ABB",
            Category: "Distribution",
            UnitSymbol: "pcs",
            SellableStock: 20m,
            UnitPrice: 1200m,
            IsSerialized: false,
            InventoryUnitId: null,
            TrackingCode: null,
            SerialNumber: null,
            Imei1: null,
            Imei2: null,
            UnitStatus: null,
            IsSellable: true,
            SupplierName: null,
            PurchaseNumber: null,
            SourcePurchaseItemId: null,
            SupplierId: null,
            TrackingMode: TrackingMode.Container);

        var handler = new DelegatingHandlerStub(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/api/sales/scan"))
            {
                var json = JsonSerializer.Serialize(new[] { matchDto });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (path.Contains("/api/inventory/packs"))
            {
                var response = new PackContainerQueryResponse(0, Array.Empty<PackContainerDto>());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);
        var dialogService = new TestDialogService();
        var viewModel = CreateTestPosViewModel(dialogService: dialogService, workflowService: workflowService);

        viewModel.SearchText = "8901234567890";
        viewModel.ScanCommand.Execute(null);

        for (var i = 0; i < 50 && !dialogService.IsOpen; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(dialogService.IsOpen);
        Assert.IsType<PackPickerViewModel>(dialogService.Content);
    }

    [Fact]
    public async Task Imp102_105_PosViewModel_ScanCommand_ContinuousCableCoil_OpensCableCutDialog()
    {
        var productId = Guid.NewGuid();
        var productUnitId = Guid.NewGuid();

        var matchDto = new ScannerProductMatchDto(
            Namespace: ScannerResolutionNamespace.ProductBarcode,
            ProductId: productId,
            ProductUnitId: productUnitId,
            ProductName: "Armoured Power Cable 16mm",
            Sku: "ARM-16MM",
            Brand: "Pakistan Cables",
            Category: "Power",
            UnitSymbol: "m",
            SellableStock: 500m,
            UnitPrice: 850m,
            IsSerialized: false,
            InventoryUnitId: null,
            TrackingCode: null,
            SerialNumber: null,
            Imei1: null,
            Imei2: null,
            UnitStatus: null,
            IsSellable: true,
            SupplierName: null,
            PurchaseNumber: null,
            SourcePurchaseItemId: null,
            SupplierId: null,
            TrackingMode: TrackingMode.Length);

        var handler = new DelegatingHandlerStub(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/api/sales/scan"))
            {
                var json = JsonSerializer.Serialize(new[] { matchDto });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            if (path.Contains("/api/inventory/packs"))
            {
                var response = new PackContainerQueryResponse(0, Array.Empty<PackContainerDto>());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);
        var dialogService = new TestDialogService();
        var viewModel = CreateTestPosViewModel(dialogService: dialogService, workflowService: workflowService);

        viewModel.SearchText = "ARM-16MM";
        viewModel.ScanCommand.Execute(null);

        for (var i = 0; i < 50 && !dialogService.IsOpen; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(dialogService.IsOpen);
        Assert.IsType<CableCutViewModel>(dialogService.Content);
    }

    // =========================================================================
    // MON-W3R3-006: Sales Return Inspection, Remnant & Historical Supervisor Auth
    // =========================================================================
    [Fact]
    public async Task Imp103_SalesReturn_InspectionRemnantAndHistoricalSupervisorAuth_WiredAndEnforced()
    {
        var supervisorId = Guid.NewGuid();
        var sourceContainerId = Guid.NewGuid();
        var supervisorAccounts = new[]
        {
            new EdgeRetails.Application.Features.Identity.LoginAccountDto(supervisorId, "Manager Khan", "Supervisor", "MK", true)
        };

        var handler = new DelegatingHandlerStub(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/api/posting/period-lock-check"))
            {
                var query = req.RequestUri?.Query ?? string.Empty;
                var isHistorical = query.Contains("2026-10-10");
                var checkDto = new SupervisorApprovalModalViewModel.PeriodLockCheckDto(
                    PostingDate: DateOnly.FromDateTime(DateTime.Now).AddDays(-4),
                    ShopDate: DateOnly.FromDateTime(DateTime.Now),
                    IsLocked: false,
                    LockedPeriodName: null,
                    IsFuture: false,
                    ExceedsWindow: false,
                    EarliestAllowed: DateOnly.FromDateTime(DateTime.Now).AddDays(-7));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(checkDto))
                });
            }
            if (path.Contains("/api/posting/supervisors"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(supervisorAccounts))
                });
            }
            if (path.Contains("/api/posting/supervisor-approval"))
            {
                var approvalResp = new SupervisorApprovalModalViewModel.SupervisorApprovalApiResponse(
                    Authorized: true,
                    SupervisorId: supervisorId,
                    SupervisorDisplayName: "Manager Khan",
                    AuthorizedAtUtc: DateTimeOffset.UtcNow,
                    ApprovalToken: "tok-sup-999");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(approvalResp))
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var dialogService = new TestDialogService();
        var transactionService = new CapturingTransactionService();

        var sale = new SaleTransactionItemViewModel(
            1,
            DateTime.UtcNow,
            "Retail Customer",
            string.Empty,
            "Cashier 1",
            "Cash",
            [new SaleLineItemViewModel(1, Guid.NewGuid().ToString("D"), "Copper Wire 2.5mm", "CW-25", "", 120m, 10m)],
            invoiceDisplayOverride: "INV-9001");

        var returnVm = new SalesReturnViewModel(
            sale,
            transactionService: transactionService,
            apiClient: apiClient,
            dialogService: dialogService,
            operationIntents: new FileClientOperationIntentStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"intent_{Guid.NewGuid():N}.json")));

        var item = returnVm.ReturnItems[0];
        Assert.Contains("Intact", SalesReturnItemViewModel.InspectionConditions);
        Assert.Contains("Remnant", SalesReturnItemViewModel.InspectionConditions);
        Assert.Contains("Defective", SalesReturnItemViewModel.InspectionConditions);

        item.ReturnQuantity = 3m;
        item.ConditionInspectionResult = "Remnant";
        item.IsRestockAsRemnant = true;
        item.ReturnSourceContainerId = sourceContainerId;
        Assert.True(item.HasReturnQuantity);
        Assert.Equal("Remnant", item.ConditionInspectionResult);
        Assert.True(item.IsRestockAsRemnant);

        // Historical posting date gates CanProcessReturn on supervisor authorization
        returnVm.PostingDate = DateOnly.FromDateTime(DateTime.Now).AddDays(-4);
        Assert.True(returnVm.IsHistoricalPosting);
        Assert.False(returnVm.HasSupervisorApproval);
        Assert.False(returnVm.CanProcessReturn);

        // Attempting to process return without supervisor approval does nothing
        await returnVm.ProcessReturnAsync();
        Assert.Empty(transactionService.CapturedRequests);

        // Authorize supervisor opens modal dialog
        returnVm.AuthorizeSupervisorCommand.Execute(null);
        Assert.True(dialogService.IsOpen);
        var modal = Assert.IsType<SupervisorApprovalModalViewModel>(dialogService.Content);
        Assert.Equal(returnVm.PostingDate, modal.PostingDate);
        Assert.Equal("SaleReturn", modal.OperationType);

        // Authorize through supervisor modal workflow with callback
        modal.Supervisors.Add(supervisorAccounts[0]);
        modal.SelectedSupervisor = supervisorAccounts[0];
        modal.SupervisorPin = "7788";
        modal.Reason = "Authorizing customer return under shop policy";

        var authResult = await modal.AuthorizeAsync();
        Assert.True(authResult);

        // Supervisor PIN is zeroed out immediately after authorization
        Assert.Equal(string.Empty, modal.SupervisorPin);

        // Supervisor approval is wired into returnVm via normal onAuthorized callback
        Assert.True(returnVm.HasSupervisorApproval);
        Assert.Equal("Manager Khan", returnVm.SupervisorName);
        Assert.True(returnVm.CanProcessReturn);

        // Process return submits authoritative RecordSaleReturnRequest
        await returnVm.ProcessReturnAsync();
        Assert.Single(transactionService.CapturedRequests);
        var sentRequest = transactionService.CapturedRequests[0];

        Assert.Equal(returnVm.PostingDate, sentRequest.PostingDate);
        Assert.NotNull(sentRequest.SupervisorApproval);
        Assert.Equal(supervisorId, sentRequest.SupervisorApproval.SupervisorId);
        Assert.Equal("Authorizing customer return under shop policy", sentRequest.SupervisorApproval.Reason);
        Assert.Single(sentRequest.Items);
        Assert.Equal(3m, sentRequest.Items[0].Quantity);
        Assert.Equal("Remnant", sentRequest.Items[0].ConditionInspectionResult);
        Assert.True(sentRequest.Items[0].IsRestockAsRemnant);
        Assert.Equal(sourceContainerId, sentRequest.Items[0].ReturnSourceContainerId);

        // Clear command resets approval and reverts to current shop date
        returnVm.ClearSupervisorApprovalCommand.Execute(null);
        Assert.False(returnVm.HasSupervisorApproval);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now), returnVm.PostingDate);
        Assert.False(returnVm.IsHistoricalPosting);

        // Render actual SalesReturnDialog in STA
        OnSta(() =>
        {
            var dialog = new SalesReturnDialog { DataContext = returnVm };
            Layout(dialog, 480, 700);
            Assert.NotNull(dialog);
        });

        // Server Period Lock rejection: when period is locked, authorization is blocked
        var lockedHandler = new DelegatingHandlerStub(req =>
        {
            var path = req.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/api/posting/period-lock-check"))
            {
                var checkDto = new SupervisorApprovalModalViewModel.PeriodLockCheckDto(
                    PostingDate: DateOnly.FromDateTime(DateTime.Now).AddDays(-4),
                    ShopDate: DateOnly.FromDateTime(DateTime.Now),
                    IsLocked: true,
                    LockedPeriodName: "Period October 2026",
                    IsFuture: false,
                    ExceedsWindow: false,
                    EarliestAllowed: DateOnly.FromDateTime(DateTime.Now).AddDays(-7));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(checkDto))
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var lockedClient = new DesktopApiClient(new HttpClient(lockedHandler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var lockedModal = new SupervisorApprovalModalViewModel(
            lockedClient,
            postingDate: DateOnly.FromDateTime(DateTime.Now).AddDays(-4),
            shopDate: DateOnly.FromDateTime(DateTime.Now),
            operationType: "SaleReturn",
            dialogService: dialogService);

        await lockedModal.EvaluatePeriodLockAsync();
        Assert.True(lockedModal.IsPeriodLocked);
        Assert.False(lockedModal.CanSubmit);
        Assert.Contains("LOCKED", lockedModal.LockedPeriodBanner);
    }

    [Fact]
    public async Task Imp103_SerializedStocktakeScan_PackContainerMeasuredRemaining_TransportsMeasuredQuantities()
    {
        var stocktakeId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var containerId = Guid.NewGuid();
        var trackingCode = "COIL-STK-042";

        var containerDto = new PackContainerDto(
            ContainerId: containerId,
            TrackingCode: trackingCode,
            State: "Open",
            InitialQuantity: 100m,
            RemainingQuantity: 62.5m,
            ContinuousLength: 62.5m,
            UnitOfMeasure: "m",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        IReadOnlyDictionary<Guid, decimal>? capturedMeasuredQuantities = null;
        string? capturedReviewNote = null;
        decimal capturedSeparateLooseQuantity = 0m;
        var captureService = new StubStocktakeWorkflowService
        {
            PackResponse = containerDto,
            OnRecordSerialized = (sId, pId, fIds, unexp, note, measured, minInc, looseQty) =>
            {
                capturedReviewNote = note;
                capturedMeasuredQuantities = measured;
                capturedSeparateLooseQuantity = looseQty;
            }
        };

        var dialogService = new TestDialogService();
        var stocktake = new BackendStocktakeSnapshot(
            stocktakeId,
            StocktakeScope.FullShop,
            StocktakeStatus.Counting,
            DateTimeOffset.UtcNow,
            "Count Note",
            1,
            []);

        var line = new StocktakeLineViewModel(
            new BackendStocktakeLine(
                Guid.NewGuid(),
                productId,
                "Flex Wire Coil",
                "FLX-01",
                true,
                100m,
                0m,
                -100m,
                ""));

        var scanVm = new SerializedStocktakeScanViewModel(
            stocktake,
            line,
            captureService,
            dialogService,
            () => Task.CompletedTask);

        // 1. Invalid input: negative measured quantity is rejected
        scanVm.ScannedIdentities = $"{trackingCode}=-5.0";
        await scanVm.ConfirmAsync();
        Assert.Equal("Measured container quantity cannot be negative.", scanVm.Message);
        Assert.Null(capturedMeasuredQuantities);

        // 2. Invalid input: measured quantity exceeding container initial capacity is rejected
        scanVm.ScannedIdentities = $"{trackingCode}=120.0";
        await scanVm.ConfirmAsync();
        Assert.Contains("cannot exceed initial container capacity 100", scanVm.Message);
        Assert.Null(capturedMeasuredQuantities);

        // 3. Invalid input: negative separate loose count is rejected
        scanVm.ScannedIdentities = $"{trackingCode}=55.0";
        scanVm.SeparateLooseCountText = "-2";
        await scanVm.ConfirmAsync();
        Assert.Equal("Separate loose count cannot be negative.", scanVm.Message);
        Assert.Null(capturedMeasuredQuantities);

        // 4. Valid input: Operator measured quantity DIFFERENT from server expected (55.0 vs 62.5)
        // and separate loose count provided
        scanVm.ScannedIdentities = $"{trackingCode}=55.0";
        scanVm.SeparateLooseCountText = "12";
        await scanVm.ConfirmAsync();

        Assert.NotNull(capturedMeasuredQuantities);
        Assert.True(capturedMeasuredQuantities.ContainsKey(containerId));
        Assert.Equal(55.0m, capturedMeasuredQuantities[containerId]); // Operator measured count, NOT server expected 62.5m
        Assert.Equal(12m, capturedSeparateLooseQuantity); // Operator separate loose count transported authoritatively
        Assert.NotNull(capturedReviewNote);
        Assert.Contains("[Loose stock counted: 12]", capturedReviewNote);

        // 5. Authoritative total counted and variance calculation
        var totalCounted = capturedMeasuredQuantities[containerId] + capturedSeparateLooseQuantity;
        Assert.Equal(67.0m, totalCounted);
        var expectedQty = line.ExpectedSellableQty; // 100m
        var calculatedVariance = totalCounted - expectedQty;
        Assert.Equal(-33.0m, calculatedVariance);

        // 6. Render actual SerializedStocktakeScanDialog in STA
        OnSta(() =>
        {
            var dialog = new SerializedStocktakeScanDialog { DataContext = scanVm };
            Layout(dialog, 620, 500);
            Assert.NotNull(dialog);
        });
    }

    [Fact]
    public async Task Imp103_RemoteStocktake_ReplayIntent_PreservesOperationId_And_ChangedMeasurementTriggersConflict()
    {
        var stocktakeId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var containerId = Guid.NewGuid();

        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"intent_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new FileClientOperationIntentStore(tempPath);
            var postedOperationIds = new List<Guid>();

            var handler = new DelegatingHandlerStub(async req =>
            {
                if (req.Content is not null)
                {
                    var body = await req.Content.ReadAsStringAsync();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("clientOperationId", out var idProp))
                        {
                            postedOperationIds.Add(idProp.GetGuid());
                        }
                    }
                }
                // Return 503 so intent remains pending/unresolved
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"service_unavailable\"}")
                };
            });

            var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
            var service = new RemoteStocktakeWorkflowService(apiClient, store);

            // 1. Initial attempt with measured quantity and separate loose count
            var measured1 = new Dictionary<Guid, decimal> { [containerId] = 50.0m };
            await Assert.ThrowsAsync<BackendOperationException>(() =>
                service.RecordSerializedStocktakeAsync(stocktakeId, productId, [containerId], [], "First attempt", measured1, minimumSellableIncrement: 1m, separateLooseQuantity: 10m));

            Assert.Single(postedOperationIds);
            var initialOpId = postedOperationIds[0];
            Assert.NotEqual(Guid.Empty, initialOpId);

            // 2. Replay with identical intent (same measurements, increment, loose count) preserves operation ID
            await Assert.ThrowsAsync<BackendOperationException>(() =>
                service.RecordSerializedStocktakeAsync(stocktakeId, productId, [containerId], [], "First attempt", measured1, minimumSellableIncrement: 1m, separateLooseQuantity: 10m));

            Assert.Equal(2, postedOperationIds.Count);
            Assert.Equal(initialOpId, postedOperationIds[1]); // Preserves replay operation identity!

            // 3. Changed intent (different measured container quantity) triggers intent conflict
            var changedMeasured = new Dictionary<Guid, decimal> { [containerId] = 55.0m };
            var ex1 = await Assert.ThrowsAsync<BackendOperationException>(() =>
                service.RecordSerializedStocktakeAsync(stocktakeId, productId, [containerId], [], "First attempt", changedMeasured, minimumSellableIncrement: 1m, separateLooseQuantity: 10m));
            Assert.Equal("inventory.stocktake_outcome_unknown", ex1.Code);

            // 4. Changed intent (different separate loose count) triggers intent conflict
            var ex2 = await Assert.ThrowsAsync<BackendOperationException>(() =>
                service.RecordSerializedStocktakeAsync(stocktakeId, productId, [containerId], [], "First attempt", measured1, minimumSellableIncrement: 1m, separateLooseQuantity: 15m));
            Assert.Equal("inventory.stocktake_outcome_unknown", ex2.Code);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData((HttpStatusCode)429)]
    public async Task Imp103_RemoteStocktake_RetryableHttpStatus_PreservesOperationId(HttpStatusCode statusCode)
    {
        var stocktakeId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var tempPath = Path.Combine(Path.GetTempPath(), $"stocktake_retry_{Guid.NewGuid():N}.json");
        var postedOperationIds = new List<Guid>();
        try
        {
            var store = new FileClientOperationIntentStore(tempPath);
            var handler = new DelegatingHandlerStub(async request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    postedOperationIds.Add(body.RootElement.GetProperty("clientOperationId").GetGuid());
                    return new HttpResponseMessage(statusCode)
                    {
                        Content = new StringContent("{\"error\":\"outcome unknown\"}")
                    };
                }

                // The service cannot confirm whether the failed POST committed. Preserve its ID.
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"status unavailable\"}")
                };
            });
            var apiClient = new DesktopApiClient(new HttpClient(handler)
            {
                BaseAddress = new Uri("http://127.0.0.1:5000")
            });
            var service = new RemoteStocktakeWorkflowService(apiClient, store);

            var first = await Assert.ThrowsAsync<BackendOperationException>(() =>
                service.RecordStocktakeCountAsync(stocktakeId, productId, 12m, "retry proof"));
            Assert.Equal("inventory.stocktake_outcome_unknown", first.Code);
            var pendingAfterFirst = Assert.Single(store.FindPendingByPrefix($"count:{stocktakeId:D}:"));
            Assert.Single(postedOperationIds);
            Assert.Equal(postedOperationIds[0], pendingAfterFirst.OperationId);

            var second = await Assert.ThrowsAsync<BackendOperationException>(() =>
                service.RecordStocktakeCountAsync(stocktakeId, productId, 12m, "retry proof"));
            Assert.Equal("inventory.stocktake_outcome_unknown", second.Code);
            Assert.Equal(2, postedOperationIds.Count);
            Assert.Equal(postedOperationIds[0], postedOperationIds[1]);
            Assert.Equal(pendingAfterFirst.OperationId, Assert.Single(store.FindPendingByPrefix($"count:{stocktakeId:D}:")).OperationId);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    // =========================================================================
    // MON-W3R3-007: Product Management TaxonomyTree & Dynamic Typed Validation
    // =========================================================================
    [Fact]
    public void Imp104_ProductManagement_TaxonomyTree_HierarchyAndRedirectNotice_WiredToCatalog()
    {
        var categoryA01Id = Guid.NewGuid();
        var categoryB01Id = Guid.NewGuid();
        var categoryD03Id = Guid.NewGuid();

        var prodA = new BackendProductManagementItem(
            ProductId: Guid.NewGuid(),
            Sku: "NC-A01-WIRE",
            Name: "Single Core Building Wire",
            Brand: "National Cables",
            Model: "SC-1",
            CategoryId: categoryA01Id,
            Category: "Building Wires & Power Cables",
            BaseUnitId: Guid.NewGuid(),
            BaseUnit: "m",
            TrackingMode: TrackingMode.Length,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 100m,
            DefaultSalePrice: 150m,
            MinimumStockLevel: 20m,
            DefaultWarrantyMonths: 12,
            AttributesJson: "{}",
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 1,
            ProductUnits: [],
            SupplierProducts: [],
            ClassRef: "A01-01");

        var differentProductUnitSameClass = new BackendProductManagementItem(
            ProductId: Guid.NewGuid(),
            Sku: "NC-A01-SECOND-SAME-CLASS",
            Name: "Wire with same category and class",
            Brand: "Other",
            Model: null,
            CategoryId: categoryA01Id,
            Category: "Building Wires & Power Cables",
            BaseUnitId: Guid.NewGuid(),
            BaseUnit: "m",
            TrackingMode: TrackingMode.Length,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 110m,
            DefaultSalePrice: 160m,
            MinimumStockLevel: 0m,
            DefaultWarrantyMonths: 0,
            AttributesJson: "{}",
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 1,
            ProductUnits: [],
            SupplierProducts: [],
            ClassRef: "A01-01");

        var sameNameUnassigned = new BackendProductManagementItem(
            ProductId: Guid.NewGuid(),
            Sku: "LEGACY-UNMAPPED-WIRE",
            Name: "Single Core Building Wire (legacy)",
            Brand: "Legacy",
            Model: null,
            CategoryId: categoryA01Id,
            Category: "Building Wires & Power Cables",
            BaseUnitId: Guid.NewGuid(),
            BaseUnit: "m",
            TrackingMode: TrackingMode.Length,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 90m,
            DefaultSalePrice: 140m,
            MinimumStockLevel: 0m,
            DefaultWarrantyMonths: 0,
            AttributesJson: "{\"legacy\":\"A01-01\"}",
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 1,
            ProductUnits: [],
            SupplierProducts: []);

        var siblingClass = new BackendProductManagementItem(
            ProductId: Guid.NewGuid(),
            Sku: "SIBLING-WIRE-02",
            Name: "Twin-core building cable",
            Brand: null,
            Model: null,
            CategoryId: categoryA01Id,
            Category: "Building Wires & Power Cables",
            BaseUnitId: Guid.NewGuid(),
            BaseUnit: "m",
            TrackingMode: TrackingMode.Length,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 80m,
            DefaultSalePrice: 130m,
            MinimumStockLevel: 0m,
            DefaultWarrantyMonths: 0,
            AttributesJson: "{}",
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 1,
            ProductUnits: [],
            SupplierProducts: [],
            ClassRef: "A01-02");

        var prodB = new BackendProductManagementItem(
            ProductId: Guid.NewGuid(),
            Sku: "PH-B01-BULB",
            Name: "LED Filament Lamp",
            Brand: "Philips",
            Model: "FIL-9W",
            CategoryId: categoryB01Id,
            Category: "Lamps & Light Sources",
            BaseUnitId: Guid.NewGuid(),
            BaseUnit: "pcs",
            TrackingMode: TrackingMode.Quantity,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 200m,
            DefaultSalePrice: 350m,
            MinimumStockLevel: 10m,
            DefaultWarrantyMonths: 24,
            AttributesJson: "{}",
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 1,
            ProductUnits: [],
            SupplierProducts: [],
            ClassRef: "B01-01");

        var prodD = new BackendProductManagementItem(
            ProductId: Guid.NewGuid(),
            Sku: "DW-D03-FREESTAND",
            Name: "Freestanding Dishwasher",
            Brand: "Haier",
            Model: "HD-12",
            CategoryId: categoryD03Id,
            Category: "Domestic dishwasher appliance",
            BaseUnitId: Guid.NewGuid(),
            BaseUnit: "pcs",
            TrackingMode: TrackingMode.Quantity,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 45000m,
            DefaultSalePrice: 62000m,
            MinimumStockLevel: 2m,
            DefaultWarrantyMonths: 36,
            AttributesJson: "{}",
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 1,
            ProductUnits: [],
            SupplierProducts: []);

        var snapshot = new BackendProductManagementSnapshot(
            [prodA, differentProductUnitSameClass, sameNameUnassigned, siblingClass, prodB, prodD],
            [
                new BackendCatalogCategory(categoryA01Id, "Building Wires & Power Cables", "A01", true),
                new BackendCatalogCategory(categoryB01Id, "Lamps & Light Sources", "B01", true),
                new BackendCatalogCategory(categoryD03Id, "Domestic dishwasher appliance", "D03", true)
            ],
            [new BackendCatalogUnit(Guid.NewGuid(), "Piece", "pcs", 0, true)],
            [],
            []);

        var dialogService = new TestDialogService();
        var toastService = new TestToastService();

        var vm = new ProductManagementViewModel(toastService, dialogService, null);
        vm.SetSnapshotForTesting(snapshot);

        // Taxonomy tree initializes full 9 divisions
        Assert.Equal(9, vm.TaxonomyTree.Count);
        var totalDepartments = vm.TaxonomyTree.Sum(d => d.Children.Count);
        Assert.Equal(65, totalDepartments);
        var totalClasses = vm.TaxonomyTree.Sum(d => d.Children.Sum(dept => dept.Children.Count));
        Assert.Equal(732, totalClasses);

        // Initial catalog shows all five test products.
        Assert.Equal(6, vm.FilteredProducts.Count);

        var exactClassNode = vm.TaxonomyTree
            .SelectMany(d => d.Children)
            .SelectMany(dept => dept.Children)
            .Single(c => c.Code == "A01-01");
        vm.SelectedTaxonomyNode = exactClassNode;
        vm.FilterBySelectedTaxonomy();
        Assert.Equal(new[] { "NC-A01-SECOND-SAME-CLASS", "NC-A01-WIRE" }, vm.FilteredProducts.Select(x => x.Sku).OrderBy(x => x, StringComparer.Ordinal));
        vm.ClearTaxonomyFilter();
        Assert.Equal(6, vm.FilteredProducts.Count);

        // Find deprecated class redirect node (B01-03 redirects to B01-01)
        var deprecatedNode = vm.TaxonomyTree
            .SelectMany(d => d.Children)
            .SelectMany(dept => dept.Children)
            .FirstOrDefault(c => c.IsDeprecated && c.Code == "B01-03")
            ?? vm.TaxonomyTree
                .SelectMany(d => d.Children)
                .SelectMany(dept => dept.Children)
                .First(c => c.IsDeprecated);

        Assert.NotNull(deprecatedNode);
        Assert.False(string.IsNullOrWhiteSpace(deprecatedNode.RedirectTargetCode));

        // Select deprecated node in view model
        vm.SelectedTaxonomyNode = deprecatedNode;
        Assert.True(vm.HasSelectedTaxonomyNode);
        Assert.True(vm.HasTaxonomyRedirect);
        Assert.Contains(deprecatedNode.RedirectTargetCode!, vm.TaxonomyRedirectNotice);

        // FilterBySelectedTaxonomy actively filters catalog rows under canonical redirect semantics
        vm.FilterBySelectedTaxonomyCommand.Execute(null);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.Equal(0, vm.SelectedTabIndex); // Switched to catalog tab

        // Redirect filtering returns only the canonically assigned lamp; the sibling-category
        // lamp and unassigned legacy row whose text mentions the old class stay excluded.
        Assert.Equal(new[] { "PH-B01-BULB" }, vm.FilteredProducts.Select(x => x.Sku));

        // Render actual ProductManagementView in STA
        OnSta(() =>
        {
            var view = new ProductManagementView { DataContext = vm };
            Layout(view, 1024, 768);
            Assert.NotNull(view);
        });
    }

    [Fact]
    public async Task Imp104_ProductEdit_DynamicTypedAttributes_ValidationAndIdentityPreservation()
    {
        var capturingService = new CapturingProductManagementService();
        var dialogService = new TestDialogService();
        var toastService = new TestToastService();

        var categoryB01Id = Guid.NewGuid();
        var unmappedCategoryId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var baseUnitId = Guid.NewGuid();
        var existingProductUnitId = Guid.NewGuid();
        var existingProductId = Guid.NewGuid();

        var snapshot = new BackendProductManagementSnapshot(
            [],
            [
                new BackendCatalogCategory(categoryB01Id, "Lamps & Light Sources", "B01", true),
                new BackendCatalogCategory(unmappedCategoryId, "Legacy Vintage Spare Parts", "LEGACY-99", true)
            ],
            [new BackendCatalogUnit(baseUnitId, "Piece", "pcs", 0, true)],
            [],
            [new BackendCatalogCompany(companyId, "National Cables", "NC", true)]);

        // 1. Known non-A01 department (B01 Lamps & Light Sources)
        var productB = new BackendProductManagementItem(
            ProductId: existingProductId,
            Sku: "PH-B01-BULB9",
            Name: "LED Bulb 9W",
            Brand: "Philips",
            Model: "B9",
            CategoryId: categoryB01Id,
            Category: "Lamps & Light Sources",
            BaseUnitId: baseUnitId,
            BaseUnit: "pcs",
            TrackingMode: TrackingMode.Quantity,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 150m,
            DefaultSalePrice: 220m,
            MinimumStockLevel: 50m,
            DefaultWarrantyMonths: 12,
            AttributesJson: "{\"lumens\":\"800\"}",
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 3,
            ProductUnits: [
                new BackendProductUnitConfiguration(
                    existingProductUnitId,
                    baseUnitId,
                    "Piece",
                    "pcs",
                    1m,
                    true,
                    true,
                    true,
                    true,
                    true,
                    true)
            ],
            SupplierProducts: [],
            CompanyId: companyId,
            CompanyName: "National Cables",
            CompanyCode: "NC",
            ModelCode: "B9");

        var editVmB01 = new ProductEditViewModel(
            productB,
            snapshot,
            capturingService,
            toastService,
            () => { },
            () => Task.CompletedTask);

        // Department B01 attribute profiles loaded (NOT A01)
        Assert.False(editVmB01.IsTaxonomyUnmapped);
        Assert.Null(editVmB01.TaxonomyMappingNotice);
        Assert.True(editVmB01.HasAttributeItems);
        Assert.All(editVmB01.AttributeItems, item => Assert.Equal("B01", item.Profile.DepartmentCode));

        // 2. Unmapped legacy category honestly isolated without imposing unrelated department requirements
        var legacyProduct = new BackendProductManagementItem(
            ProductId: Guid.NewGuid(),
            Sku: "LEG-PART-001",
            Name: "Antique Brass Fuse Holder",
            Brand: "Vintage",
            Model: "V-1",
            CategoryId: unmappedCategoryId,
            Category: "Legacy Vintage Spare Parts",
            BaseUnitId: baseUnitId,
            BaseUnit: "pcs",
            TrackingMode: TrackingMode.Quantity,
            SerialTrackingEnabled: false,
            ImeiTrackingEnabled: false,
            ReferencePurchaseCost: 50m,
            DefaultSalePrice: 100m,
            MinimumStockLevel: 5m,
            DefaultWarrantyMonths: 0,
            AttributesJson: "{\"legacy_note\":\"preserved_as_is\"}",
            AttributesSchemaVersion: 1,
            IsActive: true,
            Version: 1,
            ProductUnits: [
                new BackendProductUnitConfiguration(
                    Guid.NewGuid(),
                    baseUnitId,
                    "Piece",
                    "pcs",
                    1m,
                    true,
                    true,
                    true,
                    true,
                    true,
                    true)
            ],
            SupplierProducts: []);

        var editVmLegacy = new ProductEditViewModel(
            legacyProduct,
            snapshot,
            capturingService,
            toastService,
            () => { },
            () => Task.CompletedTask);

        Assert.True(editVmLegacy.IsTaxonomyUnmapped);
        Assert.NotNull(editVmLegacy.TaxonomyMappingNotice);
        Assert.Contains("Legacy/unmapped category", editVmLegacy.TaxonomyMappingNotice);
        Assert.Empty(editVmLegacy.AttributeItems); // Zero arbitrary department profiles imposed

        // 3. Typed attribute validation blocks save on invalid values
        var targetAttr = editVmB01.AttributeItems.FirstOrDefault(a => a.IsNumber);
        if (targetAttr is not null)
        {
            targetAttr.CurrentValue = "invalid-not-a-number";
            Assert.False(targetAttr.Validate());
            Assert.True(targetAttr.HasError);

            // SaveAsync blocks on invalid attribute value
            await editVmB01.SaveAsync();
            Assert.True(editVmB01.HasValidationMessage);
            Assert.Empty(capturingService.UpdatedRequests);

            // Correct value allows save
            targetAttr.CurrentValue = "850";
            Assert.True(targetAttr.Validate());
        }

        // 4. Valid save caller preserves identities and existing fields
        await editVmB01.SaveAsync();
        Assert.Single(capturingService.UpdatedRequests);
        var updated = capturingService.UpdatedRequests[0];
        Assert.Equal(productB.ProductId, updated.ProductId);
        Assert.Equal(productB.Version, updated.ExpectedVersion);
        Assert.Equal("PH-B01-BULB9", updated.Request.Sku);
        Assert.Equal("LED Bulb 9W", updated.Request.Name);

        // 5. Render actual ProductEditDialog in STA
        OnSta(() =>
        {
            var dialog = new ProductEditDialog { DataContext = editVmB01 };
            Layout(dialog, 600, 750);
            Assert.NotNull(dialog);
        });
    }

    private sealed class StubStocktakeWorkflowService : IBackendWorkflowReadService
    {
        public PackContainerDto? PackResponse { get; set; }
        public Action<Guid, Guid, IReadOnlyCollection<Guid>, IReadOnlyCollection<string>, string?, IReadOnlyDictionary<Guid, decimal>?, decimal, decimal>? OnRecordSerialized { get; set; }

        public Task<IReadOnlyList<BackendScannerMatch>> ResolveScannerAsync(string input, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackendScannerMatch>>(Array.Empty<BackendScannerMatch>());

        public Task<PackContainerDto?> GetPackByTrackingCodeAsync(string trackingCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(PackResponse);

        public Task<IReadOnlyList<BackendExactUnit>> GetExactUnitsAsync(Guid productId, InventoryUnitStatus? status = null, Guid? sourcePurchaseItemId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackendExactUnit>>(Array.Empty<BackendExactUnit>());

        public Task<IReadOnlyList<BackendPosDraftSummary>> GetOpenDraftsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackendPosDraftSummary>>(Array.Empty<BackendPosDraftSummary>());

        public Task<BackendPosDraftDetail?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BackendPosDraftDetail?>(null);

        public Task<BackendPosDraftState> SaveDraftAsync(Guid? draftId, long? expectedVersion, Guid? customerId, string? note, IReadOnlyList<BackendPosDraftItemInput> items, Guid clientOperationId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task CancelDraftAsync(Guid draftId, long? expectedVersion, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<BackendStocktakeSnapshot?> GetOpenStocktakeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<BackendStocktakeSnapshot?>(null);

        public Task<BackendStocktakeSnapshot> StartFullShopStocktakeAsync(string? note, Guid? clientOperationId = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RecordStocktakeCountAsync(Guid stocktakeId, Guid productId, decimal countedSellableQty, string? reviewNote, Guid? clientOperationId = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordSerializedStocktakeAsync(
            Guid stocktakeId,
            Guid productId,
            IReadOnlyCollection<Guid> foundInventoryUnitIds,
            IReadOnlyCollection<string> unexpectedIdentitySnapshots,
            string? reviewNote,
            IReadOnlyDictionary<Guid, decimal>? measuredContainerQuantities = null,
            decimal minimumSellableIncrement = 1m,
            decimal separateLooseQuantity = 0m,
            Guid? clientOperationId = null,
            CancellationToken cancellationToken = default)
        {
            OnRecordSerialized?.Invoke(stocktakeId, productId, foundInventoryUnitIds, unexpectedIdentitySnapshots, reviewNote, measuredContainerQuantities, minimumSellableIncrement, separateLooseQuantity);
            return Task.CompletedTask;
        }

        public Task ReviewStocktakeAsync(Guid stocktakeId, Guid? clientOperationId = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PostStocktakeAsync(Guid stocktakeId, Guid? clientOperationId = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CancelStocktakeAsync(Guid stocktakeId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<PackContainerQueryResponse> GetPackContainersAsync(Guid? productId = null, string? conditionFilter = null, decimal? minContinuousLength = null, int pageNumber = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PackContainerQueryResponse(0, Array.Empty<PackContainerDto>()));

        public Task<PackAllocationResponse> AllocatePackAsync(EdgeRetails.Application.Contracts.Packs.PackAllocationRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class CapturingTransactionService : ITransactionService
    {
        public List<RecordSaleReturnRequest> CapturedRequests { get; } = [];

        public Task<SaleReturnRecord> RecordReturnAsync(RecordSaleReturnRequest request, CancellationToken cancellationToken = default)
        {
            CapturedRequests.Add(request);
            return Task.FromResult(new SaleReturnRecord
            {
                ReturnNumber = "RET-001",
                InvoiceNumber = request.InvoiceNumber,
                Timestamp = DateTime.UtcNow,
                Disposition = request.Disposition,
                RefundMethod = request.RefundMethod,
                Notes = request.Notes,
                TotalRefundAmount = request.TotalRefundAmount,
                Items = request.Items
            });
        }

        public SaleReturnRecord RecordReturn(RecordSaleReturnRequest request) =>
            RecordReturnAsync(request).GetAwaiter().GetResult();

        public Task<SaleTransactionRecord> RecordTransactionAsync(RecordSaleRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public SaleTransactionRecord RecordTransaction(RecordSaleRequest request) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<SaleTransactionRecord>> GetAllTransactionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SaleTransactionRecord>>([]);
        public IReadOnlyList<SaleTransactionRecord> GetAllTransactions() => [];
        public Task<SaleTransactionRecord?> GetByInvoiceNumberAsync(string invoiceNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<SaleTransactionRecord?>(null);
        public SaleTransactionRecord? GetByInvoiceNumber(string invoiceNumber) => null;
        public IReadOnlyList<SaleReturnRecord> GetReturnsForInvoice(string invoiceNumber) => [];
        public IReadOnlyList<SaleReturnRecord> GetAllReturns() => [];

        public event EventHandler<SaleTransactionRecord>? TransactionRecorded { add { } remove { } }
        public event EventHandler<SaleReturnRecord>? ReturnRecorded { add { } remove { } }

        public decimal TodaySales => 0m;
        public int TodayCount => 0;
        public decimal TotalReturns => 0m;
        public decimal NetSales => 0m;
    }

    private sealed class CapturingProductManagementService : IBackendProductManagementService
    {
        public List<BackendProductCatalogRequest> CreatedRequests { get; } = [];
        public List<(Guid ProductId, long ExpectedVersion, BackendProductCatalogRequest Request)> UpdatedRequests { get; } = [];

        public Task<BackendProductManagementSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<BackendProductManagementItem>> GetProductsPageAsync(string? search, bool? isActive, Guid? categoryId, int pageSize = 200, string? beforeName = null, Guid? beforeProductId = null, CancellationToken cancellationToken = default, string? classRef = null) =>
            Task.FromResult<IReadOnlyList<BackendProductManagementItem>>([]);
        public Task<BackendProductManagementItem?> GetProductAsync(Guid productId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BackendProductManagementItem?>(null);
        public Task<BackendProductManagementItem?> GetProductBySkuAsync(string sku, CancellationToken cancellationToken = default) =>
            Task.FromResult<BackendProductManagementItem?>(null);

        public Task<BackendProductManagementItem> CreateProductAsync(BackendProductCatalogRequest request, IReadOnlyList<BackendProductUnitConfiguration> units, IReadOnlyCollection<Guid> linkedSupplierIds, CancellationToken cancellationToken = default)
        {
            CreatedRequests.Add(request);
            return Task.FromResult(new BackendProductManagementItem(
                Guid.NewGuid(), request.Sku, request.Name, request.Brand, request.Model,
                request.CategoryId, "Cat", request.BaseUnitId, "Base", request.TrackingMode,
                request.SerialTrackingEnabled, request.ImeiTrackingEnabled, request.ReferencePurchaseCost,
                request.DefaultSalePrice, request.MinimumStockLevel, request.DefaultWarrantyMonths,
                request.AttributesJson, request.AttributesSchemaVersion, true, 1, units, []));
        }

        public Task<BackendProductManagementItem> UpdateProductAsync(Guid productId, long expectedVersion, BackendProductCatalogRequest request, IReadOnlyList<BackendProductUnitConfiguration> units, IReadOnlyCollection<Guid> linkedSupplierIds, CancellationToken cancellationToken = default)
        {
            UpdatedRequests.Add((productId, expectedVersion, request));
            return Task.FromResult(new BackendProductManagementItem(
                productId, request.Sku, request.Name, request.Brand, request.Model,
                request.CategoryId, "Cat", request.BaseUnitId, "Base", request.TrackingMode,
                request.SerialTrackingEnabled, request.ImeiTrackingEnabled, request.ReferencePurchaseCost,
                request.DefaultSalePrice, request.MinimumStockLevel, request.DefaultWarrantyMonths,
                request.AttributesJson, request.AttributesSchemaVersion, true, expectedVersion + 1, units, []));
        }

        public Task<BackendProductManagementItem> SetProductActiveAsync(Guid productId, long expectedVersion, bool isActive, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task SaveCompanyAsync(Guid? companyId, string name, string? code = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetCompanyActiveAsync(Guid companyId, bool isActive, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SaveCategoryAsync(Guid? categoryId, string name, string? identitySymbol = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetCategoryActiveAsync(Guid categoryId, bool isActive, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SaveUnitAsync(Guid? unitId, string name, string symbol, int displayDecimalPlaces, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetUnitActiveAsync(Guid unitId, bool isActive, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    // =========================================================================
    // Test Helpers and Stubs
    // =========================================================================
    private static readonly object AppLock = new();

    private static void EnsureApplication()
    {
        lock (AppLock)
        {
            if (System.Windows.Application.Current is null)
            {
                var app = new App();
                app.InitializeComponent();
            }
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplication();
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Offscreen WPF layout did not complete in 30 seconds.");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void Layout(FrameworkElement control, double width, double height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        control.UpdateLayout();
    }

    private sealed class TestDialogService : IDialogService
    {
        public bool IsOpen { get; private set; }
        public object? Content { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Show(object content)
        {
            Content = content;
            IsOpen = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
        }

        public void Close()
        {
            Content = null;
            IsOpen = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOpen)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
        }
    }

    private static PosViewModel CreateTestPosViewModel(
        IDialogService? dialogService = null,
        IBackendWorkflowReadService? workflowService = null)
    {
        var txService = DispatchProxy.Create<ITransactionService, UnusedProxy>();
        return new PosViewModel(
            transactionService: txService,
            dialogService: dialogService,
            workflowService: workflowService);
    }

    public class UnusedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("Unused in test");
    }

    private sealed class TestToastService : IToastService
    {
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<ToastMessage> Messages { get; } =
            new(new System.Collections.ObjectModel.ObservableCollection<ToastMessage>());
        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handlerFunc(request);
    }

    #region Canonical LF09 Behavioral Tests (Desktop Multi-Page Pack & Coil Traversal)

    [Fact]
    public async Task LF09_PackPicker_DiscoversSuitable51stPackAcrossPagingBoundary()
    {
        var productId = Guid.NewGuid();
        var requestedQuantity = 5m;

        // Generate 50 open packs on page 1 with RemainingQuantity = 1m (< 5m)
        var page1Items = Enumerable.Range(1, 50).Select(i => new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: $"PACK-{i:D3}",
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 1m,
            ContinuousLength: 1m,
            UnitOfMeasure: "pcs",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null)).ToList();

        // 51st pack on page 2 with RemainingQuantity = 10m (>= 5m)
        var pack51Id = Guid.NewGuid();
        var pack51 = new PackContainerDto(
            ContainerId: pack51Id,
            TrackingCode: "PACK-051",
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 10m,
            ContinuousLength: 10m,
            UnitOfMeasure: "pcs",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        var requestedPages = new List<int>();

        var handler = new DelegatingHandlerStub(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("/api/inventory/packs"))
            {
                var pageNum = 1;
                if (uri.Contains("pageNumber=2"))
                {
                    pageNum = 2;
                }
                requestedPages.Add(pageNum);

                if (pageNum == 1)
                {
                    var resp1 = new PackContainerQueryResponse(51, page1Items);
                    var json1 = JsonSerializer.Serialize(resp1);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json1, System.Text.Encoding.UTF8, "application/json")
                    });
                }
                else if (pageNum == 2)
                {
                    var resp2 = new PackContainerQueryResponse(51, new List<PackContainerDto> { pack51 });
                    var json2 = JsonSerializer.Serialize(resp2);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json2, System.Text.Encoding.UTF8, "application/json")
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);

        var viewModel = new PackPickerViewModel(
            workflowService,
            productId: productId,
            productName: "LED Bulb 10W Pack",
            requestedQuantity: requestedQuantity,
            unitOfMeasure: "pcs");

        await viewModel.LoadContainersAsync();

        // Multi-page traversal proved: both page 1 and page 2 were requested
        Assert.Contains(1, requestedPages);
        Assert.Contains(2, requestedPages);

        // Discovered exactly 51 packs across page boundary
        Assert.Equal(51, viewModel.Containers.Count);

        // Focused assertion naming the 51st pack
        Assert.NotNull(viewModel.SelectedContainer);
        Assert.Equal("PACK-051", viewModel.SelectedContainer.TrackingCode);
        Assert.Equal(10m, viewModel.SelectedContainer.RemainingQuantity);
        Assert.Equal(pack51Id, viewModel.SelectedContainer.ContainerId);
        Assert.True(viewModel.CanConfirm);
        Assert.Null(viewModel.StaleCapacityPrompt);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task LF09_CableCut_DiscoversSuitable51stCoilAcrossPagingBoundary()
    {
        var productId = Guid.NewGuid();
        var requestedCutLength = 25m;

        // Generate 50 open coils on page 1 with ContinuousLength = 10m (< 25m)
        var page1Coils = Enumerable.Range(1, 50).Select(i => new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: $"COIL-{i:D3}",
            State: "Open",
            InitialQuantity: 50m,
            RemainingQuantity: 10m,
            ContinuousLength: 10m,
            UnitOfMeasure: "m",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null)).ToList();

        // 51st coil on page 2 with ContinuousLength = 50m (>= 25m)
        var coil51Id = Guid.NewGuid();
        var coil51 = new PackContainerDto(
            ContainerId: coil51Id,
            TrackingCode: "COIL-051",
            State: "Open",
            InitialQuantity: 100m,
            RemainingQuantity: 50m,
            ContinuousLength: 50m,
            UnitOfMeasure: "m",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null);

        var requestedPages = new List<int>();

        var handler = new DelegatingHandlerStub(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("/api/inventory/packs"))
            {
                var pageNum = 1;
                if (uri.Contains("pageNumber=2"))
                {
                    pageNum = 2;
                }
                requestedPages.Add(pageNum);

                if (pageNum == 1)
                {
                    var resp1 = new PackContainerQueryResponse(51, page1Coils);
                    var json1 = JsonSerializer.Serialize(resp1);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json1, System.Text.Encoding.UTF8, "application/json")
                    });
                }
                else if (pageNum == 2)
                {
                    var resp2 = new PackContainerQueryResponse(51, new List<PackContainerDto> { coil51 });
                    var json2 = JsonSerializer.Serialize(resp2);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json2, System.Text.Encoding.UTF8, "application/json")
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);

        var viewModel = new CableCutViewModel(
            workflowService,
            productId: productId,
            productName: "Copper Cable 2.5mm",
            requestedCutLength: requestedCutLength,
            unitOfMeasure: "m");

        await viewModel.LoadCoilsAsync();

        // Multi-page traversal proved
        Assert.Contains(1, requestedPages);
        Assert.Contains(2, requestedPages);

        // Discovered exactly 51 coils
        Assert.Equal(51, viewModel.Coils.Count);

        // Focused assertion naming the 51st coil
        Assert.NotNull(viewModel.SelectedCoil);
        Assert.Equal("COIL-051", viewModel.SelectedCoil.TrackingCode);
        Assert.Equal(50m, viewModel.SelectedCoil.ContinuousLength);
        Assert.Equal(coil51Id, viewModel.SelectedCoil.ContainerId);
        Assert.True(viewModel.CanConfirm);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task LF09_PackPicker_CyclicOrDuplicatePage_TerminatesSafelyWithoutLooping()
    {
        var productId = Guid.NewGuid();
        var requestedQuantity = 20m;

        // Generate 5 items
        var repeatingItems = Enumerable.Range(1, 5).Select(i => new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: $"CYCLE-{i:D3}",
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 2m,
            ContinuousLength: 2m,
            UnitOfMeasure: "pcs",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null)).ToList();

        var pageRequests = 0;

        var handler = new DelegatingHandlerStub(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("/api/inventory/packs"))
            {
                pageRequests++;
                // Always returns the same items and TotalCount = 100
                var resp = new PackContainerQueryResponse(100, repeatingItems);
                var json = JsonSerializer.Serialize(resp);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);

        var viewModel = new PackPickerViewModel(
            workflowService,
            productId: productId,
            productName: "Cyclic Test Pack",
            requestedQuantity: requestedQuantity,
            unitOfMeasure: "pcs");

        await viewModel.LoadContainersAsync();

        // Proves cyclic/duplicate detection broke the loop without repeating up to maxPages
        Assert.Equal(5, viewModel.Containers.Count);
        // Page 1 added 5, Page 2 returned same 5 -> detected no new items -> terminated early
        Assert.True(pageRequests <= 3);
    }

    [Fact]
    public async Task LF09_PackPicker_NoEligiblePacksOnAnyPage_FailsClosedWithNullSelection()
    {
        var productId = Guid.NewGuid();
        var requestedQuantity = 50m;

        // 50 packs with only 1m each
        var page1Items = Enumerable.Range(1, 50).Select(i => new PackContainerDto(
            ContainerId: Guid.NewGuid(),
            TrackingCode: $"INELIGIBLE-{i:D3}",
            State: "Open",
            InitialQuantity: 10m,
            RemainingQuantity: 1m,
            ContinuousLength: 1m,
            UnitOfMeasure: "pcs",
            Condition: "Intact",
            ConcurrencyVersion: 1,
            UnitCost: null)).ToList();

        var handler = new DelegatingHandlerStub(req =>
        {
            var uri = req.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("/api/inventory/packs"))
            {
                if (uri.Contains("minContinuousLength=50"))
                {
                    // Targeted search also returns 0
                    var emptyResp = new PackContainerQueryResponse(0, new List<PackContainerDto>());
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(emptyResp), System.Text.Encoding.UTF8, "application/json")
                    });
                }

                var resp = new PackContainerQueryResponse(50, page1Items);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resp), System.Text.Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);

        var viewModel = new PackPickerViewModel(
            workflowService,
            productId: productId,
            productName: "Ineligible Test Pack",
            requestedQuantity: requestedQuantity,
            unitOfMeasure: "pcs");

        await viewModel.LoadContainersAsync();

        Assert.Equal(50, viewModel.Containers.Count);
        // Fail-closed: null selection, cannot confirm
        Assert.Null(viewModel.SelectedContainer);
        Assert.False(viewModel.CanConfirm);
        Assert.False(viewModel.IsLoading);
        Assert.Null(viewModel.StaleCapacityPrompt);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task LF09_PackPicker_RemoteHttpFailure_SetsErrorMessageAndFailsClosed()
    {
        var productId = Guid.NewGuid();

        var handler = new DelegatingHandlerStub(req =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Database unavailable")
            });
        });

        var apiClient = new DesktopApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5000") });
        var workflowService = new RemotePosWorkflowService(apiClient);

        var viewModel = new PackPickerViewModel(
            workflowService,
            productId: productId,
            productName: "Error Test Pack",
            requestedQuantity: 5m,
            unitOfMeasure: "pcs");

        await viewModel.LoadContainersAsync();

        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Contains("Failed to load pack containers", viewModel.ErrorMessage);
        Assert.Null(viewModel.SelectedContainer);
        Assert.False(viewModel.CanConfirm);
        Assert.False(viewModel.IsLoading);
    }

    #endregion

    #region Canonical LF14 Behavioral Tests (Desktop Thaka Reversals Presentation & STA Layout)

    [Fact]
    public void LF14_ReportThakaActivityItem_PresentationFormatting_RendersSeparateReversalDetails()
    {
        // 1. Project with both issues and reversals
        var itemWithReversals = new ReportThakaActivityItem
        {
            ProjectName = "Plaza Electrical Works",
            IssueCount = 2,
            MaterialValue = 15000m,
            ReversalCount = 1,
            ReversalAmount = 2000m,
            NetMaterialValue = 13000m
        };

        Assert.True(itemWithReversals.HasReversals);
        Assert.Equal("2 issues", itemWithReversals.IssueCountDisplay);
        Assert.Equal("Rs. 15,000.00", itemWithReversals.MaterialValueDisplay);
        Assert.Equal("1 reversal", itemWithReversals.ReversalCountDisplay);
        Assert.Equal("Rs. 2,000.00", itemWithReversals.ReversalAmountDisplay);
        Assert.Equal("Rs. 13,000.00", itemWithReversals.NetMaterialValueDisplay);
        Assert.Equal("2 issues, Rs. 15,000.00 issued · 1 reversal, Rs. 2,000.00 returned", itemWithReversals.ActivitySummaryDisplay);

        // 2. Reversal-only project (0 issues, 1 reversal)
        var reversalOnlyItem = new ReportThakaActivityItem
        {
            ProjectName = "Al-Rehman Tower Wiring",
            IssueCount = 0,
            MaterialValue = 0m,
            ReversalCount = 1,
            ReversalAmount = 5000m,
            NetMaterialValue = -5000m
        };

        Assert.True(reversalOnlyItem.HasReversals);
        Assert.Equal("0 issues", reversalOnlyItem.IssueCountDisplay);
        Assert.Equal("Rs. 0.00", reversalOnlyItem.MaterialValueDisplay);
        Assert.Equal("1 reversal", reversalOnlyItem.ReversalCountDisplay);
        Assert.Equal("Rs. 5,000.00", reversalOnlyItem.ReversalAmountDisplay);
        Assert.Equal("-Rs. 5,000.00", reversalOnlyItem.NetMaterialValueDisplay);
        Assert.Equal("0 issues, Rs. 0.00 issued · 1 reversal, Rs. 5,000.00 returned", reversalOnlyItem.ActivitySummaryDisplay);

        // 3. Issue-only project (no reversals)
        var normalItem = new ReportThakaActivityItem
        {
            ProjectName = "City Mall Lighting",
            IssueCount = 3,
            MaterialValue = 45000m,
            ReversalCount = 0,
            ReversalAmount = 0m,
            NetMaterialValue = 45000m
        };

        Assert.False(normalItem.HasReversals);
        Assert.Equal("3 issues", normalItem.IssueCountDisplay);
        Assert.Equal("Rs. 45,000.00", normalItem.MaterialValueDisplay);
        Assert.Equal("3 issues, Rs. 45,000.00 issued", normalItem.ActivitySummaryDisplay);
        Assert.Equal("Rs. 45,000.00", normalItem.NetMaterialValueDisplay);

        // 4. STA Layout proof: ReportsView renders card template with Thaka activity items
        OnSta(() =>
        {
            var snapshot = new ReportSnapshot
            {
                PeriodLabel = "19 Oct 2026",
                ProfitSeriesLabel = "Net Profit",
                ShowExpenseSeries = true,
                Trend = [],
                ExpenseBreakdown = [],
                ThakaActivity = [itemWithReversals, reversalOnlyItem, normalItem]
            };

            var view = new ReportsView();
            view.DataContext = new
            {
                Snapshot = snapshot,
                HasReportData = true,
                HasThakaActivity = true,
                ThakaActivity = snapshot.ThakaActivity,
                ThakaTotalDisplay = "Rs. 53,000.00"
            };

            Layout(view, 1024, 768);
            Assert.NotNull(view);

            // Deep Visual Tree Inspection: verify actual rendered TextBlock elements in visual/logical tree
            var textBlocks = FindDescendants<TextBlock>(view)
                .Select(tb => tb.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            // 1. Verify project headers rendered in card rows
            Assert.Contains("Plaza Electrical Works", textBlocks);
            Assert.Contains("Al-Rehman Tower Wiring", textBlocks);
            Assert.Contains("City Mall Lighting", textBlocks);

            // 2. Verify explicit issue and reversal summary breakdown rows (separate counts and amounts)
            Assert.Contains("2 issues, Rs. 15,000.00 issued · 1 reversal, Rs. 2,000.00 returned", textBlocks);
            Assert.Contains("0 issues, Rs. 0.00 issued · 1 reversal, Rs. 5,000.00 returned", textBlocks);
            Assert.Contains("3 issues, Rs. 45,000.00 issued", textBlocks);

            // 3. Verify explicit Net captions and numeric values
            Assert.Contains("Net", textBlocks);
            Assert.Contains("Rs. 13,000.00", textBlocks);
            Assert.Contains("-Rs. 5,000.00", textBlocks);
            Assert.Contains("Rs. 45,000.00", textBlocks);
        });
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T matchSelf)
        {
            yield return matchSelf;
        }

        if (root is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                foreach (var descendant in FindDescendants<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        foreach (var logicalChild in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var descendant in FindDescendants<T>(logicalChild))
            {
                yield return descendant;
            }
        }
    }

    #endregion

    #region Canonical LF01 and LF13 Desktop Behavioral Tests

    [Fact]
    public void LF01_DemoTransactionService_RejectsStoreCreditAndOtherUnapprovedMethods()
    {
        var service = DemoTransactionService.Instance;

        // 1. Rejects Store Credit
        var storeCreditReq = new RecordSaleReturnRequest
        {
            InvoiceNumber = "INV-0001",
            Disposition = SaleReturnDisposition.RestockSellable,
            RefundMethod = "Store Credit",
            TotalRefundAmount = 500m,
            Items = new[]
            {
                new SaleReturnItemRecord
                {
                    ProductId = "PRD-001",
                    Sku = "SKU-001",
                    Quantity = 1m,
                    RefundAmount = 500m
                }
            }
        };

        var ex1 = Assert.Throws<InvalidOperationException>(() => service.RecordReturn(storeCreditReq));
        Assert.Contains("Store credit is not an approved retail return method", ex1.Message);

        // 2. Rejects Other
        var otherReq = new RecordSaleReturnRequest
        {
            InvoiceNumber = "INV-0001",
            Disposition = SaleReturnDisposition.RestockSellable,
            RefundMethod = "Other",
            TotalRefundAmount = 500m,
            Items = new[]
            {
                new SaleReturnItemRecord
                {
                    ProductId = "PRD-001",
                    Sku = "SKU-001",
                    Quantity = 1m,
                    RefundAmount = 500m
                }
            }
        };

        var ex2 = Assert.Throws<InvalidOperationException>(() => service.RecordReturn(otherReq));
        Assert.Contains("Store credit is not an approved retail return method", ex2.Message);
    }

    [Fact]
    public void LF01_SalesReturnViewModel_BlocksProcessingAndDisplaysValidation_OnUnapprovedRefundMethods()
    {
        var sale = new SaleTransactionItemViewModel(
            1,
            DateTime.UtcNow,
            "Retail Customer",
            string.Empty,
            "Cashier 1",
            "Cash",
            [new SaleLineItemViewModel(1, Guid.NewGuid().ToString("D"), "Fan Regulator", "REG-01", "", 1500m, 1m)],
            invoiceDisplayOverride: "INV-9901");

        var returnVm = new SalesReturnViewModel(
            sale,
            transactionService: new CapturingTransactionService(),
            apiClient: null,
            dialogService: null,
            operationIntents: new FileClientOperationIntentStore(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"intent_{Guid.NewGuid():N}.json")));

        returnVm.ReturnItems[0].ReturnQuantity = 1m;
        Assert.True(returnVm.CanProcessReturn);
        Assert.Null(returnVm.ValidationMessage);

        // Setting unapproved refund method disables CanProcessReturn and displays explicit validation message
        returnVm.SetRefundMethod("Store Credit");
        Assert.False(returnVm.IsCashRefund);
        Assert.False(returnVm.IsBankRefund);
        Assert.False(returnVm.CanProcessReturn);
        Assert.NotNull(returnVm.ValidationMessage);
        Assert.Contains("Store credit is not an approved retail return method", returnVm.ValidationMessage);

        // Setting another unapproved method also blocks processing
        returnVm.SetRefundMethod("Other");
        Assert.False(returnVm.CanProcessReturn);
        Assert.Contains("Store credit is not an approved retail return method", returnVm.ValidationMessage);

        // Setting approved method restores CanProcessReturn
        returnVm.SetRefundMethod("Bank Refund");
        Assert.True(returnVm.IsBankRefund);
        Assert.True(returnVm.CanProcessReturn);
        Assert.Null(returnVm.ValidationMessage);

        returnVm.SetRefundMethod("Cash Refund");
        Assert.True(returnVm.IsCashRefund);
        Assert.True(returnVm.CanProcessReturn);
        Assert.Null(returnVm.ValidationMessage);
    }

    [Fact]
    public void LF13_DemoBusinessDirectoryService_EnforcesZeroCrossProjectNetting()
    {
        var directory = DemoBusinessDirectoryService.Instance;
        var retailState = DemoRetailState.Instance;

        var testCustomerName = $"Contractor Imran-{Guid.NewGuid():N}";
        var testPhone = "0333-9876543";

        var customerRecord = new CustomerDirectoryRecord
        {
            Id = $"CUST-{Guid.NewGuid():N}",
            Name = testCustomerName,
            Phone = testPhone,
            Address = "Plot 42 Industrial Area",
            IsActive = true
        };
        directory.Customers.Add(customerRecord);

        // Project 1: Positive due balance of Rs. 50 (Material 150 - Paid 100)
        var project1 = new ThakaProjectListItemViewModel(
            id: Guid.NewGuid().ToString("D"),
            projectName: "Mall Substation",
            customerName: testCustomerName,
            phone: testPhone,
            location: "Mall Site",
            startDate: DateTime.Now.AddDays(-20),
            materialValue: 150m,
            paid: 100m,
            status: "Active",
            notes: "Active project 1",
            settlementDiscount: 0m);

        // Project 2: Negative raw balance of Rs. -100 (Material 0 - Paid 100, excess advance)
        var project2 = new ThakaProjectListItemViewModel(
            id: Guid.NewGuid().ToString("D"),
            projectName: "Plaza Lift Wiring",
            customerName: testCustomerName,
            phone: testPhone,
            location: "Plaza Site",
            startDate: DateTime.Now.AddDays(-5),
            materialValue: 0m,
            paid: 100m,
            status: "Active",
            notes: "Active project 2",
            settlementDiscount: 0m);

        retailState.AddProject(project1);
        retailState.AddProject(project2);

        // Trigger metric refresh for customer
        directory.RefreshCustomerMetrics(customerRecord);

        Assert.Equal(2, customerRecord.ActiveThakaCount);

        // LF13 invariant: Project 1 balance = 50m. Project 2 balance = -100m clamped to Math.Max(0m, -100m) = 0m.
        // Total customer balance must be strictly 50m without cross-project netting (NOT -50m or 0m).
        Assert.Equal(50m, customerRecord.CurrentThakaBalance);
    }

    #endregion
}
