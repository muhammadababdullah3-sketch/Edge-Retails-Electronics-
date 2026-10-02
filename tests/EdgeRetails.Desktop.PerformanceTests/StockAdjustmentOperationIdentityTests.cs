using System.Collections.ObjectModel;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.UnitTests;

public sealed class StockAdjustmentOperationIdentityTests
{
    [Fact]
    public async Task SameAdjustmentIntent_ReusesOperationIdAfterLostResponse()
    {
        var service = new RetryCaptureStockAdjustmentService();
        var product = new PosProductItemViewModel(
            "P-1", "Test product", "SKU-1", "Brand", "General",
            10m, 100m, backendProductId: Guid.NewGuid(), backendProductUnitId: Guid.NewGuid());
        var viewModel = new StockAdjustmentViewModel(
            product,
            new TestToastService(),
            () => { },
            service);
        viewModel.QuantityText = "2";

        await viewModel.ApplyAsync();
        await viewModel.ApplyAsync();

        Assert.Equal(2, service.OperationIds.Count);
        Assert.NotEqual(Guid.Empty, service.OperationIds[0]);
        Assert.Equal(service.OperationIds[0], service.OperationIds[1]);
    }

    private sealed class RetryCaptureStockAdjustmentService : IBackendStockAdjustmentService
    {
        public List<Guid> OperationIds { get; } = [];

        public Task<Guid> CreateDeltaAdjustmentAsync(
            Guid productId,
            Guid? productUnitId,
            decimal quantity,
            bool increase,
            StockAdjustmentReason reason,
            string? note,
            Guid clientOperationId,
            CancellationToken cancellationToken = default)
        {
            OperationIds.Add(clientOperationId);
            if (OperationIds.Count == 1)
            {
                throw new DesktopApiException("network.timeout", "Simulated lost response.");
            }

            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class TestToastService : IToastService
    {
        private readonly ObservableCollection<ToastMessage> _items = [];
        public ReadOnlyObservableCollection<ToastMessage> Messages { get; }

        public TestToastService() => Messages = new ReadOnlyObservableCollection<ToastMessage>(_items);

        public void Show(string message, ToastTone tone = ToastTone.Neutral, TimeSpan? duration = null) { }
    }
}
